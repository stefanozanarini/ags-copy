using AgsCopy.Core.Models;

namespace AgsCopy.Core;

/// <summary>One item of a folder listing, as returned by the service.</summary>
public sealed class PathEntry
{
    public string Name { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime ModifiedUtc { get; set; }
}

/// <summary>What happens to an item of the destination folder.</summary>
public enum LayoutFate
{
    /// <summary>Copied from the source; not there yet.</summary>
    New,
    /// <summary>Already there with the same name: overwritten when the source copy is different.</summary>
    Update,
    /// <summary>Only in the destination, left alone.</summary>
    Kept,
    /// <summary>Only in the destination, deleted by a mirror.</summary>
    Deleted,
    /// <summary>Only in the destination, moved to the versions folder by a mirror.</summary>
    Archived,
    /// <summary>Excluded by the job: not copied, not deleted.</summary>
    Excluded,
}

public sealed record LayoutItem(string Name, bool IsDirectory, LayoutFate Fate, long Size);

public sealed class LayoutResult
{
    public List<LayoutItem> Items { get; } = [];
    public List<SummaryLine> Notes { get; } = [];

    /// <summary>Destination that keeps the source folder's own name, offered when it may be what the user meant.</summary>
    public string? Suggestion { get; set; }
    public bool SuggestionRecommended { get; set; }
}

/// <summary>
/// Shows how the destination will look after the copy, one folder level at a time, and warns about
/// the usual mistakes: robocopy copies the source folder's content, not the folder itself, so
/// C:\Data → E:\Backup puts the files in E:\Backup, not in E:\Backup\Data.
/// </summary>
public static class DestinationLayout
{
    /// <param name="relative">Folder being shown, relative to source and destination ("" = top level).</param>
    /// <param name="destination">Destination listing; null when that folder does not exist yet.</param>
    public static LayoutResult Build(CopyJob job, string relative, IReadOnlyList<PathEntry> source, IReadOnlyList<PathEntry>? destination)
    {
        var r = new LayoutResult();
        var dest = destination ?? [];
        var inSource = source.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        var inDest = dest.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        var top = relative.Length == 0;

        foreach (var s in source)
        {
            var fate = Excluded(job, s) ? LayoutFate.Excluded
                : inDest.ContainsKey(s.Name) ? LayoutFate.Update
                : LayoutFate.New;
            r.Items.Add(new LayoutItem(s.Name, s.IsDirectory, fate, s.Size));
        }
        foreach (var d in dest.Where(d => !inSource.ContainsKey(d.Name)))
        {
            var fate = top && d.IsDirectory && d.Name.Equals(Versions.FolderName, StringComparison.OrdinalIgnoreCase) ? LayoutFate.Excluded
                : Excluded(job, d) ? LayoutFate.Excluded
                : job.Mode != CopyMode.Mirror ? LayoutFate.Kept
                : job.Options.KeepVersions ? LayoutFate.Archived
                : LayoutFate.Deleted;
            r.Items.Add(new LayoutItem(d.Name, d.IsDirectory, fate, d.Size));
        }
        r.Items.Sort((a, b) => a.IsDirectory != b.IsDirectory
            ? (a.IsDirectory ? -1 : 1)
            : StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));

        if (top) AddNotes(job, r, inSource, dest, destination == null);
        return r;
    }

    private static void AddNotes(CopyJob job, LayoutResult r, Dictionary<string, PathEntry> inSource, IReadOnlyList<PathEntry> dest, bool missing)
    {
        var src = PathUtil.NormalizeDir(job.Source.Path);
        var dst = PathUtil.NormalizeDir(job.Destination.Path);
        var leaf = FolderName(src);
        var dstLeaf = FolderName(dst);

        if (leaf != null)
        {
            r.Notes.Add(new SummaryLine(SummaryLevel.Info, Loc.F("layout.into", src, dst, leaf)));
            if (!leaf.Equals(dstLeaf, StringComparison.OrdinalIgnoreCase)) r.Suggestion = PathUtil.Combine(dst, leaf);
        }
        if (missing) r.Notes.Add(new SummaryLine(SummaryLevel.Info, Loc.F("layout.create", dst)));

        // A folder named like the source inside the destination: earlier copies went one level deeper.
        if (leaf != null && !inSource.ContainsKey(leaf) && dest.Any(d => d.IsDirectory && d.Name.Equals(leaf, StringComparison.OrdinalIgnoreCase)))
        {
            if (leaf.Equals(dstLeaf, StringComparison.OrdinalIgnoreCase))
            {
                r.Notes.Add(new SummaryLine(SummaryLevel.Warning, Loc.F("layout.double", PathUtil.Combine(dst, leaf))));
            }
            else
            {
                r.Notes.Add(new SummaryLine(SummaryLevel.Warning, Loc.F("layout.nested", leaf, PathUtil.Combine(dst, leaf))));
                r.SuggestionRecommended = true;
            }
        }

        // Nothing in common: maybe the wrong folder, and a mirror would empty it.
        var others = dest.Where(d => !d.Name.Equals(Versions.FolderName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (inSource.Count > 0 && others.Count > 0 && others.All(d => !inSource.ContainsKey(d.Name)))
        {
            var mirror = job.Mode == CopyMode.Mirror;
            r.Notes.Add(new SummaryLine(mirror && !job.Options.KeepVersions ? SummaryLevel.Danger : SummaryLevel.Warning,
                Loc.F(!mirror ? "layout.foreign" : job.Options.KeepVersions ? "layout.foreign.archive" : "layout.foreign.delete", Fmt.Count(others.Count))));
        }
    }

    /// <summary>Last folder name, or null for a drive or share root (which has none to keep).</summary>
    public static string? FolderName(string path)
    {
        var n = PathUtil.NormalizeDir(path);
        if (n.Length <= 3 && n.EndsWith(":\\")) return null;
        if (PathUtil.IsUnc(n) && PathUtil.ShareRoot(n) is { } root && root.Equals(n.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return null;
        return PathUtil.LastName(n);
    }

    private static bool Excluded(CopyJob job, PathEntry e) => new Exclusions(job).Excludes(e.Name, e.IsDirectory);
}
