using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AgsCopy.Core.Credentials;

/// <summary>
/// Authenticated connections to \\server\share without a drive letter (WNetAddConnection2).
/// Connections belong to the logon session, so they are reference counted: two jobs on the
/// same share share one connection.
/// </summary>
[SupportedOSPlatform("windows")]
public static class NetworkShare
{
    private const int ResourceTypeDisk = 1;
    private const int ErrorSessionCredentialConflict = 1219;

    private static readonly Dictionary<string, int> Counts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Lock = new();

    public sealed class Lease : IDisposable
    {
        private readonly string _root;
        private bool _disposed;

        internal Lease(string root) => _root = root;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Release(_root);
        }
    }

    /// <summary>Opens (or reuses) a connection to the share of <paramref name="uncPath"/>.</summary>
    /// <exception cref="NetworkShareException">With a readable reason.</exception>
    public static Lease Connect(string uncPath, string userName, string password)
    {
        var root = Core.PathUtil.ShareRoot(uncPath) ?? throw new NetworkShareException(87, "Not a \\\\server\\share path: " + uncPath);
        lock (Lock)
        {
            if (Counts.TryGetValue(root, out var n))
            {
                Counts[root] = n + 1;
                return new Lease(root);
            }

            var res = new NetResource { dwType = ResourceTypeDisk, lpRemoteName = root };
            var rc = WNetAddConnection2(ref res, password, userName, 0);
            if (rc != 0) throw new NetworkShareException(rc, Message(rc, root));
            Counts[root] = 1;
            return new Lease(root);
        }
    }

    private static void Release(string root)
    {
        lock (Lock)
        {
            if (!Counts.TryGetValue(root, out var n)) return;
            if (n > 1)
            {
                Counts[root] = n - 1;
                return;
            }
            Counts.Remove(root);
            WNetCancelConnection2(root, 0, true);
        }
    }

    private static string Message(int rc, string root) => rc switch
    {
        ErrorSessionCredentialConflict =>
            $"Another connection to the server of {root} uses different credentials (error 1219). " +
            "Use the same account for every share of that server, or reach one of them by IP address.",
        _ => $"{new Win32Exception(rc).Message} ({root}, error {rc})",
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        public string? lpLocalName;
        public string? lpRemoteName;
        public string? lpComment;
        public string? lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NetResource netResource, string? password, string? userName, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);
}

public sealed class NetworkShareException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
