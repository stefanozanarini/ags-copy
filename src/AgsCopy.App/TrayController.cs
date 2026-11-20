using System.Windows.Forms;
using System.Windows.Threading;
using AgsCopy.Core.Ipc;
using AgsCopy.Core.Models;

namespace AgsCopy.App;

/// <summary>
/// The icon next to the clock. Runs unelevated in every user session, shows what the service
/// is doing and opens the (elevated) configuration window.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly DispatcherTimer _timer;
    private readonly ToolStripMenuItem _runMenu;
    private readonly ToolStripMenuItem _statusItem;
    private ServiceStatus? _status;
    private HashSet<Guid> _runningBefore = [];
    private bool _polling;

    public TrayController()
    {
        _statusItem = new ToolStripMenuItem { Enabled = false };
        _runMenu = new ToolStripMenuItem(Loc.T("tray.run"));
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem(Loc.T("tray.open"), null, (_, _) => OpenMain()) { Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_runMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(Loc.T("tray.exit"), null, (_, _) => System.Windows.Application.Current.Shutdown()));
        menu.Opening += (_, _) => BuildRunMenu();

        _icon = new NotifyIcon
        {
            Icon = TrayIcons.Get(TrayState.Offline),
            Text = "AGS-Copy",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenMain();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await PollAsync();
    }

    public void Start()
    {
        _timer.Start();
        _ = PollAsync();
    }

    private static void OpenMain() => Elevation.StartElevated();

    private void BuildRunMenu()
    {
        _runMenu.DropDownItems.Clear();
        var jobs = _status?.Jobs ?? [];
        _runMenu.Enabled = jobs.Count > 0;
        foreach (var j in jobs.OrderBy(j => j.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var id = j.Id;
            _runMenu.DropDownItems.Add(new ToolStripMenuItem(j.Name, null, (_, _) => Elevation.StartElevated("--run", id.ToString()))
            {
                Enabled = !j.Running,
            });
        }
    }

    private async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            _status = await PipeClient.CallAsync<ServiceStatus>(Commands.Status, timeout: TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            _status = null;
        }
        finally
        {
            _polling = false;
        }
        Render();
    }

    private void Render()
    {
        if (_status == null)
        {
            SetState(TrayState.Offline, Loc.T("tray.offline"));
            _runningBefore = [];
            return;
        }

        var running = _status.Jobs.Where(j => j.Running).ToList();
        foreach (var finished in _status.Jobs.Where(j => !j.Running && _runningBefore.Contains(j.Id)))
            NotifyFinished(finished);
        _runningBefore = running.Select(j => j.Id).ToHashSet();

        if (running.Count > 0)
        {
            var text = running.Count == 1
                ? Loc.F("tray.running.one", running[0].Name, Fmt.Bytes(running[0].ProgressBytes))
                : Loc.F("tray.running.many", running.Count, Fmt.Bytes(running.Sum(j => j.ProgressBytes)));
            SetState(TrayState.Running, text);
        }
        else if (_status.Jobs.Any(j => j.Enabled && j.LastRun?.Outcome == RunOutcome.Failed))
        {
            SetState(TrayState.Error, Loc.T("tray.failed"));
        }
        else if (_status.SchedulerPaused)
        {
            SetState(TrayState.Paused, Loc.T("tray.paused"));
        }
        else
        {
            var next = _status.Jobs.Where(j => j.NextRunLocal != null).MinBy(j => j.NextRunLocal);
            SetState(TrayState.Idle, next == null
                ? Loc.T("tray.idle")
                : Loc.F("tray.next", next.Name, Fmt.DateTime(next.NextRunLocal!.Value)));
        }
    }

    private void SetState(TrayState state, string text)
    {
        _icon.Icon = TrayIcons.Get(state);
        var tip = "AGS-Copy — " + text;
        // NotifyIcon.Text is limited to 127 characters.
        _icon.Text = tip.Length > 127 ? tip[..126] + "…" : tip;
        _statusItem.Text = text;
    }

    private void NotifyFinished(JobStatus job)
    {
        var r = job.LastRun;
        if (r == null) return;
        var (icon, key) = r.Outcome switch
        {
            RunOutcome.Success => (ToolTipIcon.Info, "tray.done.ok"),
            RunOutcome.Warning => (ToolTipIcon.Warning, "tray.done.warn"),
            RunOutcome.Cancelled => (ToolTipIcon.Warning, "tray.done.cancel"),
            _ => (ToolTipIcon.Error, "tray.done.fail"),
        };
        _icon.ShowBalloonTip(5000, job.Name, Loc.F(key, Fmt.Count(r.FilesCopied), Fmt.Bytes(r.BytesCopied), Fmt.Duration(r.Duration)), icon);
    }

    public void Dispose()
    {
        _timer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
    }
}
