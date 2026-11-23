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

/// <summary>How far a walk is, read by the activity window while it runs.</summary>
public sealed class WalkProgress
{
    private long _folders;
    private int _links;

    public long Folders => Interlocked.Read(ref _folders);
    public int Links => Volatile.Read(ref _links);
    public bool Done { get; set; }

    internal void Folder() => Interlocked.Increment(ref _folders);
    internal void SetLinks(int links) => Volatile.Write(ref _links, links);
}

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

    /// <summary>Threads that list folders at once when a whole share is walked: over SMB each listing mostly waits on the network.</summary>
    public const int WalkThreads = 16;

    /// <param name="list">The sub-folders of a folder.</param>
    /// <param name="folderId">Identity of a folder (volume and file id) once its links are followed; needed to follow junctions.</param>
    /// <param name="threads">Folders listed at once. The walk goes level by level, so the result does not depend on it.</param>
    public static LinkScan Find(string root, Func<string, IEnumerable<FolderEntry>> list, Func<string, string?>? folderId, Exclusions exclusions,
        bool followJunctions, CancellationToken ct, int? maxFolders = null, int threads = 1, WalkProgress? progress = null)
    {
        var scan = new LinkScan();
        var max = maxFolders ?? (followJunctions ? MaxFolders : MaxNamespaceFolders);
        var ids = new System.Collections.Concurrent.ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var level = new List<(string Rel, bool BehindLink)> { ("", false) };
        var seen = 0;

        string Full(string rel) => rel.Length == 0 ? root : PathUtil.Combine(root, rel);

        // A folder whose identity is that of a folder it sits in: entering it would go round forever.
        bool LoopsBack(string rel) => folderId != null && LoopsBackTo(rel, r => ids.GetOrAdd(r, x => folderId(Full(x))));

        // What one folder holds: links, loops, and the folders to walk next, in the order listed.
        List<(Found What, string Rel, bool Behind, bool IsDfs)> Visit((string Rel, bool BehindLink) folder)
        {
            var (rel, behindLink) = folder;
            var found = new List<(Found, string, bool, bool)>();
            progress?.Folder();
            foreach (var f in list(Full(rel)))
            {
                var child = rel.Length == 0 ? f.Name : rel + "\\" + f.Name;
                if (rel.Length == 0 && f.Name.Equals(Versions.FolderName, StringComparison.OrdinalIgnoreCase)) continue;
                if (exclusions.Excludes(child, true)) continue;

                if (f.IsDfsLink || (f.IsJunction && followJunctions))
                {
                    if (followJunctions && LoopsBack(child))
                    {
                        found.Add((Found.Loop, child, false, false));
                        continue;
                    }
                    found.Add((Found.Link, child, false, f.IsDfsLink));
                    // Junctions may sit anywhere behind a link too; DFS links do not nest.
                    if (followJunctions) found.Add((Found.Folder, child, true, false));
                    continue;
                }
                if (f.IsReparsePoint) continue; // symbolic links, and junctions the job does not follow
                // Behind a junction a plain folder may be the way back up (a junction to the drive root).
                if (behindLink && LoopsBack(child))
                {
                    found.Add((Found.Loop, child, false, false));
                    continue;
                }
                found.Add((Found.Folder, child, behindLink, false));
            }
            return found;
        }

        while (level.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var results = new List<(Found What, string Rel, bool Behind, bool IsDfs)>[level.Count];
            if (threads <= 1 || level.Count == 1)
            {
                for (var i = 0; i < level.Count; i++) results[i] = Visit(level[i]);
            }
            else
            {
                // Threads of its own: listing a share folder blocks on the network, and blocked thread-pool
                // threads would starve the rest of the service (its pipe stops answering).
                var current = level;
                var taken = -1;
                Exception? error = null;
                var workers = Enumerable.Range(0, Math.Min(threads, current.Count)).Select(_ => new Thread(() =>
                {
                    try
                    {
                        for (int i; (i = Interlocked.Increment(ref taken)) < current.Count && Volatile.Read(ref error) == null && !ct.IsCancellationRequested;)
                            results[i] = Visit(current[i]);
                    }
                    catch (Exception e)
                    {
                        Interlocked.CompareExchange(ref error, e, null);
                    }
                }) { IsBackground = true, Name = "AGS-Copy link walk" }).ToList();
                foreach (var w in workers) w.Start();
                foreach (var w in workers) w.Join();
                if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
                ct.ThrowIfCancellationRequested();
            }

            var next = new List<(string, bool)>();
            foreach (var (what, rel, behind, isDfs) in results.SelectMany(r => r))
            {
                switch (what)
                {
                    case Found.Link:
                        scan.Links.Add(new LinkedFolder(rel, Full(rel), isDfs ? LinkKind.Dfs : LinkKind.Junction));
                        break;
                    case Found.Loop:
                        scan.Loops.Add(rel);
                        break;
                    default:
                        if (++seen < max) next.Add((rel, behind));
                        else scan.Truncated = true;
                        break;
                }
            }
            level = next;
            progress?.SetLinks(scan.Links.Count);
        }
        return scan;
    }

    private enum Found
    {
        Folder,
        Link,
        Loop,
    }

    /// <summary>Whether the folder at <paramref name="rel"/> is one of the folders it sits in ("" = the root).</summary>
    private static bool LoopsBackTo(string rel, Func<string, string?> id)
    {
        if (id(rel) is not { } own) return false;
        for (var i = rel.LastIndexOf('\\'); ; i = rel.LastIndexOf('\\', i - 1))
        {
            if (id(i < 0 ? "" : rel[..i]) == own) return true;
            if (i <= 0) return false;
        }
    }

    /// <summary>
    /// The DFS links below a source in a namespace, from the namespace's own list of links
    /// (\\server\namespace\link\path, as NetDfsEnum gives them) instead of walking its folders.
    /// The server name the namespace reports may differ from the one in the source (NetBIOS or DNS):
    /// only the part after the namespace counts.
    /// </summary>
    public static LinkScan FromNamespace(string source, IEnumerable<string> entries, Exclusions exclusions)
    {
        var scan = new LinkScan();
        var src = PathUtil.NormalizeDir(source).TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (src.Length < 2) return scan;
        var below = src.Skip(2).ToList(); // the source's folders inside the namespace
        foreach (var entry in entries)
        {
            var parts = entry.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 2 || !string.Equals(parts[1], src[1], StringComparison.OrdinalIgnoreCase)) continue;
            var inNamespace = parts.Skip(2).ToList();
            if (inNamespace.Count <= below.Count
                || !inNamespace.Take(below.Count).SequenceEqual(below, StringComparer.OrdinalIgnoreCase)) continue;
            var relParts = inNamespace.Skip(below.Count).ToList();
            if (relParts[0].Equals(Versions.FolderName, StringComparison.OrdinalIgnoreCase)) continue;
            var rel = string.Join('\\', relParts);
            if (Excluded(rel, exclusions)) continue;
            if (scan.Links.Any(l => l.Relative.Equals(rel, StringComparison.OrdinalIgnoreCase))) continue;
            scan.Links.Add(new LinkedFolder(rel, PathUtil.Combine(source, rel), LinkKind.Dfs));
        }
        scan.Links.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Relative, b.Relative));
        return scan;
    }

    /// <summary>The walk does not go into an excluded folder: a link in one, or below one, is not followed.</summary>
    private static bool Excluded(string rel, Exclusions exclusions)
    {
        var parts = rel.Split('\\');
        return Enumerable.Range(1, parts.Length).Any(n => exclusions.Excludes(string.Join('\\', parts.Take(n)), true));
    }

    /// <summary>
    /// The links of an earlier walk that still are what they were: the folder is still there and still a
    /// DFS link or a junction (followed only with <paramref name="followJunctions"/>), and a junction does
    /// not lead back to a folder it is in. Checking a few folders takes moments; walking the share again
    /// takes as long as the share is big.
    /// </summary>
    /// <param name="entry">The folder itself as its parent lists it; null when it is gone.</param>
    public static List<LinkedFolder> Recheck(string root, IEnumerable<LinkedFolder> links, Func<string, FolderEntry?> entry, Func<string, string?> folderId,
        Exclusions exclusions, bool followJunctions, CancellationToken ct)
    {
        var ids = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? Id(string rel)
        {
            if (!ids.TryGetValue(rel, out var id)) ids[rel] = id = folderId(rel.Length == 0 ? root : PathUtil.Combine(root, rel));
            return id;
        }

        var kept = new List<LinkedFolder>();
        foreach (var l in links)
        {
            ct.ThrowIfCancellationRequested();
            if (Excluded(l.Relative, exclusions)) continue; // the job's exclusions may have changed since
            var path = PathUtil.Combine(root, l.Relative);
            if (entry(path) is not { } e) continue;
            var ok = l.Kind == LinkKind.Dfs ? e.IsDfsLink : e.IsJunction && followJunctions && !LoopsBackTo(l.Relative, Id);
            if (ok) kept.Add(l with { Path = path });
        }
        return kept;
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
/// What the last walk of each share source of a job found, so the next run can start copying with those
/// links at once while the share is walked again.
/// </summary>
public sealed class LinkCache
{
    public sealed class Entry
    {
        public string Source { get; set; } = "";
        public DateTime WalkedUtc { get; set; }
        public List<LinkedFolder> Links { get; set; } = [];
        public List<string> Loops { get; set; } = [];
    }

    public List<Entry> Sources { get; set; } = [];

    public Entry? Get(string source) =>
        Sources.FirstOrDefault(e => string.Equals(e.Source, PathUtil.NormalizeDir(source), StringComparison.OrdinalIgnoreCase));

    public void Put(string source, LinkScan scan, DateTime walkedUtc)
    {
        Sources.RemoveAll(e => string.Equals(e.Source, PathUtil.NormalizeDir(source), StringComparison.OrdinalIgnoreCase));
        Sources.Add(new Entry { Source = PathUtil.NormalizeDir(source), WalkedUtc = walkedUtc, Links = [.. scan.Links], Loops = [.. scan.Loops] });
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
