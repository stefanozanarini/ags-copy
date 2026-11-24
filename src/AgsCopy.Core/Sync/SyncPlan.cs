using AgsCopy.Core.Models;

namespace AgsCopy.Core.Sync;

/// <summary>A source file to write into the destination.</summary>
public sealed record SyncCopy(SyncEntry Source, string Target, bool Compressed, bool Overwrite);

/// <summary>A destination item the source does not have.</summary>
public sealed record SyncExtra(SyncEntry Entry, long Files, long Bytes);

/// <summary>What a run will do: folders to create, files to write, extras (deleted by a mirror).</summary>
public sealed class SyncPlan
{
    public List<string> Directories { get; } = [];
    public List<SyncCopy> Copies { get; } = [];
    public List<SyncExtra> Extras { get; } = [];
    public long Unchanged { get; set; }
    public long UnchangedBytes { get; set; }
    public bool DestinationExists { get; set; }

    /// <summary>Modification times closer than this are equal: FAT and SFTP keep 1-2 second precision.</summary>
    public static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Compares the two trees. A compressible file is expected as name.gz when <paramref name="compress"/>
    /// is set; it is up to date when its time matches the source (its size differs by design).
    /// Plain files must also match in size. Extras inside an extra folder are counted on that folder.
    /// </summary>
    public static SyncPlan Build(IReadOnlyList<SyncEntry> source, IReadOnlyList<SyncEntry>? destination, bool compress)
    {
        var plan = new SyncPlan { DestinationExists = destination != null };
        var dest = new Dictionary<string, SyncEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in destination ?? []) dest[d.Relative] = d;
        var sourceNames = new HashSet<string>(source.Select(s => s.Relative), StringComparer.OrdinalIgnoreCase);
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in source.OrderBy(s => s.Relative, StringComparer.Ordinal))
        {
            if (s.IsDirectory)
            {
                expected.Add(s.Relative);
                if (!dest.TryGetValue(s.Relative, out var dd) || !dd.IsDirectory) plan.Directories.Add(s.Relative);
                continue;
            }
            var target = TargetOf(s, sourceNames, compress, out var gz);
            expected.Add(target);
            if (dest.TryGetValue(target, out var d) && UpToDate(s, d, gz))
            {
                plan.Unchanged++;
                plan.UnchangedBytes += s.Size;
                continue;
            }
            plan.Copies.Add(new SyncCopy(s, target, gz, d != null));
        }

        // Extras: the topmost destination items the source does not explain.
        var extraDirs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in (destination ?? []).OrderBy(d => d.Relative, StringComparer.Ordinal))
        {
            if (expected.Contains(d.Relative)) continue;
            if (ExtraAbove(d.Relative, extraDirs) is { } i)
            {
                if (!d.IsDirectory) plan.Extras[i] = plan.Extras[i] with { Files = plan.Extras[i].Files + 1, Bytes = plan.Extras[i].Bytes + d.Size };
                continue;
            }
            if (d.IsDirectory) extraDirs[d.Relative] = plan.Extras.Count;
            plan.Extras.Add(new SyncExtra(d, d.IsDirectory ? 0 : 1, d.IsDirectory ? 0 : d.Size));
        }
        return plan;
    }

    /// <summary>The extra folder an item sits in, looked up through its parents.</summary>
    private static int? ExtraAbove(string relative, Dictionary<string, int> extraDirs)
    {
        for (var p = SyncPath.Parent(relative); !string.IsNullOrEmpty(p); p = SyncPath.Parent(p))
            if (extraDirs.TryGetValue(p, out var i)) return i;
        return null;
    }

    /// <summary>Where a source file goes: a.txt is stored as a.txt.gz, unless the source also has an a.txt.gz.</summary>
    private static string TargetOf(SyncEntry s, ISet<string> sourceNames, bool compress, out bool gz)
    {
        gz = compress && Compression.IsCompressible(s.Name) && !sourceNames.Contains(s.Relative + Compression.Suffix);
        return gz ? s.Relative + Compression.Suffix : s.Relative;
    }

    private static bool UpToDate(SyncEntry s, SyncEntry d, bool gz) =>
        !d.IsDirectory && (d.ModifiedUtc - s.ModifiedUtc).Duration() <= TimeTolerance && (gz || d.Size == s.Size);

    /// <summary>
    /// One folder of both trees, compared as soon as it is read: what the streaming run copies from it,
    /// which subfolders it reads next and what of the destination the source does not explain.
    /// </summary>
    /// <param name="source">The items directly inside the folder in the source.</param>
    /// <param name="destination">The same folder in the destination (empty when it does not exist).</param>
    public static FolderComparison Compare(IReadOnlyList<SyncEntry> source, IReadOnlyList<SyncEntry> destination, bool compress)
    {
        var c = new FolderComparison();
        var dest = new Dictionary<string, SyncEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in destination) dest[d.Relative] = d;
        var sourceNames = new HashSet<string>(source.Select(s => s.Relative), StringComparer.OrdinalIgnoreCase);
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in source)
        {
            if (s.IsDirectory)
            {
                expected.Add(s.Relative);
                c.Folders.Add((s.Relative, dest.TryGetValue(s.Relative, out var dd) && dd.IsDirectory));
                continue;
            }
            var target = TargetOf(s, sourceNames, compress, out var gz);
            expected.Add(target);
            if (dest.TryGetValue(target, out var d) && UpToDate(s, d, gz))
            {
                c.Unchanged++;
                c.UnchangedBytes += s.Size;
                continue;
            }
            c.Copies.Add(new SyncCopy(s, target, gz, d != null));
        }
        c.Extras.AddRange(destination.Where(d => !expected.Contains(d.Relative)));
        return c;
    }
}

/// <summary>What <see cref="SyncPlan.Compare"/> found in one folder.</summary>
public sealed class FolderComparison
{
    /// <summary>The source's subfolders, and whether the destination already has each.</summary>
    public List<(string Relative, bool Exists)> Folders { get; } = [];
    public List<SyncCopy> Copies { get; } = [];
    /// <summary>Destination items directly inside the folder that the source does not have.</summary>
    public List<SyncEntry> Extras { get; } = [];
    public long Unchanged { get; set; }
    public long UnchangedBytes { get; set; }
}
