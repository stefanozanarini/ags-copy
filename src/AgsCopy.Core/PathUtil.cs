namespace AgsCopy.Core;

/// <summary>
/// Windows path helpers written on strings, so they behave the same when the tests run on Linux.
/// sftp://host[:port]/path locations keep their forward slashes and are compared as written.
/// </summary>
public static class PathUtil
{
    public const string SftpScheme = "sftp://";

    public static bool IsSftp(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.Trim().StartsWith(SftpScheme, StringComparison.OrdinalIgnoreCase);

    public static bool IsUnc(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.Trim().Replace('/', '\\').StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>\\server\share\a\b → \\server\share; null when the path is not a valid UNC path.</summary>
    public static string? ShareRoot(string? path)
    {
        if (!IsUnc(path)) return null;
        var parts = path!.Trim().Replace('/', '\\').TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        return @"\\" + parts[0] + @"\" + parts[1];
    }

    /// <summary>Server name of a UNC path.</summary>
    public static string? Server(string? path) =>
        ShareRoot(path) is { } root ? root[2..root.IndexOf('\\', 2)] : null;

    /// <summary>
    /// Trims spaces and trailing separators, which robocopy misreads before a closing quote
    /// ("D:\data\" ends up as D:\data"). Drive roots keep theirs: D:\.
    /// </summary>
    public static string NormalizeDir(string path)
    {
        if (IsSftp(path))
        {
            var u = path.Trim().Replace('\\', '/');
            var rootEnd = u.IndexOf('/', SftpScheme.Length);
            while (u.EndsWith('/') && rootEnd >= 0 && u.Length > rootEnd + 1) u = u[..^1];
            return u;
        }
        var p = path.Trim().Replace('/', '\\');
        while (p.Length > 3 && p.EndsWith('\\')) p = p[..^1];
        if (p.Length == 2 && p[1] == ':') p += "\\";
        return p;
    }

    public static string WithSlash(string path)
    {
        var n = NormalizeDir(path);
        var sep = IsSftp(n) ? '/' : '\\';
        return n.EndsWith(sep) ? n : n + sep;
    }

    public static string Combine(string root, string name) => IsSftp(root)
        ? WithSlash(root) + name.Replace('\\', '/').Trim('/')
        : WithSlash(root) + name.Trim('\\');

    /// <summary>True when path is root itself or inside it.</summary>
    public static bool IsUnder(string path, string root) =>
        WithSlash(path).StartsWith(WithSlash(root), StringComparison.OrdinalIgnoreCase);

    public static bool Overlaps(string a, string b) => IsUnder(a, b) || IsUnder(b, a);

    /// <summary>Accepts D:\..., \\server\share\..., sftp://host/path; rejects relative paths.</summary>
    public static bool IsAbsolute(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (IsSftp(path)) return SftpLocation.TryParse(path) != null;
        var p = path.Trim().Replace('/', '\\');
        if (p.StartsWith(@"\\")) return ShareRoot(p) != null;
        return p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\';
    }

    /// <summary>Last component of a folder path.</summary>
    public static string LastName(string path)
    {
        var sep = IsSftp(path) ? '/' : '\\';
        var n = NormalizeDir(path).TrimEnd(sep);
        var i = n.LastIndexOf(sep);
        return i >= 0 ? n[(i + 1)..] : n;
    }
}
