using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace AgsCopy.Core.Sync;

/// <summary>
/// What the copy engine left in a destination, folder by folder: a later run compares the source with
/// this instead of reading the whole destination again (a NAS with millions of files takes hours).
/// The source is always read in full: neither a share nor DFS tells which files changed.
/// Whatever the index gets wrong only costs a copy made again, except a file deleted from the
/// destination by hand, which comes back at the next full check (<see cref="VerifyDays"/>).
/// </summary>
public sealed class DestinationIndex
{
    /// <summary>Days after which a run reads the whole destination again and rebuilds the index.</summary>
    public const int VerifyDays = 7;

    private const string Header = "AGS-Copy destination index 1";

    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, Item>> _folders = new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct Item(bool IsDirectory, long Size, long Ticks);

    /// <summary>When a run last read every folder of the destination; default when never.</summary>
    public DateTime VerifiedUtc { get; set; }

    public int FolderCount
    {
        get { lock (_gate) return _folders.Count; }
    }

    public bool NeedsVerify(DateTime nowUtc) => VerifiedUtc == default || nowUtc - VerifiedUtc > TimeSpan.FromDays(VerifyDays) || VerifiedUtc > nowUtc.AddDays(1);

    /// <summary>The items of a folder ("" = the root), or null when the index does not know it.</summary>
    public IReadOnlyList<SyncEntry>? Folder(string relative)
    {
        lock (_gate)
        {
            if (!_folders.TryGetValue(relative, out var items)) return null;
            return items.Select(i => new SyncEntry(SyncPath.Join(relative, i.Key), i.Value.IsDirectory, i.Value.Size, new DateTime(i.Value.Ticks, DateTimeKind.Utc))).ToList();
        }
    }

    /// <summary>The items of a folder as just read from the destination.</summary>
    public void SetFolder(string relative, IEnumerable<SyncEntry> entries)
    {
        var items = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries) items[e.Name] = new Item(e.IsDirectory, e.Size, e.ModifiedUtc.Ticks);
        // A name the file cannot hold (SFTP allows control characters): that folder is read every time.
        var keep = !relative.Any(char.IsControl) && !items.Keys.Any(n => n.Any(char.IsControl));
        lock (_gate)
        {
            if (keep) _folders[relative] = items;
            else _folders.Remove(relative);
        }
    }

    /// <summary>A file written or a folder created; ignored when the index does not know its folder.</summary>
    public void Put(SyncEntry e)
    {
        var parent = SyncPath.Parent(e.Relative) ?? "";
        lock (_gate)
        {
            if (!_folders.TryGetValue(parent, out var items)) return;
            if (e.Name.Any(char.IsControl)) _folders.Remove(parent);
            else items[e.Name] = new Item(e.IsDirectory, e.Size, e.ModifiedUtc.Ticks);
        }
    }

    /// <summary>A file or folder no longer there; a folder takes what the index knew below it along.</summary>
    public void Remove(string relative, bool isDirectory)
    {
        lock (_gate)
        {
            if (_folders.TryGetValue(SyncPath.Parent(relative) ?? "", out var items)) items.Remove(relative[(relative.LastIndexOf('/') + 1)..]);
            if (!isDirectory) return;
            foreach (var key in _folders.Keys.Where(k => k.Equals(relative, StringComparison.OrdinalIgnoreCase) || k.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase)).ToList())
                _folders.Remove(key);
        }
    }

    /// <summary>Drops the folders a complete run did not come across: they are gone, or excluded now.</summary>
    public void Retain(ICollection<string> folders)
    {
        lock (_gate)
            foreach (var key in _folders.Keys.Where(k => !folders.Contains(k)).ToList())
                _folders.Remove(key);
    }

    /// <summary>The index, or null when there is none or it cannot be read (the run then reads the destination).</summary>
    public static DestinationIndex? Load(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            using var reader = new StreamReader(new GZipStream(File.OpenRead(file), CompressionMode.Decompress), Encoding.UTF8);
            if (reader.ReadLine() != Header) return null;
            var index = new DestinationIndex();
            if (reader.ReadLine() is not { } verified || !long.TryParse(verified, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)) return null;
            index.VerifiedUtc = new DateTime(ticks, DateTimeKind.Utc);
            Dictionary<string, Item>? items = null;
            while (reader.ReadLine() is { } line)
            {
                if (line.Length < 2) return null;
                var body = line[2..];
                switch (line[0])
                {
                    case 'D':
                        items = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
                        index._folders[body] = items;
                        break;
                    case 'S' when items != null:
                        items[body] = new Item(true, 0, 0);
                        break;
                    case 'F' when items != null && body.Split('\t') is [var name, var size, var time]:
                        items[name] = new Item(false, long.Parse(size, CultureInfo.InvariantCulture), long.Parse(time, CultureInfo.InvariantCulture));
                        break;
                    default:
                        return null;
                }
            }
            return index;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>Written to a new file then moved in place, so a crash never leaves half an index.</summary>
    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        lock (_gate)
        {
            using (var writer = new StreamWriter(new GZipStream(File.Create(temp), CompressionLevel.Fastest), new UTF8Encoding(false)))
            {
                writer.WriteLine(Header);
                writer.WriteLine(VerifiedUtc.Ticks.ToString(CultureInfo.InvariantCulture));
                foreach (var (folder, items) in _folders)
                {
                    writer.Write("D ");
                    writer.WriteLine(folder);
                    foreach (var (name, i) in items)
                    {
                        if (i.IsDirectory)
                        {
                            writer.Write("S ");
                            writer.WriteLine(name);
                        }
                        else
                        {
                            writer.Write("F ");
                            writer.Write(name);
                            writer.Write('\t');
                            writer.Write(i.Size.ToString(CultureInfo.InvariantCulture));
                            writer.Write('\t');
                            writer.WriteLine(i.Ticks.ToString(CultureInfo.InvariantCulture));
                        }
                    }
                }
            }
            File.Move(temp, file, overwrite: true);
        }
    }
}
