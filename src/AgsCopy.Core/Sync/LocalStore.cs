namespace AgsCopy.Core.Sync;

/// <summary>A local or \\server\share folder (the share already connected by the caller).</summary>
public class LocalSource(string root) : ISourceStore
{
    /// <summary>The folder as given (the service passes normalized Windows paths; tests pass Linux ones).</summary>
    protected string Root { get; } = root.Length > 3 ? root.TrimEnd('\\', '/') : root;

    protected string Full(string relative) =>
        relative.Length == 0 ? Root : Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public string Display(string relative) => Full(relative);

    public bool Exists() => Directory.Exists(Full(""));

    public IReadOnlyList<SyncEntry> List(string relative)
    {
        var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false, RecurseSubdirectories = false };
        var list = new List<SyncEntry>();
        foreach (var i in new DirectoryInfo(Full(relative)).EnumerateFileSystemInfos("*", options))
        {
            var dir = (i.Attributes & FileAttributes.Directory) != 0;
            list.Add(new SyncEntry(SyncPath.Join(relative, i.Name), dir, dir ? 0 : ((FileInfo)i).Length, i.LastWriteTimeUtc));
        }
        return list;
    }

    public Stream OpenRead(string relative) =>
        new FileStream(Full(relative), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);

    public virtual void Dispose()
    {
    }
}

public sealed class LocalTarget(string root) : LocalSource(root), ITargetStore
{
    public void CreateDirectory(string relative) => Directory.CreateDirectory(Full(relative));

    public Stream Create(string relative) =>
        new FileStream(Full(relative), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);

    public void Replace(string from, string to)
    {
        var target = new FileInfo(Full(to));
        if (target.Exists && target.IsReadOnly) target.IsReadOnly = false;
        File.Move(Full(from), Full(to), overwrite: true);
    }

    public void SetModified(string relative, DateTime utc) => File.SetLastWriteTimeUtc(Full(relative), utc);

    public void Move(string relative, string newRelative)
    {
        if (Directory.Exists(Full(relative))) Directory.Move(Full(relative), Full(newRelative));
        else File.Move(Full(relative), Full(newRelative));
    }

    public void Delete(string relative, bool isDirectory)
    {
        var path = Full(relative);
        if (isDirectory)
        {
            // Copies keep the read-only attribute, which would stop the delete.
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                if (f.IsReadOnly) f.IsReadOnly = false;
            Directory.Delete(path, recursive: true);
        }
        else
        {
            var f = new FileInfo(path);
            if (f.IsReadOnly) f.IsReadOnly = false;
            f.Delete();
        }
    }
}
