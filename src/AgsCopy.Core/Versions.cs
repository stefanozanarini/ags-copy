using System.Globalization;
using AgsCopy.Core.Models;
using AgsCopy.Core.Robocopy;

namespace AgsCopy.Core;

public enum VersionReason
{
    /// <summary>The source has a different version of the file: robocopy would overwrite it.</summary>
    Overwritten,
    /// <summary>The source no longer has it: a mirror would delete it.</summary>
    Deleted,
}

/// <summary>A destination item to move into the versions folder before the copy.</summary>
public sealed record VersionMove(string DestinationPath, string RelativePath, bool IsDirectory, VersionReason Reason);

/// <summary>
/// Old versions of destination files. Before a run, whatever robocopy would overwrite or delete
/// in the destination is moved to Destination\_AGS-Copy-Versions\yyyy-MM-dd_HHmmss\, keeping
/// its relative path; folders older than the retention are deleted after the run. Only the
/// destination is touched.
/// </summary>
public static class Versions
{
    public const string FolderName = "_AGS-Copy-Versions";
    public const string StampFormat = "yyyy-MM-dd_HHmmss";
    public const int MaxRetentionDays = 3650;

    public static string Root(string destination) => PathUtil.Combine(destination, FolderName);

    public static string RunFolder(string destination, DateTime runStartLocal) =>
        PathUtil.Combine(Root(destination), runStartLocal.ToString(StampFormat, CultureInfo.InvariantCulture));

    /// <summary>
    /// What to move, from the lines of a robocopy /L run. A source file robocopy would copy
    /// becomes a candidate at the same relative path in the destination; the caller keeps the
    /// candidates that exist (the others are new files). Extras count only in Mirror mode.
    /// </summary>
    public static IEnumerable<VersionMove> Plan(IEnumerable<string> listLines, string source, string destination, CopyMode mode)
    {
        var src = PathUtil.WithSlash(source);
        var dst = PathUtil.WithSlash(destination);
        var destinationInsideSource = PathUtil.IsUnder(dst, src);
        foreach (var raw in listLines)
        {
            if (RobocopyOutput.ParseLine(raw) is not { } l) continue;
            var inDest = PathUtil.IsUnder(l.Path, dst) && (destinationInsideSource || !PathUtil.IsUnder(l.Path, src));
            if (inDest)
            {
                if (mode != CopyMode.Mirror) continue;
                var rel = l.Path[dst.Length..].TrimEnd('\\');
                if (rel.Length == 0 || IsInsideVersions(rel)) continue;
                yield return new VersionMove(PathUtil.NormalizeDir(l.Path), rel, l.IsDirectory, VersionReason.Deleted);
            }
            else if (!l.IsDirectory && PathUtil.IsUnder(l.Path, src))
            {
                var rel = l.Path[src.Length..];
                yield return new VersionMove(dst + rel, rel, false, VersionReason.Overwritten);
            }
        }
    }

    private static bool IsInsideVersions(string relative) =>
        relative.Equals(FolderName, StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith(FolderName + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Version folders (by name) older than the retention. 0 days keeps everything.</summary>
    public static List<string> Expired(IEnumerable<string> folderNames, DateTime nowLocal, int retentionDays)
    {
        if (retentionDays <= 0) return [];
        var limit = nowLocal.AddDays(-retentionDays);
        return folderNames
            .Where(n => DateTime.TryParseExact(n, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) && t < limit)
            .ToList();
    }
}
