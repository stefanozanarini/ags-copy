using System.Windows;
using AgsCopy.Core.Ipc;

namespace AgsCopy.App;

/// <summary>
/// AGS-Copy.exe            configuration window (relaunches itself elevated)
/// AGS-Copy.exe --tray     tray icon, unelevated, started at logon
/// AGS-Copy.exe --run ID   asks the service to run a job now, then exits
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

        if (!Elevation.IsElevated)
        {
            Elevation.StartElevated(args);
            Shutdown();
            return;
        }

        if (!SingleInstance("Main")) return;
        ShowMainWindow();
    }

    public void ShowMainWindow()
    {
        var old = MainWindow;
        MainWindow = new MainWindow();
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        MainWindow.Show();
        old?.Close();
    }

    private bool SingleInstance(string name)
    {
        _instance = new Mutex(true, @"Local\AGS-Copy." + name, out var created);
        if (created) return true;
        Shutdown();
        return false;
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
        _instance?.Dispose();
        base.OnExit(e);
    }
}
