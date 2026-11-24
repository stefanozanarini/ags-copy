using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using AgsCopy.Core.Models;
using AgsCopy.Core.Reports;
using AgsCopy.Core.Robocopy;

namespace AgsCopy.Core.Sync;

public sealed class SyncSettings
{
    public CopyMode Mode { get; init; }
    public bool Compress { get; init; }
    public bool KeepVersions { get; init; }
    public DateTime StartLocal { get; init; } = DateTime.Now;
    /// <summary>Copy threads, each with its own connections.</summary>
    public int Threads { get; init; } = 4;
    public int Retries { get; init; } = 2;
    public int RetryWaitSeconds { get; init; } = 5;

    /// <summary>Mirror: extras are deleted before the copies, to make room on a full destination.</summary>
    public bool ExtrasFirst { get; init; }
}

public sealed class SyncResult
{
    public long FilesCopied { get; set; }
    public long BytesCopied { get; set; }
    public long FilesCompressed { get; set; }
    public long FilesSkipped { get; set; }
    public long FilesFailed { get; set; }
    public long FilesExtra { get; set; }
    public long DirsExtra { get; set; }
    public List<CopyError> Errors { get; } = [];
    public List<VersionMove> Archived { get; } = [];
    public long ArchivedCount { get; set; }
    public string? VersionsFolder { get; set; }
    public List<string> Deleted { get; } = [];
    public long DeletedCount { get; set; }

    /// <summary>Streaming run: source folders read, and destination folders read rather than taken from the index.</summary>
    public long FoldersRead { get; set; }
    public long DestinationFoldersRead { get; set; }
    /// <summary>Streaming run: the whole destination was read, not taken from the index.</summary>
    public bool DestinationVerified { get; set; }
}

/// <summary>How a streaming run reads the two trees.</summary>
public sealed class SyncWalk
{
    public required Exclusions Exclusions { get; init; }

    /// <summary>Folders read at the same time, each thread with its own connections.</summary>
    public int Threads { get; init; } = 1;

    /// <summary>What the destination held after the last runs; null reads the destination and keeps no index.</summary>
    public DestinationIndex? Index { get; init; }

    /// <summary>Read every folder of the destination even where the index knows it, and rebuild the index.</summary>
    public bool Verify { get; init; }

    public SyncWalkProgress Progress { get; init; } = new();
}

/// <summary>How far a streaming run's walk is, for the activity window.</summary>
public sealed class SyncWalkProgress
{
    private long _folders, _files, _bytes;
    private string? _current;

    public long Folders => Interlocked.Read(ref _folders);
    public long FilesToCopy => Interlocked.Read(ref _files);
    public long BytesToCopy => Interlocked.Read(ref _bytes);
    /// <summary>A folder being read (with many threads, one of them).</summary>
    public string? Current => Volatile.Read(ref _current);
    public volatile bool Done;

    /// <summary>Told of every file the walk hands to the copy, to grow the run's totals.</summary>
    public Action<long>? OnPlanned { get; set; }

    internal void Folder(string path)
    {
        Interlocked.Increment(ref _folders);
        Volatile.Write(ref _current, path);
    }

    internal void Planned(long bytes)
    {
        Interlocked.Increment(ref _files);
        Interlocked.Add(ref _bytes, bytes);
        OnPlanned?.Invoke(bytes);
    }
}

/// <summary>
/// The copy engine used where robocopy cannot go: SFTP servers and compressed (.gz) copies.
/// Same rules as a robocopy run: the source is only read (it is reached through
/// <see cref="ISourceStore"/>), Copy never deletes, Mirror removes extras or moves them to the
/// versions folder, and files are written under a temporary name then moved in place, so an
/// interrupted run never leaves half a file under the real name.
/// </summary>
public static class SyncEngine
{
    public const string TempSuffix = ".agscopy-tmp";

    /// <summary>Every item below the root, skipping excluded ones (and, at the root, the versions folder).</summary>
    public static List<SyncEntry> Scan(ISourceStore store, Exclusions exclusions, bool isDestination, CancellationToken ct)
    {
        var all = new List<SyncEntry>();
        var pending = new Stack<string>();
        pending.Push("");
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            foreach (var e in Clean(store.List(dir), dir, isDestination))
            {
                if (exclusions.Excludes(e.Relative, e.IsDirectory)) continue;
                all.Add(e);
                if (e.IsDirectory) pending.Push(e.Relative);
            }
        }
        return all;
    }

    /// <summary>Like <see cref="Scan(ISourceStore, Exclusions, bool, CancellationToken)"/>, reading several folders at a time, each thread with its own connection.</summary>
    public static List<SyncEntry> Scan(Func<ISourceStore> open, int threads, Exclusions exclusions, bool isDestination, CancellationToken ct)
    {
        var all = new ConcurrentQueue<SyncEntry>();
        ParallelWalk.Run("", threads, open, (store, dir, add) =>
        {
            ct.ThrowIfCancellationRequested();
            foreach (var e in Clean(store.List(dir), dir, isDestination))
            {
                if (exclusions.Excludes(e.Relative, e.IsDirectory)) continue;
                all.Enqueue(e);
                if (e.IsDirectory) add(e.Relative);
            }
        }, ct);
        return [.. all];
    }

    /// <summary>Leaves out what the engine itself keeps in a destination: the versions folder at the root, files being written.</summary>
    private static IEnumerable<SyncEntry> Clean(IEnumerable<SyncEntry> items, string dir, bool isDestination) =>
        !isDestination ? items : items.Where(e =>
            !(dir.Length == 0 && e.IsDirectory && e.Name.Equals(Versions.FolderName, StringComparison.OrdinalIgnoreCase))
            && !(!e.IsDirectory && e.Name.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Copies a plan made beforehand (the whole of both trees scanned).</summary>
    /// <param name="openSource">Opens a source connection; called once per worker (SFTP sessions are not shared).</param>
    /// <param name="openTarget">Opens a destination connection, likewise.</param>
    public static async Task<SyncResult> RunAsync(SyncPlan plan, Func<ISourceStore> openSource, Func<ITargetStore> openTarget,
        SyncSettings settings, ProgressCounter progress, Action<string> log, CancellationToken ct)
    {
        var run = new Runner(openSource, openTarget, settings, progress, log, null, ct);

        // On a full destination the extras go first; otherwise after, so a failed copy never costs the old copy.
        if (settings.ExtrasFirst) run.HandleExtras(plan.Extras);

        using (var target = openTarget())
        {
            if (!plan.DestinationExists) target.CreateDirectory("");
            foreach (var d in plan.Directories)
            {
                ct.ThrowIfCancellationRequested();
                run.CreateDirectory(target, d);
            }
        }

        // Copies, in parallel, one connection pair per worker.
        var queue = Channel.CreateUnbounded<SyncCopy>();
        foreach (var c in plan.Copies.OrderByDescending(c => c.Source.Size)) queue.Writer.TryWrite(c);
        queue.Writer.Complete();
        await run.CopyAsync(queue.Reader, Math.Clamp(settings.Threads, 1, Math.Max(1, plan.Copies.Count))).ConfigureAwait(false);

        if (!settings.ExtrasFirst) run.HandleExtras(plan.Extras);
        run.Result.FilesSkipped = plan.Unchanged;
        return run.Finish();
    }

    /// <summary>Copies queued at most while the walk runs ahead of the copy (about 300 bytes each).</summary>
    private const int MaxQueued = 100_000;

    /// <summary>
    /// Reads both trees folder by folder and copies while it reads: each folder is compared as soon as
    /// both sides are listed and what it has to copy goes to the copy threads at once. The destination
    /// is read from <see cref="SyncWalk.Index"/> where the index knows the folder (unless the run checks
    /// it in full), and the index is kept up to date with what the run writes and deletes.
    /// With <see cref="SyncSettings.ExtrasFirst"/> the copies wait for the walk, which deleted the extras.
    /// </summary>
    public static async Task<SyncResult> RunAsync(SyncWalk walk, Func<ISourceStore> openSource, Func<ITargetStore> openTarget,
        SyncSettings settings, ProgressCounter progress, Action<string> log, CancellationToken ct)
    {
        var index = walk.Index;
        var extras = new ConcurrentQueue<SyncExtra>();
        var visited = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var queue = settings.ExtrasFirst ? Channel.CreateUnbounded<SyncCopy>() : Channel.CreateBounded<SyncCopy>(MaxQueued);
        long unchanged = 0, destinationRead = 0, listErrors = 0;

        var verify = index == null || walk.Verify;
        bool rootExists;
        using (var target = openTarget())
        {
            rootExists = target.Exists();
            if (!rootExists) target.CreateDirectory("");
            else if (!verify && !SameRoot(target))
            {
                // Something changed the destination behind the index (emptied, another disk): check it all.
                verify = true;
                log("Destination index out of date: reading the whole destination");
            }
        }
        var run = new Runner(openSource, openTarget, settings, progress, log, index, ct, trusted: !verify);
        var result = run.Result;
        result.DestinationVerified = verify;

        var walking = Task.Factory.StartNew(() =>
        {
            try
            {
                ParallelWalk.Run(("", !rootExists), walk.Threads, () => new StorePair(openSource(), openTarget()), Visit, ct);
            }
            finally
            {
                queue.Writer.TryComplete();
                walk.Progress.Done = true;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        if (settings.ExtrasFirst)
        {
            await walking.ConfigureAwait(false);
            run.HandleExtras(extras);
            await run.CopyAsync(queue.Reader, settings.Threads).ConfigureAwait(false);
        }
        else
        {
            var copying = run.CopyAsync(queue.Reader, settings.Threads);
            try
            {
                await walking.ConfigureAwait(false);
            }
            finally
            {
                // A walk that stopped half-way: the copies already queued still finish before the error is told.
                await copying.ConfigureAwait(false);
            }
            run.HandleExtras(extras);
        }

        result.FilesSkipped = unchanged;
        result.FoldersRead = walk.Progress.Folders;
        result.DestinationFoldersRead = destinationRead;
        // Only a walk that saw every folder can tell which ones the index should forget.
        if (index != null && listErrors == 0 && !ct.IsCancellationRequested)
        {
            index.Retain(visited.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
            if (verify) index.VerifiedUtc = DateTime.UtcNow;
        }
        return run.Finish();

        bool SameRoot(ITargetStore target)
        {
            if (index!.Folder("") is not { } known) return false;
            var now = Clean(target.List(""), "", true).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return now.SetEquals(known.Select(e => e.Name));
        }

        IReadOnlyList<SyncEntry> ReadDestination(ITargetStore target, string dir)
        {
            visited[dir] = true;
            if (!verify && index?.Folder(dir) is { } known) return known;
            var items = Clean(target.List(dir), dir, true).ToList();
            Interlocked.Increment(ref destinationRead);
            index?.SetFolder(dir, items);
            return items;
        }

        void Visit(StorePair pair, (string Dir, bool New) folder, Action<(string, bool)> add)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, isNew) = folder;
            walk.Progress.Folder(pair.Source.Display(dir));
            IReadOnlyList<SyncEntry> source, destination;
            try
            {
                source = pair.Source.List(dir).Where(e => !walk.Exclusions.Excludes(e.Relative, e.IsDirectory)).ToList();
            }
            catch (Exception e) when (IsIo(e) && dir.Length > 0)
            {
                Interlocked.Increment(ref listErrors);
                run.Error(new CopyError(pair.Source.Display(dir), Code(e), "Listing Directory", e.Message));
                return;
            }
            try
            {
                if (isNew)
                {
                    visited[dir] = true;
                    index?.SetFolder(dir, []);
                    destination = [];
                }
                else
                {
                    destination = ReadDestination(pair.Target, dir).Where(e => !walk.Exclusions.Excludes(e.Relative, e.IsDirectory)).ToList();
                }
            }
            catch (Exception e) when (IsIo(e) && dir.Length > 0)
            {
                // Without the destination's side nothing in this folder can be told apart: it waits for the next run.
                Interlocked.Increment(ref listErrors);
                run.Error(new CopyError(pair.Target.Display(dir), Code(e), "Listing Directory", e.Message));
                return;
            }

            var c = SyncPlan.Compare(source, destination, settings.Compress);
            Interlocked.Add(ref unchanged, c.Unchanged);
            foreach (var x in c.Extras)
            {
                var (files, bytes) = x.IsDirectory ? CountTree(pair.Target, x.Relative) : (1, x.Size);
                extras.Enqueue(new SyncExtra(x, files, bytes));
            }
            foreach (var (sub, exists) in c.Folders)
            {
                if (!exists && !run.CreateDirectory(pair.Target, sub)) continue;
                add((sub, !exists));
            }
            foreach (var copy in c.Copies)
            {
                walk.Progress.Planned(copy.Source.Size);
                queue.Writer.WriteAsync(copy, ct).AsTask().GetAwaiter().GetResult();
            }
        }

        // What an extra folder holds, for the report (a mirror deletes all of it).
        (long Files, long Bytes) CountTree(ITargetStore target, string dir)
        {
            long files = 0, bytes = 0;
            var pending = new Stack<string>();
            pending.Push(dir);
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var d = pending.Pop();
                IReadOnlyList<SyncEntry> items;
                try
                {
                    items = ReadDestination(target, d);
                }
                catch (Exception e) when (IsIo(e))
                {
                    continue; // counted short; the delete reports what it cannot reach
                }
                foreach (var e in items.Where(e => !walk.Exclusions.Excludes(e.Relative, e.IsDirectory)))
                {
                    if (e.IsDirectory) pending.Push(e.Relative);
                    else
                    {
                        files++;
                        bytes += e.Size;
                    }
                }
            }
            return (files, bytes);
        }
    }

    private sealed class StorePair(ISourceStore source, ITargetStore target) : IDisposable
    {
        public ISourceStore Source { get; } = source;
        public ITargetStore Target { get; } = target;

        public void Dispose()
        {
            Source.Dispose();
            Target.Dispose();
        }
    }

    /// <summary>One run's copies, versions and extras, shared by the planned and the streaming run.</summary>
    /// <param name="trusted">The destination is read from the index, which may miss a file put there since.</param>
    private sealed class Runner(Func<ISourceStore> openSource, Func<ITargetStore> openTarget, SyncSettings settings, ProgressCounter progress,
        Action<string> log, DestinationIndex? index, CancellationToken ct, bool trusted = false)
    {
        private readonly string _versionsRoot = Versions.FolderName + "/" + settings.StartLocal.ToString(Versions.StampFormat, CultureInfo.InvariantCulture);
        private readonly ConcurrentQueue<CopyError> _errors = new();
        private readonly ConcurrentQueue<VersionMove> _archived = new();
        private readonly ConcurrentDictionary<string, bool> _archiveDirs = new(StringComparer.OrdinalIgnoreCase);
        private long _copied, _bytes, _compressed, _failed, _archivedCount;

        public SyncResult Result { get; } = new();

        public void Error(CopyError e) => _errors.Enqueue(e);

        public bool CreateDirectory(ITargetStore target, string relative)
        {
            try
            {
                target.CreateDirectory(relative);
                index?.Put(new SyncEntry(relative, true, 0, default));
                return true;
            }
            catch (Exception e) when (IsIo(e))
            {
                _errors.Enqueue(new CopyError(target.Display(relative), Code(e), "Creating Directory", e.Message));
                return false;
            }
        }

        /// <summary>Copies what the queue holds until it is completed, on <paramref name="threads"/> connection pairs.</summary>
        public Task CopyAsync(ChannelReader<SyncCopy> queue, int threads) =>
            Task.WhenAll(Enumerable.Range(0, Math.Max(1, threads)).Select(_ => Task.Factory.StartNew(() =>
            {
                ISourceStore? source = null;
                ITargetStore? target = null;
                try
                {
                    while (queue.WaitToReadAsync(ct).AsTask().GetAwaiter().GetResult())
                    {
                        while (queue.TryRead(out var c))
                        {
                            ct.ThrowIfCancellationRequested();
                            // Connections are opened by the threads that get work, not before.
                            source ??= openSource();
                            target ??= openTarget();
                            CopyOne(source, target, c);
                        }
                    }
                }
                finally
                {
                    source?.Dispose();
                    target?.Dispose();
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)));

        private void CopyOne(ISourceStore source, ITargetStore target, SyncCopy c)
        {
            var ok = false;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    // A file the index does not know is still kept as a version before it is replaced.
                    var overwrite = c.Overwrite || (trusted && settings.KeepVersions && target.FileExists(c.Target));
                    if (overwrite && settings.KeepVersions && !Archive(target, c.Target, VersionReason.Overwritten, false))
                        break; // never overwrite what could not be kept
                    Copy(source, target, c, ct);
                    ok = true;
                    break;
                }
                catch (Exception e) when (IsIo(e) && !ct.IsCancellationRequested)
                {
                    if (attempt >= settings.Retries)
                    {
                        _errors.Enqueue(new CopyError(source.Display(c.Source.Relative), Code(e), "Copying File", e.Message));
                        break;
                    }
                    log($"Retry {attempt + 1} after {settings.RetryWaitSeconds}s: {source.Display(c.Source.Relative)}: {e.Message}");
                    Task.Delay(TimeSpan.FromSeconds(settings.RetryWaitSeconds), ct).Wait(ct);
                }
            }
            if (ok)
            {
                Interlocked.Increment(ref _copied);
                Interlocked.Add(ref _bytes, c.Source.Size);
                if (c.Compressed) Interlocked.Increment(ref _compressed);
                index?.Put(new SyncEntry(c.Target, false, c.Source.Size, c.Source.ModifiedUtc));
                progress.AddFile(c.Source.Size);
                log($"{(c.Overwrite ? "Newer" : "New File")}\t{c.Source.Size}\t{source.Display(c.Source.Relative)}{(c.Compressed ? "\t→ " + Compression.Suffix : "")}");
            }
            else
            {
                Interlocked.Increment(ref _failed);
            }
        }

        public void HandleExtras(IEnumerable<SyncExtra> extras)
        {
            using var target = openTarget();
            foreach (var x in extras)
            {
                if (x.Entry.IsDirectory) Result.DirsExtra++;
                Result.FilesExtra += x.Files;
                if (settings.Mode != CopyMode.Mirror) continue;
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (settings.KeepVersions)
                    {
                        if (!Archive(target, x.Entry.Relative, VersionReason.Deleted, x.Entry.IsDirectory)) continue;
                    }
                    else
                    {
                        target.Delete(x.Entry.Relative, x.Entry.IsDirectory);
                        Result.DeletedCount++;
                        if (Result.Deleted.Count < RunReport.MaxListedItems) Result.Deleted.Add(target.Display(x.Entry.Relative));
                        log($"*EXTRA {(x.Entry.IsDirectory ? "Dir" : "File")}\t{x.Bytes}\t{target.Display(x.Entry.Relative)}\tdeleted");
                    }
                    index?.Remove(x.Entry.Relative, x.Entry.IsDirectory);
                }
                catch (Exception e) when (IsIo(e))
                {
                    _errors.Enqueue(new CopyError(target.Display(x.Entry.Relative), Code(e), "Deleting Extra", e.Message));
                }
            }
        }

        private bool Archive(ITargetStore target, string relative, VersionReason reason, bool isDirectory)
        {
            var to = _versionsRoot + "/" + relative;
            try
            {
                EnsureParent(target, to);
                target.Move(relative, to);
                Interlocked.Increment(ref _archivedCount);
                if (_archived.Count < RunReport.MaxListedItems) _archived.Enqueue(new VersionMove(target.Display(relative), relative, isDirectory, reason));
                log($"Archived\t\t{target.Display(relative)}\t→ {target.Display(to)}");
                return true;
            }
            catch (Exception e) when (IsIo(e))
            {
                _errors.Enqueue(new CopyError(target.Display(relative), Code(e), "→ " + Versions.FolderName, e.Message));
                return false;
            }
        }

        private void EnsureParent(ITargetStore target, string relative)
        {
            var parent = SyncPath.Parent(relative);
            if (string.IsNullOrEmpty(parent) || !_archiveDirs.TryAdd(parent, true)) return;
            // Create each level once; CreateDirectory is a no-op on folders that exist.
            var path = "";
            foreach (var part in parent.Split('/'))
            {
                path = SyncPath.Join(path, part);
                target.CreateDirectory(path);
            }
        }

        public SyncResult Finish()
        {
            Result.FilesCopied = _copied;
            Result.BytesCopied = _bytes;
            Result.FilesCompressed = _compressed;
            Result.FilesFailed = _failed;
            Result.ArchivedCount = _archivedCount;
            Result.Archived.AddRange(_archived.Take(RunReport.MaxListedItems));
            Result.Errors.AddRange(_errors.Take(ErrorCollector.MaxErrors));
            if (_archivedCount > 0) Result.VersionsFolder = _versionsRoot;
            return Result;
        }
    }

    private static void Copy(ISourceStore source, ITargetStore target, SyncCopy c, CancellationToken ct)
    {
        var temp = c.Target + TempSuffix;
        try
        {
            using (var input = source.OpenRead(c.Source.Relative))
            using (var output = target.Create(temp))
            {
                if (c.Compressed)
                {
                    using var gzip = AgsGzip.Create(output);
                    input.CopyToAsync(gzip, 1 << 16, ct).GetAwaiter().GetResult();
                }
                else
                {
                    input.CopyToAsync(output, 1 << 16, ct).GetAwaiter().GetResult();
                }
            }
            target.Replace(temp, c.Target);
            target.SetModified(c.Target, c.Source.ModifiedUtc);
        }
        catch
        {
            try { target.Delete(temp, false); } catch (Exception e) when (IsIo(e)) { }
            throw;
        }
    }

    /// <summary>Deletes version folders older than the retention; returns how many.</summary>
    public static int RemoveExpiredVersions(ITargetStore target, int retentionDays, Action<string> log)
    {
        IReadOnlyList<SyncEntry> folders;
        try
        {
            folders = target.List(Versions.FolderName);
        }
        catch (Exception e) when (IsIo(e))
        {
            return 0; // no versions yet
        }
        var removed = 0;
        foreach (var name in Versions.Expired(folders.Where(f => f.IsDirectory).Select(f => f.Name), DateTime.Now, retentionDays))
        {
            try
            {
                target.Delete(Versions.FolderName + "/" + name, true);
                removed++;
            }
            catch (Exception e) when (IsIo(e))
            {
                log($"Cannot delete old versions {name}: {e.Message}");
            }
        }
        return removed;
    }

    /// <summary>Errors of a single item, which the run reports and goes on from (SFTP errors included).</summary>
    public static bool IsIo(Exception e) =>
        e is IOException or UnauthorizedAccessException || e.GetType().Namespace?.StartsWith("Renci.SshNet", StringComparison.Ordinal) == true;

    private static int Code(Exception e) => e is UnauthorizedAccessException ? 5 : e.HResult & 0xFFFF;
}
