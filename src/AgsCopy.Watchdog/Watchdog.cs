using System.Diagnostics;
using System.Management;
using System.ServiceProcess;
using AgsCopy.Core;
using AgsCopy.Core.Ipc;

namespace AgsCopy.Watchdog;

/// <summary>
/// Separate service that keeps the AGS-Copy service alive: starts it when it stays stopped and
/// kills and restarts it when it stays running without answering on its pipe. Crashes are also
/// covered by the service's own recovery actions; this catches hangs and stops that those miss.
/// To stop AGS-Copy on purpose, stop this service first (or set AGS-Copy to Disabled).
/// </summary>
public sealed class Watchdog(ILogger<Watchdog> log) : BackgroundService
{
    public const string ServiceName = "AGS-Copy-Watchdog";
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private readonly WatchdogPolicy _policy = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("Watching the {Service} service every {Interval}.", AppPaths.ServiceName, Interval);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var health = await ProbeAsync(ct);
                switch (_policy.Next(health))
                {
                    case WatchdogAction.Start:
                        log.LogWarning("{Service} is stopped: starting it.", AppPaths.ServiceName);
                        Start();
                        break;
                    case WatchdogAction.Restart:
                        log.LogWarning("{Service} is {Health}: killing and restarting it.", AppPaths.ServiceName, health);
                        KillAndRestart();
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                log.LogError(e, "Watchdog check failed.");
            }
            await Task.Delay(Interval, ct).ContinueWith(_ => { });
        }
    }

    private static async Task<ServiceHealth> ProbeAsync(CancellationToken ct)
    {
        if (SetupRunning()) return ServiceHealth.Ignored;
        using var sc = Find();
        if (sc == null || sc.StartType == ServiceStartMode.Disabled) return ServiceHealth.Ignored;
        switch (sc.Status)
        {
            case ServiceControllerStatus.Stopped:
                return ServiceHealth.Stopped;
            case ServiceControllerStatus.Running:
                break;
            default:
                return ServiceHealth.Changing;
        }
        try
        {
            var status = await PipeClient.CallAsync<ServiceStatus>(Commands.Status, timeout: TimeSpan.FromSeconds(30), ct: ct);
            return status != null ? ServiceHealth.Healthy : ServiceHealth.Unresponsive;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return ServiceHealth.Unresponsive;
        }
    }

    /// <summary>Windows Installer holds this mutex while a package installs: an upgrade stops the service on purpose.</summary>
    private static bool SetupRunning()
    {
        if (Mutex.TryOpenExisting(@"Global\_MSIExecute", out var m))
        {
            m.Dispose();
            return true;
        }
        return false;
    }

    private static ServiceController? Find() =>
        ServiceController.GetServices().FirstOrDefault(s => s.ServiceName.Equals(AppPaths.ServiceName, StringComparison.OrdinalIgnoreCase));

    private static void Start()
    {
        using var sc = new ServiceController(AppPaths.ServiceName);
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromMinutes(2));
    }

    private void KillAndRestart()
    {
        var pid = ProcessId();
        if (pid > 0)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
                p.WaitForExit(30_000);
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
        }
        using var sc = new ServiceController(AppPaths.ServiceName);
        sc.Refresh();
        // The service's own recovery action may already be restarting it.
        if (sc.Status == ServiceControllerStatus.Stopped) Start();
        else log.LogInformation("{Service} is {Status} after the kill.", AppPaths.ServiceName, sc.Status);
    }

    private static int ProcessId()
    {
        using var searcher = new ManagementObjectSearcher($"SELECT ProcessId FROM Win32_Service WHERE Name = '{AppPaths.ServiceName}'");
        foreach (ManagementObject o in searcher.Get())
            using (o)
                return Convert.ToInt32(o["ProcessId"]);
        return 0;
    }
}
