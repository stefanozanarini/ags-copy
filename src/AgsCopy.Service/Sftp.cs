using System.Text;
using AgsCopy.Core;
using AgsCopy.Core.Credentials;
using AgsCopy.Core.Ipc;
using AgsCopy.Core.Models;
using AgsCopy.Core.Sync;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace AgsCopy.Service;

/// <summary>Connections to SFTP endpoints, with the server's host key checked against the one recorded.</summary>
public static class Sftp
{
    /// <summary>
    /// Connects to the endpoint's server. With <paramref name="expectedHostKey"/> the server must present
    /// that key; without (the first access check) any key is accepted and returned to be recorded.
    /// </summary>
    public static SftpClient Connect(JobEndpoint endpoint, JobEndpoint? stored, string? expectedHostKey, string? lang, out string? hostKey)
    {
        var location = SftpLocation.TryParse(endpoint.Path) ?? throw new ServiceException(Loc.Tl(lang, "val.sftp"));
        if (endpoint.Credential is not { UserName.Length: > 0 } credential) throw new ServiceException(Loc.Tl(lang, "err.sftpuser"));
        var user = credential.UserName.Trim();
        var (password, key) = Secrets(endpoint, stored);

        var methods = new List<AuthenticationMethod>();
        if (key != null)
        {
            using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(key));
            try
            {
                methods.Add(new PrivateKeyAuthenticationMethod(user, string.IsNullOrEmpty(password) ? new PrivateKeyFile(keyStream) : new PrivateKeyFile(keyStream, password)));
            }
            catch (Exception e) when (e is SshException or InvalidOperationException or ArgumentException)
            {
                throw new ServiceException(Loc.Fl(lang, "err.sftpkey", e.Message));
            }
        }
        else if (!string.IsNullOrEmpty(password))
        {
            methods.Add(new PasswordAuthenticationMethod(user, password));
            // Some servers take passwords only through keyboard-interactive.
            var interactive = new KeyboardInteractiveAuthenticationMethod(user);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts) prompt.Response = password;
            };
            methods.Add(interactive);
        }
        else
        {
            throw new ServiceException(Loc.Fl(lang, "err.password", user));
        }

        var info = new ConnectionInfo(location.Host, location.Port, user, [.. methods]) { Timeout = TimeSpan.FromSeconds(30) };
        var client = new SftpClient(info) { OperationTimeout = TimeSpan.FromMinutes(5) };
        string? seen = null;
        client.HostKeyReceived += (_, e) =>
        {
            seen = "SHA256:" + e.FingerPrintSHA256;
            e.CanTrust = expectedHostKey == null || string.Equals(expectedHostKey, seen, StringComparison.Ordinal);
        };
        try
        {
            client.Connect();
        }
        catch (Exception e) when (e is SshException or System.Net.Sockets.SocketException)
        {
            client.Dispose();
            if (seen != null && expectedHostKey != null && seen != expectedHostKey)
                throw new ServiceException(Loc.Fl(lang, "err.hostkey", location.Host, seen, expectedHostKey));
            if (e is SshAuthenticationException) throw new ServiceException(Loc.Fl(lang, "err.sftpauth", user, location.Host));
            throw new ServiceException(Loc.Fl(lang, "err.sftpconnect", location.Host, location.Port, e.Message));
        }
        hostKey = seen;
        return client;
    }

    /// <summary>A store on the endpoint for a run: the recorded host key is required.</summary>
    /// <param name="stored">Saved version of an endpoint being edited, whose secrets fill those not retyped.</param>
    public static SftpStore Open(JobEndpoint endpoint, JobEndpoint? stored, string? lang)
    {
        if (string.IsNullOrEmpty(endpoint.HostKey)) throw new ServiceException(Loc.Fl(lang, "err.hostkey.missing", endpoint.Path));
        var client = Connect(endpoint, stored, endpoint.HostKey, lang, out _);
        return new SftpStore(client, SftpLocation.TryParse(endpoint.Path)!);
    }

    /// <summary>Password (or key passphrase) and private key, typed now or stored with the job.</summary>
    private static (string? Password, string? Key) Secrets(JobEndpoint e, JobEndpoint? stored)
    {
        var c = e.Credential!;
        var s = stored?.Credential is { } sc && string.Equals(sc.UserName, c.UserName, StringComparison.OrdinalIgnoreCase) ? sc : null;
        var password = !string.IsNullOrEmpty(c.Password) ? c.Password
            : !string.IsNullOrEmpty(c.ProtectedPassword) ? CredentialProtector.Unprotect(c.ProtectedPassword)
            : s?.ProtectedPassword is { } p ? CredentialProtector.Unprotect(p)
            : null;
        var key = !string.IsNullOrEmpty(c.PrivateKey) ? c.PrivateKey
            : !string.IsNullOrEmpty(c.ProtectedPrivateKey) ? CredentialProtector.Unprotect(c.ProtectedPrivateKey)
            : c.HasPrivateKey && s?.ProtectedPrivateKey is { } k ? CredentialProtector.Unprotect(k)
            : null;
        return (password, key);
    }
}

/// <summary>A folder on an SFTP server, as source or destination of the copy engine.</summary>
public sealed class SftpStore(SftpClient client, SftpLocation location) : ITargetStore
{
    private string Remote(string relative) => location.Resolve(relative);

    public string Display(string relative) => relative.Length == 0 ? location.ToString() : PathUtil.Combine(location.ToString(), relative);

    public bool Exists() => client.Exists(location.RemotePath) && client.GetAttributes(location.RemotePath).IsDirectory;

    public IReadOnlyList<SyncEntry> List(string relative) =>
        client.ListDirectory(Remote(relative))
            .Where(f => f.Name is not "." and not ".." && (f.IsDirectory || f.IsRegularFile))
            .Select(f => new SyncEntry(SyncPath.Join(relative, f.Name), f.IsDirectory, f.IsDirectory ? 0 : f.Length, f.LastWriteTimeUtc))
            .ToList();

    public Stream OpenRead(string relative) => client.OpenRead(Remote(relative));

    public void CreateDirectory(string relative)
    {
        // The destination root may be several levels that do not exist yet.
        var path = "";
        foreach (var part in Remote(relative).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path += "/" + part;
            if (!client.Exists(path)) client.CreateDirectory(path);
        }
    }

    public Stream Create(string relative) => client.Create(Remote(relative));

    public void Replace(string from, string to)
    {
        try
        {
            // posix-rename@openssh.com replaces the target in one step.
            client.RenameFile(Remote(from), Remote(to), isPosix: true);
        }
        catch (SshException) when (client.IsConnected)
        {
            if (client.Exists(Remote(to))) client.DeleteFile(Remote(to));
            client.RenameFile(Remote(from), Remote(to));
        }
    }

    public void SetModified(string relative, DateTime utc) => client.SetLastWriteTimeUtc(Remote(relative), utc);

    public void Move(string relative, string newRelative) => client.RenameFile(Remote(relative), Remote(newRelative));

    public void Delete(string relative, bool isDirectory)
    {
        if (!isDirectory)
        {
            client.DeleteFile(Remote(relative));
            return;
        }
        foreach (var child in List(relative)) Delete(child.Relative, child.IsDirectory);
        client.DeleteDirectory(Remote(relative));
    }

    public void Dispose() => client.Dispose();
}
