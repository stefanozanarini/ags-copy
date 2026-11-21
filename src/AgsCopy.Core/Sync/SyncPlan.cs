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

            // a.txt is stored as a.txt.gz, unless the source also has an a.txt.gz.
            var gz = compress && Compression.IsCompressible(s.Name) && !sourceNames.Contains(s.Relative + Compression.Suffix);
            var target = gz ? s.Relative + Compression.Suffix : s.Relative;
            expected.Add(target);

            if (dest.TryGetValue(target, out var d) && !d.IsDirectory
                && (d.ModifiedUtc - s.ModifiedUtc).Duration() <= TimeTolerance
                && (gz || d.Size == s.Size))
            {
                plan.Unchanged++;
                plan.UnchangedBytes += s.Size;
                continue;
            }
            plan.Copies.Add(new SyncCopy(s, target, gz, d != null));
        }

        // Extras: the topmost destination items the source does not explain.
        var extraDirs = new List<string>();
        foreach (var d in (destination ?? []).OrderBy(d => d.Relative, StringComparer.Ordinal))
        {
            if (expected.Contains(d.Relative)) continue;
            if (extraDirs.Any(x => d.Relative.StartsWith(x + "/", StringComparison.OrdinalIgnoreCase)))
            {
                var i = plan.Extras.FindIndex(e => d.Relative.StartsWith(e.Entry.Relative + "/", StringComparison.OrdinalIgnoreCase));
                if (i >= 0 && !d.IsDirectory) plan.Extras[i] = plan.Extras[i] with { Files = plan.Extras[i].Files + 1, Bytes = plan.Extras[i].Bytes + d.Size };
                continue;
            }
            if (d.IsDirectory) extraDirs.Add(d.Relative);
            plan.Extras.Add(new SyncExtra(d, d.IsDirectory ? 0 : 1, d.IsDirectory ? 0 : d.Size));
        }
        return plan;
    }
}
