namespace AgsCopy.Core;

/// <summary>sftp://host[:port]/absolute/path. The account is in the endpoint's credential, not in the URL.</summary>
public sealed record SftpLocation(string Host, int Port, string RemotePath)
{
    public const int DefaultPort = 22;

    public static SftpLocation? TryParse(string? path)
    {
        if (!PathUtil.IsSftp(path)) return null;
        var rest = path!.Trim()[PathUtil.SftpScheme.Length..].Replace('\\', '/');
        if (rest.Contains('@')) return null; // user@host: the user goes in the credentials
        var slash = rest.IndexOf('/');
        var authority = slash < 0 ? rest : rest[..slash];
        var remote = slash < 0 ? "/" : rest[slash..];
        if (authority.Length == 0) return null;

        var host = authority;
        var port = DefaultPort;
        var colon = authority.LastIndexOf(':');
        if (colon > 0 && !authority.EndsWith(']'))
        {
            if (!int.TryParse(authority[(colon + 1)..], out port) || port is < 1 or > 65535) return null;
            host = authority[..colon];
        }
        host = host.Trim('[', ']');
        if (host.Length == 0 || host.Any(char.IsWhiteSpace)) return null;
        if (remote.Split('/').Any(p => p == "..")) return null;
        while (remote.Length > 1 && remote.EndsWith('/')) remote = remote[..^1];
        return new SftpLocation(host, port, remote);
    }

    /// <summary>Remote path of an item below this location ("" = the location itself).</summary>
    public string Resolve(string relative) =>
        relative.Length == 0 ? RemotePath : (RemotePath == "/" ? "/" : RemotePath + "/") + relative.Replace('\\', '/').Trim('/');

    public override string ToString() =>
        PathUtil.SftpScheme + (Host.Contains(':') ? $"[{Host}]" : Host) + (Port == DefaultPort ? "" : ":" + Port) + RemotePath;
}
