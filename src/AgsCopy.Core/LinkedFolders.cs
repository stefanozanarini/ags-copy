using AgsCopy.Core.Models;
using AgsCopy.Core.Sync;

namespace AgsCopy.Core;

/// <summary>A folder found while looking for links below a share.</summary>
/// <param name="IsDfsLink">A DFS link: the DFS client sends whoever opens it to another share.</param>
/// <param name="IsJunction">A directory junction, which the file server resolves on its own disks.</param>
/// <param name="IsReparsePoint">Any reparse point, these two included (symbolic links are the others).</param>
public sealed record FolderEntry(string Name, bool IsDfsLink, bool IsJunction, bool IsReparsePoint);

public enum LinkKind
{
    Dfs,
    Junction,
}

/// <summary>A link below a source that is followed: <see cref="Relative"/> ('\' separated) and its full path.</summary>
public sealed record LinkedFolder(string Relative, string Path, LinkKind Kind);

public sealed class LinkScan
{
    public List<LinkedFolder> Links { get; } = [];

    /// <summary>Junctions (or folders behind them) not followed: they lead back to a folder they are in.</summary>
    public List<string> Loops { get; } = [];

    /// <summary>The look stopped at its folder limit; links further on are not followed.</summary>
    public bool Truncated { get; set; }
}

/// <summary>
/// Links below a share source that the copy follows: DFS links always, directory junctions when the job
/// says so. Reading through them works by itself (the DFS client follows referrals, the file server
/// resolves junctions), but robocopy sees both as junctions and /XJ skips them, and the copy engine
/// leaves reparse points out. Letting robocopy follow junctions itself would also follow one that
/// points back to a folder above it, round and round. So the links are found here first, loops left
/// out, and each link is copied as a source of its own into the same place of the destination, while
/// everything around it is copied without it.
/// </summary>
public static class LinkedFolders
{
    /// <summary>Without junctions only the DFS namespace is walked, whose folders are few.</summary>
    public const int MaxNamespaceFolders = 5000;

    /// <summary>Following junctions walks the whole share; past this many folders the look stops.</summary>
    public const int MaxFolders = 2_000_000;

    /// <param name="list">The sub-folders of a folder.</param>
    /// <param name="folderId">Identity of a folder (volume and file id) once its links are followed; needed to follow junctions.</param>
    public static LinkScan Find(string root, Func<string, IEnumerable<FolderEntry>> list, Func<string, string?>? folderId, Exclusions exclusions,
        bool followJunctions, CancellationToken ct, int? maxFolders = null)
    {
        var scan = new LinkScan();
        var max = maxFolders ?? (followJunctions ? MaxFolders : MaxNamespaceFolders);
        var ids = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(string Rel, bool BehindLink)>();
        pending.Enqueue(("", false));
        var seen = 0;

        string Full(string rel) => rel.Length == 0 ? root : PathUtil.Combine(root, rel);

        string? Id(string rel)
        {
            if (!ids.TryGetValue(rel, out var id)) ids[rel] = id = folderId!(Full(rel));
            return id;
        }

        // A folder whose identity is that of a folder it sits in: entering it would go round forever.
        bool LoopsBack(string rel)
        {
            if (folderId == null || Id(rel) is not { } id) return false;
            for (var i = rel.LastIndexOf('\\'); ; i = rel.LastIndexOf('\\', i - 1))
            {
                if (Id(i < 0 ? "" : rel[..i]) == id) return true;
                if (i <= 0) return false;
            }
        }

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (rel, behindLink) = pending.Dequeue();
            foreach (var f in list(Full(rel)))
            {
                var child = rel.Length == 0 ? f.Name : rel + "\\" + f.Name;
                if (rel.Length == 0 && f.Name.Equals(Versions.FolderName, StringComparison.OrdinalIgnoreCase)) continue;
                if (exclusions.Excludes(child, true)) continue;

                if (f.IsDfsLink || (f.IsJunction && followJunctions))
                {
                    if (followJunctions && LoopsBack(child))
                    {
                        scan.Loops.Add(child);
                        continue;
                    }
                    scan.Links.Add(new LinkedFolder(child, Full(child), f.IsDfsLink ? LinkKind.Dfs : LinkKind.Junction));
                    // Junctions may sit anywhere behind a link too; DFS links do not nest.
                    if (followJunctions) Enqueue(child, true);
                    continue;
                }
                if (f.IsReparsePoint) continue; // symbolic links, and junctions the job does not follow
                // Behind a junction a plain folder may be the way back up (a junction to the drive root).
                if (behindLink && LoopsBack(child))
                {
                    scan.Loops.Add(child);
                    continue;
                }
                Enqueue(child, behindLink);
            }
        }
        return scan;

        void Enqueue(string rel, bool behind)
        {
            if (++seen < max) pending.Enqueue((rel, behind));
            else scan.Truncated = true;
        }
    }

    /// <summary>Links directly below <paramref name="relative"/> ("" = the source), not behind another one.</summary>
    private static IEnumerable<LinkedFolder> Below(IReadOnlyList<LinkedFolder> links, string relative) =>
        links.Where(l => relative.Length == 0 || l.Relative.StartsWith(relative + "\\", StringComparison.OrdinalIgnoreCase))
            .Where(l => !links.Any(o => o != l && l.Relative.StartsWith(o.Relative + "\\", StringComparison.OrdinalIgnoreCase)
                && (relative.Length == 0 || o.Relative.StartsWith(relative + "\\", StringComparison.OrdinalIgnoreCase))));

    /// <summary>
    /// The job copying its source without the links: they are excluded by full path on both sides,
    /// so the copy skips them and a mirror never deletes their copies in the destination.
    /// </summary>
    public static CopyJob WithoutLinks(CopyJob job, IReadOnlyList<LinkedFolder> links) => Exclude(Json.Clone(job), job, Below(links, ""));

    /// <summary>The job copying one link into the same relative folder of the destination, without the links below it.</summary>
    public static CopyJob ForLink(CopyJob job, LinkedFolder link, IReadOnlyList<LinkedFolder> links)
    {
        var j = Json.Clone(job);
        j.Source.Path = link.Path;
        j.Destination.Path = PathUtil.Combine(job.Destination.Path, link.Relative);
        return Exclude(j, job, Below(links, link.Relative));
    }

    private static CopyJob Exclude(CopyJob j, CopyJob job, IEnumerable<LinkedFolder> links)
    {
        foreach (var l in links)
        {
            j.Options.ExcludeDirectories.Add(l.Path);
            j.Options.ExcludeDirectories.Add(PathUtil.Combine(job.Destination.Path, l.Relative));
        }
        return j;
    }
}

/// <summary>
/// A source with other sources shown inside it as folders: the links below a share, each read through
/// its own path, so the copy engine sees one tree. Only reads, like every source.
/// </summary>
public sealed class MountedSource(ISourceStore main, IReadOnlyDictionary<string, ISourceStore> mounts) : ISourceStore
{
    /// <summary>Mount folders, '/' separated relative paths.</summary>
    private readonly Dictionary<string, ISourceStore> _mounts =
        mounts.ToDictionary(m => m.Key.Replace('\\', '/').Trim('/'), m => m.Value, StringComparer.OrdinalIgnoreCase);

    private (ISourceStore Store, string Relative, string Prefix) Route(string relative)
    {
        var r = relative.Replace('\\', '/').Trim('/');
        foreach (var (prefix, store) in _mounts.OrderByDescending(m => m.Key.Length))
        {
            if (r.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return (store, "", prefix);
            if (r.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) return (store, r[(prefix.Length + 1)..], prefix);
        }
        return (main, r, "");
    }

    public string Display(string relative)
    {
        var (store, rel, _) = Route(relative);
        return store.Display(rel);
    }

    public bool Exists() => main.Exists();

    public IReadOnlyList<SyncEntry> List(string relative)
    {
        var r = relative.Replace('\\', '/').Trim('/');
        var (store, rel, prefix) = Route(r);
        var list = store.List(rel).Select(e => prefix.Length == 0 ? e : e with { Relative = SyncPath.Join(prefix, e.Relative) }).ToList();
        foreach (var m in _mounts.Keys.Where(k => string.Equals(SyncPath.Parent(k), r, StringComparison.OrdinalIgnoreCase)))
        {
            // The store above leaves the link out (it is a reparse point) or shows it as a folder.
            list.RemoveAll(e => e.Relative.Equals(m, StringComparison.OrdinalIgnoreCase));
            if (_mounts[m].Exists()) list.Add(new SyncEntry(m, true, 0, default));
        }
        return list;
    }

    public Stream OpenRead(string relative)
    {
        var (store, rel, _) = Route(relative);
        return store.OpenRead(rel);
    }

    public void Dispose()
    {
        main.Dispose();
        foreach (var m in _mounts.Values) m.Dispose();
    }
}
