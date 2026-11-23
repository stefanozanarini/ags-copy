using System.Windows.Forms;
using System.Windows.Threading;
using AgsCopy.Core.Ipc;
using AgsCopy.Core.Models;
using AgsCopy.Core.Updates;

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
    private readonly ToolStripMenuItem _stopMenu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _updateItem;
    private readonly DispatcherTimer _updateTimer;
    private UpdateInfo? _update;
    private ServiceStatus? _status;
    private HashSet<Guid> _runningBefore = [];
    private bool _polling;

    public TrayController()
    {
        _statusItem = new ToolStripMenuItem { Enabled = false };
        _runMenu = new ToolStripMenuItem(Loc.T("tray.run"));
        _stopMenu = new ToolStripMenuItem(Loc.T("tray.stop"));
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem(Loc.T("tray.open"), null, (_, _) => OpenMain()) { Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(_statusItem);
        _updateItem = new ToolStripMenuItem("", null, async (_, _) => await InstallUpdateAsync()) { Visible = false };
        menu.Items.Add(_updateItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_runMenu);
        menu.Items.Add(_stopMenu);
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

        // At start, then once a day: the tray stays up for as long as the user is logged on.
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(24) };
        _updateTimer.Tick += async (_, _) => await CheckUpdateAsync(again: true);
        _icon.BalloonTipClicked += async (_, _) =>
        {
            if (_balloonIsUpdate) await InstallUpdateAsync();
        };
    }

    private bool _balloonIsUpdate;

    private async Task CheckUpdateAsync(bool again)
    {
        var update = await Updater.CheckAsync(again);
        if (update == null || update.Version == _update?.Version) return;
        _update = update;
        _updateItem.Text = Loc.F("update.menu", update.Version);
        _updateItem.Visible = true;
        _balloonIsUpdate = true;
        _icon.ShowBalloonTip(10000, "AGS-Copy", Loc.F("update.balloon", update.Version), ToolTipIcon.Info);
    }

    private async Task InstallUpdateAsync()
    {
        if (_update == null) return;
        var running = _status?.Jobs.Where(j => j.Running).Select(j => j.Name).ToList() ?? [];
        var question = Loc.F("update.confirm", _update.Version, UpdateCheck.Current)
                       + (running.Count > 0 ? Environment.NewLine + Environment.NewLine + Loc.T("update.running") : "");
        if (MessageBox.Show(question, "AGS-Copy", MessageBoxButtons.YesNo, running.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        try
        {
            _icon.ShowBalloonTip(5000, "AGS-Copy", Loc.F("update.downloading", _update.Version), ToolTipIcon.Info);
            await Updater.InstallAsync(_update);
        }
        catch (Exception e)
        {
            MessageBox.Show(Loc.F("update.failed", e.Message), "AGS-Copy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public void Start()
    {
        _timer.Start();
        _ = PollAsync();
        _updateTimer.Start();
        _ = CheckUpdateAsync(again: false);
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

        // Stopping is for administrators: the elevated instance asks the service, after a confirmation.
        _stopMenu.DropDownItems.Clear();
        var running = jobs.Where(j => j.Running).OrderBy(j => j.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _stopMenu.Enabled = running.Count > 0;
        foreach (var j in running)
        {
            var (id, name) = (j.Id, j.Name);
            _stopMenu.DropDownItems.Add(new ToolStripMenuItem($"{name} ({Progress.Short(j)})", null, (_, _) =>
            {
                if (MessageBox.Show(Loc.F("main.confirmstop", name), "AGS-Copy", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                        MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                    Elevation.StartElevated("--stop", id.ToString());
            }));
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
                ? Loc.F("tray.running.one", running[0].Name, Progress.Short(running[0]))
                : Loc.F("tray.running.many", running.Count, string.Join(", ", running.Select(j => Progress.Short(j))));
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
        _balloonIsUpdate = false;
        _icon.ShowBalloonTip(5000, job.Name, Loc.F(key, Fmt.Count(r.FilesCopied), Fmt.Bytes(r.BytesCopied), Fmt.Duration(r.Duration)), icon);
    }

    public void Dispose()
    {
        _timer.Stop();
        _updateTimer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
    }
}
