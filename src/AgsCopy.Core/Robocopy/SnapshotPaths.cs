using AgsCopy.Core.Models;

namespace AgsCopy.Core.Robocopy;

/// <summary>
/// A shadow copy shows a whole volume under another folder (D:\ → ...\vss\run\). These helpers
/// move a job's source and its absolute exclusions onto that folder.
/// </summary>
public static class SnapshotPaths
{
    /// <summary>D:\Data\Docs with volume D:\ and snapshot S:\snap → S:\snap\Data\Docs.</summary>
    public static string Map(string path, string volumeRoot, string snapshotRoot)
    {
        if (!PathUtil.IsAbsolute(path) || !PathUtil.IsUnder(path, volumeRoot)) return path;
        var rest = PathUtil.WithSlash(path)[PathUtil.WithSlash(volumeRoot).Length..].TrimEnd('\\');
        return rest.Length == 0 ? PathUtil.NormalizeDir(snapshotRoot) : PathUtil.Combine(snapshotRoot, rest);
    }

    /// <summary>The job reading its source from the snapshot. The destination is unchanged.</summary>
    public static CopyJob ForSnapshot(CopyJob job, string volumeRoot, string snapshotRoot)
    {
        var j = Json.Clone(job);
        j.Source.Path = Map(job.Source.Path, volumeRoot, snapshotRoot);
        j.Options.ExcludeDirectories = job.Options.ExcludeDirectories.Select(x => Map(x, volumeRoot, snapshotRoot)).ToList();
        j.Options.ExcludeFiles = job.Options.ExcludeFiles.Select(x => Map(x, volumeRoot, snapshotRoot)).ToList();
        return j;
    }
}
