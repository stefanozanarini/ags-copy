using System.Runtime.InteropServices;
using AgsCopy.Core;
using Microsoft.Win32.SafeHandles;

namespace AgsCopy.Service;

/// <summary>
/// What Windows knows about the links of share paths: whether a path is in a DFS namespace, which
/// share a DFS link points to, which folders below a source are DFS links or directory junctions, and
/// the identity of a folder (to tell when a junction leads back to where it is).
/// </summary>
public static class DfsClient
{
    private const uint ReparseTagDfs = 0x8000000A;
    private const uint ReparseTagMountPoint = 0xA0000003;
    private const uint ReparseTagSymlink = 0xA000000C;
    private const uint FileReadAttributes = 0x80;
    private const uint FileShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int StorageStateActive = 0x4;
    private const int FindExInfoBasic = 1;
    private const int FindExSearchLimitToDirectories = 1;
    private const int FindFirstExLargeFetch = 2;

    /// <summary>The namespace root or link a path belongs to (\\domain\ns or \\domain\ns\link); null outside DFS.</summary>
    public static string? EntryOf(string path)
    {
        if (!PathUtil.IsUnc(path)) return null;
        try
        {
            if (NetDfsGetClientInfo(PathUtil.NormalizeDir(path), null, null, 1, out var buffer) != 0) return null;
            try
            {
                var entry = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer));
                return entry == null ? null : @"\\" + entry.TrimStart('\\');
            }
            finally
            {
                NetApiBufferFree(buffer);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>A link entry has more than server and namespace: \\domain\ns\link.</summary>
    private static bool IsLinkEntry(string entry) =>
        entry.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries).Length > 2;

    /// <summary>
    /// The physical folder behind a DFS path (\\server\share\rest, from the active target), or the
    /// path itself outside DFS. Used to tell whether two paths are really the same place.
    /// </summary>
    public static string Resolve(string path)
    {
        try
        {
            if (!PathUtil.IsUnc(path)) return path;
            if (NetDfsGetClientInfo(PathUtil.NormalizeDir(path), null, null, 3, out var buffer) != 0) return path;
            try
            {
                var info = Marshal.PtrToStructure<DfsInfo3>(buffer);
                var entry = @"\\" + (info.EntryPath ?? "").TrimStart('\\');
                var size = Marshal.SizeOf<DfsStorageInfo>();
                for (var i = 0; i < info.NumberOfStorages; i++)
                {
                    var s = Marshal.PtrToStructure<DfsStorageInfo>(info.Storage + i * size);
                    if ((s.State & StorageStateActive) == 0 && info.NumberOfStorages > 1) continue;
                    var target = $@"\\{s.ServerName}\{s.ShareName}";
                    var rest = PathUtil.NormalizeDir(path).Length > entry.Length && PathUtil.IsUnder(path, entry)
                        ? PathUtil.NormalizeDir(path)[entry.Length..].Trim('\\')
                        : "";
                    return rest.Length == 0 ? target : PathUtil.Combine(target, rest);
                }
                return path;
            }
            finally
            {
                NetApiBufferFree(buffer);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return path;
        }
    }

    /// <summary>
    /// The links below a share source to follow. Without junctions only a DFS namespace is looked at:
    /// nothing when the source is not in one, or is inside a link already (a link's target is a plain
    /// share, which the DFS client reaches by itself). With junctions the whole share is walked.
    /// </summary>
    public static LinkScan FindLinks(string source, Exclusions exclusions, bool followJunctions, CancellationToken ct)
    {
        var src = PathUtil.NormalizeDir(source);
        if (!PathUtil.IsUnc(src)) return new LinkScan();
        if (followJunctions) return LinkedFolders.Find(src, Folders, FolderId, exclusions, true, ct);
        if (EntryOf(src) is not { } entry || IsLinkEntry(entry)) return new LinkScan();
        return LinkedFolders.Find(src, Folders, null, exclusions, false, ct);
    }

    private static string LongPath(string path) =>
        PathUtil.IsUnc(path) ? @"\\?\UNC\" + PathUtil.NormalizeDir(path).TrimStart('\\') : @"\\?\" + PathUtil.NormalizeDir(path);

    /// <summary>
    /// Volume and file id of a folder, its links followed (opening a junction opens its target); null
    /// when it cannot be opened.
    /// </summary>
    public static string? FolderId(string path)
    {
        using var h = CreateFileW(LongPath(path), FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (h.IsInvalid || !GetFileInformationByHandle(h, out var info)) return null;
        return $"{info.VolumeSerialNumber:X8}-{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
    }

    /// <summary>Sub-folders, with the reparse tag that tells DFS links from junctions and symbolic links.</summary>
    private static IEnumerable<FolderEntry> Folders(string path)
    {
        var list = new List<FolderEntry>();
        var pattern = LongPath(path).TrimEnd('\\') + @"\*";
        using var handle = FindFirstFileExW(pattern, FindExInfoBasic, out var data, FindExSearchLimitToDirectories, IntPtr.Zero, FindFirstExLargeFetch);
        if (handle.IsInvalid) return list;
        do
        {
            if ((data.dwFileAttributes & (uint)FileAttributes.Directory) == 0 || data.cFileName is "." or "..") continue;
            var reparse = (data.dwFileAttributes & (uint)FileAttributes.ReparsePoint) != 0;
            // Over SMB the tag comes with the listing. A DFS link has IO_REPARSE_TAG_DFS; when a server reports
            // a tag that is neither that nor a junction or symbolic link, the DFS client is asked.
            var tag = reparse ? data.dwReserved0 : 0;
            var junction = tag == ReparseTagMountPoint;
            var link = reparse && (tag == ReparseTagDfs
                || (!junction && tag != ReparseTagSymlink && IsLinkFolder(PathUtil.Combine(path, data.cFileName))));
            list.Add(new FolderEntry(data.cFileName, link, junction, reparse));
        }
        while (FindNextFileW(handle, out data));
        return list;
    }

    private static bool IsLinkFolder(string path) =>
        EntryOf(path) is { } entry && string.Equals(entry, PathUtil.NormalizeDir(path), StringComparison.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DfsInfo3
    {
        public string? EntryPath;
        public string? Comment;
        public int State;
        public int NumberOfStorages;
        public IntPtr Storage;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DfsStorageInfo
    {
        public int State;
        public string ServerName;
        public string ShareName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindData
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    private sealed class FindHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => FindClose(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetDfsGetClientInfo(string dfsEntryPath, string? serverName, string? shareName, int level, out IntPtr buffer);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern FindHandle FindFirstFileExW(string fileName, int infoLevel, out Win32FindData data, int searchOp, IntPtr filter, int flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool FindNextFileW(FindHandle handle, out Win32FindData data);

    [DllImport("kernel32.dll")]
    private static extern bool FindClose(IntPtr handle);
}
