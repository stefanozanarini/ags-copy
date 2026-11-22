using System.IO.Enumeration;
using AgsCopy.Core.Models;

namespace AgsCopy.Core;

/// <summary>
/// The job's file and folder exclusions, matched on names (wildcards allowed). Full paths are
/// matched too, against the source location: that is how robocopy reads them.
/// </summary>
public sealed class Exclusions(CopyJob job)
{
    private readonly List<string> _files = job.Options.ExcludeFiles.Select(Clean).Where(p => p.Length > 0).ToList();
    private readonly List<string> _dirs = job.Options.ExcludeDirectories.Select(Clean).Where(p => p.Length > 0).ToList();
    private readonly string _source = job.Source.Path;

    /// <param name="relative">Path below the source or destination root, '/' or '\' separated.</param>
    public bool Excludes(string relative, bool isDirectory)
    {
        var name = relative.Replace('\\', '/').TrimEnd('/');
        name = name[(name.LastIndexOf('/') + 1)..];
        foreach (var p in isDirectory ? _dirs : _files)
        {
            if (p.Contains('\\') || p.Contains('/'))
            {
                if (PathUtil.Combine(_source, PathUtil.IsSftp(_source) ? relative : relative.Replace('/', '\\')).Equals(PathUtil.NormalizeDir(p), StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true))
            {
                return true;
            }
        }
        return false;
    }

    private static string Clean(string p) => p.Trim().Trim('"');
}
