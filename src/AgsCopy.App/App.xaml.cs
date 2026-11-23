using System.Windows;
using AgsCopy.Core.Ipc;

namespace AgsCopy.App;

/// <summary>
/// AGS-Copy.exe            configuration window (relaunches itself elevated)
/// AGS-Copy.exe --tray     tray icon, unelevated, started at logon
/// AGS-Copy.exe --run ID   asks the service to run a job now, then exits
/// AGS-Copy.exe --selftest LOG   opens every window and page against the running service, writes
///                               what went wrong to LOG and exits with 1 if anything did (CI)
/// The portable build only has the configuration window, with the engine inside it.
/// </summary>
public partial class App : Application
{
    private Mutex? _instance;
    private TrayController? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        UserSettings.Load();
        var args = e.Args;

        if (PortableBuild)
        {
            StartPortable(args);
            return;
        }

        if (args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
        {
            if (!SingleInstance("Tray")) return;
            _tray = new TrayController();
            _tray.Start();
            return;
        }

        if (args.Length >= 2 && args[0].Equals("--run", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(args[1], out var id))
        {
            _ = RunJobAndExitAsync(id);
            return;
        }

        if (args.Length >= 1 && args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
        {
            _ = SelfTestAsync(args.Length > 1 ? args[1] : null);
            return;
        }

        if (!Elevation.IsElevated)
        {
            Elevation.StartElevated(args);
            Shutdown();
            return;
        }

        if (!SingleInstance("Main")) return;
        ShowMainWindow();
    }

    /// <summary>Set by <c>dotnet publish -p:AgsPortable=true</c>.</summary>
    private static readonly bool PortableBuild =
#if PORTABLE
        true;
#else
        false;
#endif

    private void StartPortable(string[] args)
    {
        if (!Elevation.IsElevated)
        {
            Elevation.StartElevated(args);
            Shutdown();
            return;
        }
        if (!SingleInstance("Portable")) return;
        try
        {
            PortableEngine.Start();
        }
        catch (Exception ex)
        {
            Ui.Error(null, ex);
            Shutdown();
            return;
        }
        ShowMainWindow();
    }

    public void ShowMainWindow()
    {
        var old = MainWindow;
        MainWindow = new MainWindow();
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        MainWindow.Show();
        if (old is MainWindow replaced) replaced.Replaced = true;
        old?.Close();
    }

    private bool SingleInstance(string name)
    {
        _instance = new Mutex(true, @"Local\AGS-Copy." + name, out var created);
        if (created) return true;
        Shutdown();
        return false;
    }

    /// <summary>
    /// The end-to-end test's look at the GUI: a XAML mistake only shows when a window loads, so every
    /// window and every page of a job is loaded once. Messages that would wait for a click are logged instead.
    /// </summary>
    private async Task SelfTestAsync(string? logFile)
    {
        var problems = new List<string>();
        Ui.Silent = message => problems.Add("Message: " + message);
        DispatcherUnhandledException += (_, e) =>
        {
            problems.Add("Unhandled: " + e.Exception);
            e.Handled = true;
        };
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var log = new List<string>();
        try
        {
            var main = new MainWindow();
            MainWindow = main;
            main.Show();
            await main.SelfTestAsync(log.Add);
            main.Close();
        }
        catch (Exception ex)
        {
            problems.Add("Failed: " + ex);
        }
        if (logFile != null)
        {
            try
            {
                File.WriteAllLines(logFile, log.Concat(problems));
            }
            catch (IOException)
            {
            }
        }
        Shutdown(problems.Count == 0 ? 0 : 1);
    }

    private async Task RunJobAndExitAsync(Guid id)
    {
        try
        {
            await PipeClient.CallAsync(Commands.RunJob, new IdRequest { Id = id });
        }
        catch (Exception ex)
        {
            Ui.Error(null, ex);
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        PortableEngine.Stop();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
