using System.Runtime.InteropServices;
using System.Text;

namespace AgsCopy.App;

/// <summary>Drive letters mapped to network shares, which the service (another logon session) cannot see.</summary>
public static class NetworkDrives
{
    /// <summary>"Z:\Data" on a drive mapped to \\nas\share → "\\nas\share\Data"; null otherwise.</summary>
    public static string? ToUnc(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Replace('/', '\\');
        if (p.Length < 2 || !char.IsAsciiLetter(p[0]) || p[1] != ':') return null;
        var drive = p[..2].ToUpperInvariant();
        var buffer = new StringBuilder(512);
        var length = buffer.Capacity;
        if (WNetGetConnection(drive, buffer, ref length) != 0) return null;
        var rest = p[2..].TrimStart('\\');
        return rest.Length == 0 ? buffer.ToString() : buffer.ToString().TrimEnd('\\') + "\\" + rest;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);
}
