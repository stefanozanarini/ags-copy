using System.Diagnostics;
using AgsCopy.Core.Updates;

namespace AgsCopy.App;

/// <summary>Checks GitHub for a newer release and installs its .msi package.</summary>
public static class Updater
{
    private static Task<UpdateInfo?>? _check;

    /// <summary>One check per process start; later callers share its result.</summary>
    public static Task<UpdateInfo?> CheckAsync(bool again = false)
    {
        if (_check == null || again) _check = UpdateCheck.FindAsync();
        return _check;
    }

    public static void OpenPage(UpdateInfo update) =>
        Process.Start(new ProcessStartInfo(update.PageUrl) { UseShellExecute = true });

    /// <summary>
    /// Downloads the package, then hands over to msiexec (which asks for elevation) and exits.
    /// The tray icon is started again once the setup ends, whatever its outcome.
    /// </summary>
    public static async Task InstallAsync(UpdateInfo update)
    {
        if (update.MsiUrl == null)
        {
            OpenPage(update);
            return;
        }
        var msi = await UpdateCheck.DownloadAsync(update);
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "AGS-Copy.exe");
        var log = Path.Combine(Path.GetDirectoryName(msi)!, "install.log");
        var script = $"msiexec.exe /i \"{msi}\" /passive /norestart /l*v \"{log}\" & start \"\" \"{exe}\" --tray";
        Process.Start(new ProcessStartInfo("cmd.exe", "/c " + script) { UseShellExecute = false, CreateNoWindow = true });

        // Other AGS-Copy windows of this user would keep files in use during the upgrade.
        var self = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("AGS-Copy"))
        {
            try
            {
                if (p.Id != self && p.SessionId == Process.GetCurrentProcess().SessionId) p.Kill();
            }
            catch (Exception)
            {
                // An elevated window cannot be closed from the tray: the setup asks to close it.
            }
            finally
            {
                p.Dispose();
            }
        }
        System.Windows.Application.Current.Shutdown();
    }
}
