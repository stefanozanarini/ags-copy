using System.Globalization;
using System.IO.Compression;
using AgsCopy.Core.Robocopy;

namespace AgsCopy.Core.Sync;

/// <summary>A moment the backup can go back to: the current copy, or just before a run that kept versions.</summary>
public sealed record RestorePoint(string? Stamp, DateTime? Local)
{
    public static readonly RestorePoint Current = new(null, null);
}

/// <summary>A file or folder as it was at a restore point, and where its copy lives in the backup.</summary>
public sealed record RestoreItem(string Relative, string Stored, bool IsDirectory, long Size, DateTime ModifiedUtc, string? Version)
{
    public string Name => Relative[(Relative.LastIndexOf('/') + 1)..];
}

public sealed class RestoreResult
{
    public long Files { get; set; }
    public long Bytes { get; set; }
    public long Skipped { get; set; }
    public List<CopyError> Errors { get; } = [];
}

/// <summary>
/// Rebuilds the backup as it was at a restore point. The versions folder of the run at stamp S holds
/// what that run overwrote or deleted, i.e. the files as they were before S. So the state before S
/// is, for each item, its copy in the earliest version folder from S on, otherwise the current copy.
/// Only reads the backup; writes only into the restore folder.
/// </summary>
public sealed class RestoreBrowser(ISourceStore backup)
{
    /// <summary>The current copy first, then the version folders, newest first.</summary>
    public List<RestorePoint> Points()
    {
        var points = new List<RestorePoint> { RestorePoint.Current };
        IReadOnlyList<SyncEntry> folders;
        try
        {
            folders = backup.List(Versions.FolderName);
        }
        catch (Exception e) when (SyncEngine.IsIo(e))
        {
            return points;
        }
        foreach (var f in folders.Where(f => f.IsDirectory).OrderByDescending(f => f.Name, StringComparer.Ordinal))
            if (DateTime.TryParseExact(f.Name, Versions.StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                points.Add(new RestorePoint(f.Name, t));
        return points;
    }

    /// <summary>Items directly inside <paramref name="relative"/> at the given point.</summary>
    public List<RestoreItem> List(RestorePoint point, string relative)
    {
        var result = new Dictionary<string, RestoreItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var (prefix, version) in Layers(point))
        {
            IReadOnlyList<SyncEntry> entries;
            try
            {
                entries = backup.List(SyncPath.Join(prefix, relative).Trim('/'));
            }
            catch (Exception e) when (SyncEngine.IsIo(e))
            {
                continue; // this layer has nothing there
            }
            foreach (var e in entries)
            {
                if (version == null && relative.Length == 0 && e.IsDirectory && e.Name.Equals(Versions.FolderName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!e.IsDirectory && e.Name.EndsWith(SyncEngine.TempSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                var stored = e.Relative; // already below the layer's folder
                var name = DisplayName(e, stored);
                if (result.ContainsKey(name)) continue; // an earlier layer has it as it was then
                result[name] = new RestoreItem(SyncPath.Join(relative, name), stored, e.IsDirectory, e.Size, e.ModifiedUtc, version);
            }
        }
        return result.Values
            .OrderBy(i => !i.IsDirectory).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Version folders from the point on (oldest first), then the current copy.</summary>
    private IEnumerable<(string Prefix, string? Version)> Layers(RestorePoint point)
    {
        if (point.Stamp != null)
            foreach (var p in Points().Where(p => p.Stamp != null && string.CompareOrdinal(p.Stamp, point.Stamp) >= 0).OrderBy(p => p.Stamp, StringComparer.Ordinal))
                yield return (Versions.FolderName + "/" + p.Stamp, p.Stamp);
        yield return ("", null);
    }

    /// <summary>
    /// a.txt.gz written by the engine shows (and comes back) as a.txt; a .gz that was already in the
    /// source keeps its name. Only the gzip header tells them apart, so it is read for .gz files.
    /// </summary>
    private string DisplayName(SyncEntry e, string stored)
    {
        if (e.IsDirectory || !e.Name.EndsWith(Compression.Suffix, StringComparison.OrdinalIgnoreCase)) return e.Name;
        var original = e.Name[..^Compression.Suffix.Length];
        return Compression.IsCompressible(original) && IsMarked(stored) ? original : e.Name;
    }

    /// <summary>Files of the selected items (folders expanded), as they were at the point.</summary>
    public IEnumerable<RestoreItem> Expand(RestorePoint point, IEnumerable<string> selected, CancellationToken ct)
    {
        foreach (var path in selected.Select(p => p.Replace('\\', '/').Trim('/')).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var parent = SyncPath.Parent(path) ?? "";
            var item = List(point, parent).FirstOrDefault(i => i.Relative.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (item == null) continue;
            if (!item.IsDirectory)
            {
                yield return item;
                continue;
            }
            yield return item; // the folder itself, so empty folders come back too
            var pending = new Stack<string>();
            pending.Push(item.Relative);
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var child in List(point, pending.Pop()))
                {
                    yield return child;
                    if (child.IsDirectory) pending.Push(child.Relative);
                }
            }
        }
    }

    /// <summary>
    /// Writes the items under <paramref name="targetFolder"/> with their relative paths, original
    /// times, and AGS-Copy .gz files unpacked. Existing files are kept unless <paramref name="overwrite"/>.
    /// </summary>
    public RestoreResult Restore(RestorePoint point, IEnumerable<string> selected, string targetFolder, bool overwrite,
        ProgressCounter progress, Action<string> log, CancellationToken ct) =>
        Restore(Expand(point, selected, ct).ToList(), targetFolder, overwrite, progress, log, ct);

    /// <summary>Restores items already expanded with <see cref="Expand"/> (whose total the caller may show).</summary>
    public RestoreResult Restore(IReadOnlyList<RestoreItem> items, string targetFolder, bool overwrite,
        ProgressCounter progress, Action<string> log, CancellationToken ct)
    {
        var result = new RestoreResult();
        Directory.CreateDirectory(targetFolder);
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(targetFolder, item.Relative.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (item.IsDirectory)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                if (File.Exists(target) && !overwrite)
                {
                    result.Skipped++;
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temp = target + SyncEngine.TempSuffix;
                var unpack = item.Stored.EndsWith(Compression.Suffix, StringComparison.OrdinalIgnoreCase) && IsMarked(item.Stored);
                using (var input = backup.OpenRead(item.Stored))
                using (var source = unpack ? new GZipStream(input, CompressionMode.Decompress) : input)
                using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    source.CopyTo(output, 1 << 16);
                }
                var info = new FileInfo(target);
                if (info.Exists && info.IsReadOnly) info.IsReadOnly = false;
                File.Move(temp, target, overwrite: true);
                File.SetLastWriteTimeUtc(target, item.ModifiedUtc);
                var size = new FileInfo(target).Length;
                result.Files++;
                result.Bytes += size;
                progress.AddFile(size);
                log($"Restored\t{size}\t{backup.Display(item.Stored)}\t→ {target}");
            }
            catch (Exception e) when (SyncEngine.IsIo(e) || e is InvalidDataException)
            {
                try { File.Delete(target + SyncEngine.TempSuffix); } catch (Exception d) when (SyncEngine.IsIo(d)) { }
                if (result.Errors.Count < ErrorCollector.MaxErrors)
                    result.Errors.Add(new CopyError(backup.Display(item.Stored), e is UnauthorizedAccessException ? 5 : e.HResult & 0xFFFF, "Restoring", e.Message));
            }
        }
        return result;
    }

    private readonly Dictionary<string, bool> _marked = new(StringComparer.Ordinal);

    private bool IsMarked(string stored)
    {
        lock (_marked)
        {
            if (_marked.TryGetValue(stored, out var known)) return known;
        }
        bool marked;
        try
        {
            using var s = backup.OpenRead(stored);
            marked = AgsGzip.IsMarked(s);
        }
        catch (Exception e) when (SyncEngine.IsIo(e))
        {
            marked = false;
        }
        lock (_marked) _marked[stored] = marked;
        return marked;
    }
}

/// <summary>
/// The backup of a job with several sources, seen as one: each source has its folder in the
/// destination, with its own versions folder inside (Dest\F\_AGS-Copy-Versions\stamp\...). This view
/// shows them the way <see cref="RestoreBrowser"/> reads a single backup
/// (_AGS-Copy-Versions\stamp\F\...), so a restore point covers every source. Only reads.
/// </summary>
public sealed class SourceFoldersBackup(ISourceStore backup, IReadOnlyList<string> folders) : ISourceStore
{
    private readonly List<string> _folders = folders.Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private string? Folder(string name) => _folders.FirstOrDefault(f => f.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The real path of a path of this view; null for the view's own folders.</summary>
    private string? Real(string relative)
    {
        var parts = relative.Replace('\\', '/').Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        if (parts[0].Equals(Versions.FolderName, StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length < 3 || Folder(parts[2]) is not { } f) return null;
            return string.Join('/', new[] { f, Versions.FolderName, parts[1] }.Concat(parts[3..]));
        }
        return Folder(parts[0]) is { } folder ? string.Join('/', new[] { folder }.Concat(parts[1..])) : throw new DirectoryNotFoundException(relative);
    }

    public string Display(string relative) => backup.Display(Real(relative) ?? relative);

    public bool Exists() => backup.Exists();

    public IReadOnlyList<SyncEntry> List(string relative)
    {
        var r = relative.Replace('\\', '/').Trim('/');
        var parts = r.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            var list = backup.List("").Where(e => e.IsDirectory && Folder(e.Name) != null).ToList();
            if (Stamps().Count > 0) list.Add(new SyncEntry(Versions.FolderName, true, 0, default));
            return list;
        }
        if (parts[0].Equals(Versions.FolderName, StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length == 1)
                return Stamps().Select(s => new SyncEntry(Versions.FolderName + "/" + s, true, 0, default)).ToList();
            if (parts.Length == 2)
                return _folders.Where(f => HasStamp(f, parts[1])).Select(f => new SyncEntry(r + "/" + f, true, 0, default)).ToList();
        }
        var real = Real(r)!;
        return backup.List(real).Select(e => e with { Relative = r + e.Relative[real.Length..] }).ToList();
    }

    public Stream OpenRead(string relative) => backup.OpenRead(Real(relative) ?? throw new FileNotFoundException(relative));

    private List<string> Stamps() =>
        _folders.SelectMany(f => Try(() => backup.List(f + "/" + Versions.FolderName)).Where(e => e.IsDirectory).Select(e => e.Name))
            .Distinct(StringComparer.Ordinal).ToList();

    private bool HasStamp(string folder, string stamp) =>
        Try(() => backup.List(folder + "/" + Versions.FolderName)).Any(e => e.IsDirectory && e.Name.Equals(stamp, StringComparison.Ordinal));

    private static IReadOnlyList<SyncEntry> Try(Func<IReadOnlyList<SyncEntry>> list)
    {
        try
        {
            return list();
        }
        catch (Exception e) when (SyncEngine.IsIo(e))
        {
            return [];
        }
    }

    public void Dispose() => backup.Dispose();
}
