using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AgsCopy.Core;

namespace AgsCopy.Service;

/// <summary>
/// A Volume Shadow Copy of one volume, reachable as a folder: a directory symbolic link under
/// %ProgramData%\AGS-Copy\vss points at the snapshot device. Robocopy reads from that folder,
/// so open and locked files are copied as they were when the snapshot was taken.
/// Disposing removes the link and the snapshot.
/// </summary>
public sealed class ShadowCopy : IDisposable
{
    private const int SymbolicLinkFlagDirectory = 0x1;
    private const uint VssBusy = 9;

    /// <summary>VSS takes one snapshot at a time.</summary>
    private static readonly SemaphoreSlim CreateLock = new(1, 1);
    private static readonly object RegistryLock = new();

    private static string VssDir => AppPaths.VssDir;
    private static string RegistryFile => Path.Combine(AppPaths.DataDir, "vss.json");

    public string ShadowId { get; }
    public string VolumeRoot { get; }
    public string LinkPath { get; }

    private ShadowCopy(string id, string volume, string link)
    {
        ShadowId = id;
        VolumeRoot = volume;
        LinkPath = link;
    }

    /// <summary>Volume that holds a local path: D:\ (or a mount point folder).</summary>
    public static string VolumeOf(string path)
    {
        var buffer = new StringBuilder(1024);
        if (!GetVolumePathName(path, buffer, buffer.Capacity)) throw new Win32Exception();
        return PathUtil.WithSlash(buffer.ToString());
    }

    /// <exception cref="ShadowCopyException">VSS refused or failed, with the reason.</exception>
    public static async Task<ShadowCopy> CreateAsync(string localPath, string linkName, CancellationToken ct)
    {
        var volume = VolumeOf(localPath);
        await CreateLock.WaitAsync(ct);
        try
        {
            var id = CreateSnapshot(volume, ct);
            Register(id);
            try
            {
                var device = DeviceObject(id);
                Directory.CreateDirectory(VssDir);
                var link = Path.Combine(VssDir, linkName);
                // The target needs its trailing backslash to open as a folder.
                if (!CreateSymbolicLink(link, device.TrimEnd('\\') + "\\", SymbolicLinkFlagDirectory))
                    throw new ShadowCopyException(0, "Cannot link the snapshot: " + new Win32Exception().Message);
                return new ShadowCopy(id, volume, link);
            }
            catch
            {
                DeleteSnapshot(id);
                Unregister(id);
                throw;
            }
        }
        finally
        {
            CreateLock.Release();
        }
    }

    public void Dispose()
    {
        RemoveLink(LinkPath);
        DeleteSnapshot(ShadowId);
        Unregister(ShadowId);
    }

    /// <summary>
    /// Removes snapshots and links left by a run that never finished (service killed, power
    /// loss): ClientAccessible snapshots would otherwise stay and use disk space.
    /// </summary>
    public static void CleanupLeftovers(ILogger log)
    {
        foreach (var id in Registered())
        {
            try
            {
                DeleteSnapshot(id);
                log.LogInformation("Removed leftover shadow copy {Id}", id);
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Cannot remove leftover shadow copy {Id}", id);
            }
            Unregister(id);
        }
        if (!Directory.Exists(VssDir)) return;
        foreach (var link in Directory.EnumerateDirectories(VssDir)) RemoveLink(link);
    }

    private static string CreateSnapshot(string volume, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var cls = new ManagementClass(@"\\.\root\cimv2", "Win32_ShadowCopy", null);
            using var input = cls.GetMethodParameters("Create");
            input["Volume"] = volume;
            input["Context"] = "ClientAccessible";
            using var output = cls.InvokeMethod("Create", input, null);
            var rc = Convert.ToUInt32(output["ReturnValue"]);
            if (rc == 0) return (string)output["ShadowID"];
            // Another backup program may be taking its own snapshot.
            if (rc == VssBusy && attempt < 6)
            {
                Task.Delay(TimeSpan.FromSeconds(10), ct).Wait(ct);
                continue;
            }
            throw new ShadowCopyException(rc, Reason(rc, volume));
        }
    }

    private static string DeviceObject(string id)
    {
        using var searcher = new ManagementObjectSearcher(@"\\.\root\cimv2", $"SELECT DeviceObject FROM Win32_ShadowCopy WHERE ID='{id}'");
        foreach (var o in searcher.Get())
        {
            using (o)
                return (string)o["DeviceObject"];
        }
        throw new ShadowCopyException(0, "The shadow copy disappeared right after its creation.");
    }

    private static void DeleteSnapshot(string id)
    {
        using var searcher = new ManagementObjectSearcher(@"\\.\root\cimv2", $"SELECT * FROM Win32_ShadowCopy WHERE ID='{id}'");
        foreach (var o in searcher.Get())
        {
            using var mo = (ManagementObject)o;
            mo.Delete();
        }
    }

    /// <summary>Deletes the link only: never recurse, that would walk into the snapshot.</summary>
    private static void RemoveLink(string link)
    {
        try
        {
            if (Directory.Exists(link) && new DirectoryInfo(link).LinkTarget != null) Directory.Delete(link, recursive: false);
        }
        catch (IOException)
        {
        }
    }

    private static string Reason(uint rc, string volume) => rc switch
    {
        1 => "access denied",
        2 => "invalid argument",
        3 => $"volume {volume} not found",
        4 => $"volume {volume} does not support shadow copies (FAT/exFAT, removable or network)",
        5 => "unsupported shadow copy context",
        6 => "not enough free space for the shadow copy storage",
        7 => "the volume is in use",
        8 => "too many shadow copies on the volume",
        9 => "another shadow copy is being created",
        10 => "a VSS provider vetoed the operation",
        11 => "VSS provider not registered",
        12 => "VSS provider failure",
        _ => $"unknown VSS error {rc}",
    } + $" (Win32_ShadowCopy.Create {rc})";

    // Snapshot ids created and not yet deleted, in vss.json, so a crash can be cleaned up.
    private static List<string> Registered()
    {
        lock (RegistryLock)
        {
            try
            {
                return File.Exists(RegistryFile)
                    ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(RegistryFile)) ?? []
                    : [];
            }
            catch (Exception e) when (e is IOException or JsonException)
            {
                return [];
            }
        }
    }

    private static void Register(string id) => UpdateRegistry(ids => ids.Add(id));

    private static void Unregister(string id) => UpdateRegistry(ids => ids.Remove(id));

    private static void UpdateRegistry(Action<List<string>> change)
    {
        lock (RegistryLock)
        {
            var ids = Registered();
            change(ids);
            DataDirectory.WriteAtomic(RegistryFile, JsonSerializer.Serialize(ids));
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumePathName(string fileName, StringBuilder volumePathName, int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CreateSymbolicLink(string link, string target, int flags);
}

public sealed class ShadowCopyException(uint code, string message) : Exception(message)
{
    public uint Code { get; } = code;
}
