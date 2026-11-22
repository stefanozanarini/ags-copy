using System.Collections.Concurrent;
using System.Globalization;
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
    public int Threads { get; init; } = 4;
    public int Retries { get; init; } = 2;
    public int RetryWaitSeconds { get; init; } = 5;
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
            foreach (var e in store.List(dir))
            {
                if (dir.Length == 0 && isDestination && e.IsDirectory && e.Name.Equals(Versions.FolderName, StringComparison.OrdinalIgnoreCase)) continue;
                if (isDestination && !e.IsDirectory && e.Name.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                if (exclusions.Excludes(e.Relative, e.IsDirectory)) continue;
                all.Add(e);
                if (e.IsDirectory) pending.Push(e.Relative);
            }
        }
        return all;
    }

    /// <param name="openSource">Opens a source connection; called once per worker (SFTP sessions are not shared).</param>
    /// <param name="openTarget">Opens a destination connection, likewise.</param>
    public static async Task<SyncResult> RunAsync(SyncPlan plan, Func<ISourceStore> openSource, Func<ITargetStore> openTarget,
        SyncSettings settings, ProgressCounter progress, Action<string> log, CancellationToken ct)
    {
        var result = new SyncResult();
        var versions = settings.StartLocal.ToString(Versions.StampFormat, CultureInfo.InvariantCulture);
        var versionsRoot = Versions.FolderName + "/" + versions;
        var errors = new ConcurrentQueue<CopyError>();
        var archived = new ConcurrentQueue<VersionMove>();
        long copied = 0, bytes = 0, compressed = 0, failed = 0, archivedCount = 0;

        using (var target = openTarget())
        {
            if (!plan.DestinationExists) target.CreateDirectory("");
            foreach (var d in plan.Directories)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    target.CreateDirectory(d);
                }
                catch (Exception e) when (IsIo(e))
                {
                    errors.Enqueue(new CopyError(target.Display(d), Code(e), "Creating Directory", e.Message));
                }
            }
        }

        // Copies, in parallel, one connection pair per worker.
        var queue = new ConcurrentQueue<SyncCopy>(plan.Copies.OrderByDescending(c => c.Source.Size));
        var workers = Math.Clamp(settings.Threads, 1, Math.Max(1, plan.Copies.Count));
        var archiveDirs = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
        {
            using var source = openSource();
            using var target = openTarget();
            while (queue.TryDequeue(out var c))
            {
                ct.ThrowIfCancellationRequested();
                var ok = false;
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (c.Overwrite && settings.KeepVersions && !Archive(target, c.Target, VersionReason.Overwritten, false))
                            break; // never overwrite what could not be kept
                        Copy(source, target, c, ct);
                        ok = true;
                        break;
                    }
                    catch (Exception e) when (IsIo(e) && !ct.IsCancellationRequested)
                    {
                        if (attempt >= settings.Retries)
                        {
                            errors.Enqueue(new CopyError(source.Display(c.Source.Relative), Code(e), "Copying File", e.Message));
                            break;
                        }
                        log($"Retry {attempt + 1} after {settings.RetryWaitSeconds}s: {source.Display(c.Source.Relative)}: {e.Message}");
                        Task.Delay(TimeSpan.FromSeconds(settings.RetryWaitSeconds), ct).Wait(ct);
                    }
                }
                if (ok)
                {
                    Interlocked.Increment(ref copied);
                    Interlocked.Add(ref bytes, c.Source.Size);
                    if (c.Compressed) Interlocked.Increment(ref compressed);
                    progress.AddFile(c.Source.Size);
                    log($"{(c.Overwrite ? "Newer" : "New File")}\t{c.Source.Size}\t{source.Display(c.Source.Relative)}{(c.Compressed ? "\t→ " + Compression.Suffix : "")}");
                }
                else
                {
                    Interlocked.Increment(ref failed);
                }
            }
        }, ct))).ConfigureAwait(false);

        // Extras, after the copies: a failed copy never costs the old copy.
        using (var target = openTarget())
        {
            foreach (var x in plan.Extras)
            {
                if (x.Entry.IsDirectory) result.DirsExtra++;
                result.FilesExtra += x.Files;
                if (settings.Mode != CopyMode.Mirror) continue;
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (settings.KeepVersions)
                    {
                        Archive(target, x.Entry.Relative, VersionReason.Deleted, x.Entry.IsDirectory);
                    }
                    else
                    {
                        target.Delete(x.Entry.Relative, x.Entry.IsDirectory);
                        result.DeletedCount++;
                        if (result.Deleted.Count < RunReport.MaxListedItems) result.Deleted.Add(target.Display(x.Entry.Relative));
                        log($"*EXTRA {(x.Entry.IsDirectory ? "Dir" : "File")}\t{x.Bytes}\t{target.Display(x.Entry.Relative)}\tdeleted");
                    }
                }
                catch (Exception e) when (IsIo(e))
                {
                    errors.Enqueue(new CopyError(target.Display(x.Entry.Relative), Code(e), "Deleting Extra", e.Message));
                }
            }
        }

        result.FilesCopied = copied;
        result.BytesCopied = bytes;
        result.FilesCompressed = compressed;
        result.FilesFailed = failed;
        result.FilesSkipped = plan.Unchanged;
        result.ArchivedCount = archivedCount;
        result.Archived.AddRange(archived.Take(RunReport.MaxListedItems));
        result.Errors.AddRange(errors.Take(ErrorCollector.MaxErrors));
        if (archivedCount > 0) result.VersionsFolder = versionsRoot;
        return result;

        bool Archive(ITargetStore target, string relative, VersionReason reason, bool isDirectory)
        {
            var to = versionsRoot + "/" + relative;
            try
            {
                EnsureParent(target, to);
                target.Move(relative, to);
                Interlocked.Increment(ref archivedCount);
                if (archived.Count < RunReport.MaxListedItems) archived.Enqueue(new VersionMove(target.Display(relative), relative, isDirectory, reason));
                log($"Archived\t\t{target.Display(relative)}\t→ {target.Display(to)}");
                return true;
            }
            catch (Exception e) when (IsIo(e))
            {
                errors.Enqueue(new CopyError(target.Display(relative), Code(e), "→ " + Versions.FolderName, e.Message));
                return false;
            }
        }

        void EnsureParent(ITargetStore target, string relative)
        {
            var parent = SyncPath.Parent(relative);
            if (string.IsNullOrEmpty(parent) || !archiveDirs.TryAdd(parent, true)) return;
            // Create each level once; CreateDirectory is a no-op on folders that exist.
            var path = "";
            foreach (var part in parent.Split('/'))
            {
                path = SyncPath.Join(path, part);
                target.CreateDirectory(path);
            }
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
