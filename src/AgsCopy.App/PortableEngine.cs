using AgsCopy.Core.Ipc;
using AgsCopy.Service;
using Microsoft.Extensions.Hosting;

namespace AgsCopy.App;

/// <summary>
/// Runs the service's engine inside the portable exe: same job store, executor and pipe server,
/// on a pipe of this process only, while the window is open. Jobs, history and reports go to
/// AGS-Copy-Data next to the exe, so they travel with it (or to %LocalAppData% when that folder is
/// read-only).
/// </summary>
public static class PortableEngine
{
    private static IHost? _host;

    public static void Start()
    {
        AppPaths.UsePortable(DataDirectory());
        Pipe.Name = "AgsCopy.Portable." + Environment.ProcessId;
        _host = ServiceHost.Create([], windowsService: false);
        _host.Start();
    }

    /// <summary>Stops running jobs (robocopy processes are killed) and the scheduler.</summary>
    public static void Stop()
    {
        if (_host == null) return;
        try
        {
            _host.StopAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        }
        finally
        {
            _host.Dispose();
            _host = null;
        }
    }

    private static string DataDirectory()
    {
        var beside = Path.Combine(Path.GetDirectoryName(Elevation.ExePath) ?? AppContext.BaseDirectory, "AGS-Copy-Data");
        try
        {
            Directory.CreateDirectory(beside);
            var probe = Path.Combine(beside, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return beside;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AGS-Copy-Portable");
        }
    }
}
