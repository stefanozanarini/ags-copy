using System.ComponentModel;
using System.Runtime.InteropServices;
using AgsCopy.Core;
using Microsoft.Win32.SafeHandles;

namespace AgsCopy.Service;

/// <summary>
/// Windows (NTFS) compression of the copies: files stay ordinary files, opened and restored as
/// they are, and robocopy keeps comparing them by size and time as before. Only the destination
/// is touched.
/// </summary>
public static class NtfsCompression
{
    private const uint FileFileCompression = 0x10;
    private const uint FsctlSetCompression = 0x9C040;
    private const short CompressionFormatDefault = 1;
    private const long MinSize = 8 * 1024;

    /// <summary>True when the volume of the folder (local or share) supports NTFS compression.</summary>
    public static bool Supported(string folder)
    {
        var root = PathUtil.IsUnc(folder) ? PathUtil.WithSlash(PathUtil.ShareRoot(folder)!) : Path.GetPathRoot(folder);
        if (string.IsNullOrEmpty(root)) return false;
        return GetVolumeInformation(root, null, 0, out _, out _, out var flags, null, 0) && (flags & FileFileCompression) != 0;
    }

    public sealed record Result(long Files, long Bytes, long Saved, List<string> Errors);

    /// <summary>Compresses the compressible files of the tree that are not compressed yet.</summary>
    public static Result CompressTree(string root, CancellationToken ct)
    {
        long files = 0, bytes = 0, saved = 0;
        var errors = new List<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (f.Attributes.HasFlag(FileAttributes.Compressed) || f.Length < MinSize || !Compression.IsCompressible(f.Name)) continue;
            var readOnly = f.IsReadOnly;
            try
            {
                if (readOnly) f.IsReadOnly = false;
                using (var handle = File.OpenHandle(f.FullName, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                {
                    var format = CompressionFormatDefault;
                    if (!DeviceIoControl(handle, FsctlSetCompression, ref format, sizeof(short), IntPtr.Zero, 0, out _, IntPtr.Zero))
                        throw new Win32Exception();
                }
                // Called directly, so the prefix .NET adds by itself for long paths is ours to add.
                var low = GetCompressedFileSize(PathUtil.LongPath(f.FullName), out var high);
                files++;
                bytes += f.Length;
                if (low != uint.MaxValue || Marshal.GetLastWin32Error() == 0) saved += f.Length - (((long)high << 32) | low);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
            {
                if (errors.Count < 100) errors.Add(f.FullName + ": " + e.Message);
            }
            finally
            {
                if (readOnly)
                {
                    try { f.IsReadOnly = true; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        return new Result(files, bytes, saved, errors);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(string rootPathName, char[]? volumeName, int volumeNameSize, out uint serialNumber,
        out uint maxComponentLength, out uint fileSystemFlags, char[]? fileSystemName, int fileSystemNameSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, ref short inBuffer, int inBufferSize,
        IntPtr outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSize(string fileName, out uint fileSizeHigh);
}
