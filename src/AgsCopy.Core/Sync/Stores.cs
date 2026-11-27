namespace AgsCopy.Core.Sync;

/// <summary>A file or folder below a store's root. <see cref="Relative"/> uses '/' on every store.</summary>
/// <param name="Link">A junction or link the source does not go through: nothing is copied from it, and a mirror leaves its namesake in the destination alone.</param>
public sealed record SyncEntry(string Relative, bool IsDirectory, long Size, DateTime ModifiedUtc, bool Link = false)
{
    public string Name => Relative[(Relative.LastIndexOf('/') + 1)..];
}

/// <summary>
/// What the engine may do with the source: list and read, nothing else. The source is handed to
/// the engine only through this interface, so no code path of the engine can change it.
/// </summary>
public interface ISourceStore : IDisposable
{
    /// <summary>Full path of an item, for logs and reports.</summary>
    string Display(string relative);

    bool Exists();

    /// <summary>Items directly inside a folder ("" = the root). Links and reparse points are left out, unless the store follows them.</summary>
    IReadOnlyList<SyncEntry> List(string relative);

    Stream OpenRead(string relative);
}

/// <summary>The destination: everything the copy needs to write, replace, archive and delete.</summary>
public interface ITargetStore : ISourceStore
{
    void CreateDirectory(string relative);

    /// <summary>A file there (not a folder).</summary>
    bool FileExists(string relative);

    /// <summary>A new file (overwritten if it exists), to be moved in place with <see cref="Replace"/>.</summary>
    Stream Create(string relative);

    /// <summary>Moves <paramref name="from"/> onto <paramref name="to"/>, replacing a file already there.</summary>
    void Replace(string from, string to);

    void SetModified(string relative, DateTime utc);

    /// <summary>Moves a file or folder elsewhere in the store (versions); the parent of the target must exist.</summary>
    void Move(string relative, string newRelative);

    void Delete(string relative, bool isDirectory);
}

public static class SyncPath
{
    public static string Join(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;

    public static string? Parent(string relative)
    {
        var i = relative.LastIndexOf('/');
        return i < 0 ? (relative.Length == 0 ? null : "") : relative[..i];
    }

    /// <summary>Whether <paramref name="relative"/> lies inside the folder <paramref name="folder"/>.</summary>
    public static bool IsUnder(string relative, string folder) =>
        relative.Length > folder.Length + 1 && relative[folder.Length] == '/' && relative.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
}
