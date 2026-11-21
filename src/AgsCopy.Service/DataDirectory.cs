using System.Security.AccessControl;
using System.Security.Principal;
using AgsCopy.Core;

namespace AgsCopy.Service;

public static class DataDirectory
{
    /// <summary>
    /// Creates %ProgramData%\AGS-Copy readable only by SYSTEM and Administrators: it holds the
    /// encrypted credentials and the logs, which list every copied path. The portable data folder
    /// keeps the permissions of where it was put (a USB stick may not even have any).
    /// </summary>
    public static void Ensure()
    {
        var dir = Directory.CreateDirectory(AppPaths.DataDir);
        if (!AppPaths.Portable) Protect(dir);

        Directory.CreateDirectory(AppPaths.HistoryDir);
        Directory.CreateDirectory(AppPaths.LogsDir);
        Directory.CreateDirectory(AppPaths.ReportsDir);
        Directory.CreateDirectory(AppPaths.TempDir);
        foreach (var f in Directory.EnumerateFiles(AppPaths.TempDir))
        {
            try { File.Delete(f); } catch (IOException) { }
        }
    }

    private static void Protect(DirectoryInfo dir)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
        dir.SetAccessControl(security);
    }

    /// <summary>Writes through a temporary file so a crash never leaves half a file.</summary>
    public static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
