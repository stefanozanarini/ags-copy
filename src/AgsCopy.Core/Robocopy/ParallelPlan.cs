using System.Text.RegularExpressions;
using AgsCopy.Core.Models;

namespace AgsCopy.Core.Robocopy;

public sealed record RobocopyTask(string Source, string Destination, RobocopyPass Pass, IReadOnlyCollection<string>? RootExcludedDirs = null)
{
    public string Label => Pass == RobocopyPass.Folder ? PathUtil.LastName(Source) : "(root)";
}

/// <summary>
/// Splits a job into robocopy processes that can run side by side: one for the files in the
/// root folder, one per top-level folder. Each process keeps its own /MT threads.
/// </summary>
public static class ParallelPlan
{
    public static IReadOnlyList<RobocopyTask> Build(CopyJob job, IReadOnlyList<string> topLevelDirNames)
    {
        var src = PathUtil.NormalizeDir(job.Source.Path);
        var dst = PathUtil.NormalizeDir(job.Destination.Path);
        var full = new[] { new RobocopyTask(src, dst, RobocopyPass.Full) };

        if (job.Options.Processes <= 1) return full;

        var names = topLevelDirNames
            .Where(n => !IsExcluded(job.Options.ExcludeDirectories, src, n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count < 2) return full;

        // The root pass excludes every source folder by name; with thousands of folders the
        // command line would not fit, and a single process is used instead.
        var root = new RobocopyTask(src, dst, RobocopyPass.Root, names);
        var rootArgs = RobocopyArgs.Build(job, src, dst, RobocopyPass.Root, false, @"C:\ProgramData\AGS-Copy\logs\x\yyyyMMdd-HHmmss-000.log", true, names);
        if (RobocopyArgs.Length(rootArgs) > RobocopyArgs.MaxCommandLine) return full;

        var tasks = new List<RobocopyTask> { root };
        tasks.AddRange(names.Select(n => new RobocopyTask(PathUtil.Combine(src, n), PathUtil.Combine(dst, n), RobocopyPass.Folder)));
        return tasks;
    }

    /// <summary>Matches robocopy's /XD rule: a folder name with wildcards, or a full path.</summary>
    public static bool IsExcluded(IEnumerable<string> patterns, string sourceRoot, string name)
    {
        foreach (var raw in patterns)
        {
            var p = raw.Trim();
            if (p.Length == 0) continue;
            if (PathUtil.IsAbsolute(p))
            {
                if (string.Equals(PathUtil.NormalizeDir(p), PathUtil.Combine(sourceRoot, name), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (Wildcard(p).IsMatch(name))
            {
                return true;
            }
        }
        return false;
    }

    private static Regex Wildcard(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
