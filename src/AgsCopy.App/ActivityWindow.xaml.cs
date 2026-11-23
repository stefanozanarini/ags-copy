using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using AgsCopy.Core.Ipc;

namespace AgsCopy.App;

/// <summary>One process of a running job, updated in place so the grid keeps its scroll and selection.</summary>
public sealed class ProcessRow : INotifyPropertyChanged
{
    private ProcessActivity _p = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(ProcessActivity p)
    {
        _p = p;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public string Label => _p.Label;
    public string State => _p.State == ProcessState.Done && _p.ExitCode is { } code
        ? Loc.F("activity.state.Done.code", code)
        : Loc.T("activity.state." + _p.State);
    public string Files => Fmt.Count(_p.Files);
    public string Bytes => Fmt.Bytes(_p.Bytes);
    public string Errors => _p.Errors == 0 ? "" : Fmt.Count(_p.Errors);
    public string Time => _p.StartedUtc is { } start ? Fmt.Duration((_p.EndedUtc ?? DateTime.UtcNow) - start) : "";
    public string Current => _p.State switch
    {
        ProcessState.Waiting => _p.Source,
        ProcessState.Done => "",
        _ => _p.Current ?? _p.Source,
    };
}

/// <summary>
/// What a running job is doing, refreshed every second: the phase before the copy, the walks looking
/// for links, each robocopy process with the file it is on, and robocopy's latest lines.
/// </summary>
public partial class ActivityWindow : Window
{
    private readonly Guid _jobId;
    private readonly ObservableCollection<ProcessRow> _rows = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _busy;
    private string _lastLines = "";

    public ActivityWindow(Guid jobId, string jobName)
    {
        InitializeComponent();
        _jobId = jobId;
        Title = Loc.F("activity.title", jobName);
        Heading.Text = jobName;
        ProcessGrid.ItemsSource = _rows;
        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) =>
        {
            await RefreshAsync();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var a = await PipeClient.CallAsync<JobActivity>(Commands.JobActivity, new IdRequest { Id = _jobId }, TimeSpan.FromSeconds(10)) ?? new JobActivity();
            Show(a);
        }
        catch (Exception e)
        {
            // The service may be too busy to answer now: keep what is shown and say so.
            PhaseText.Text = Loc.F("activity.noanswer", Ui.Describe(e));
        }
        finally
        {
            _busy = false;
        }
    }

    private void Show(JobActivity a)
    {
        if (!a.Running)
        {
            Summary.Text = Loc.T("activity.notrunning");
            PhaseText.Text = "";
            CurrentText.Text = "";
            foreach (var r in _rows) r.Update(new ProcessActivity());
            _timer.Stop();
            return;
        }

        var elapsed = DateTime.UtcNow - (a.StartedUtc ?? DateTime.UtcNow);
        Summary.Text = a.TotalsKnown && (a.TotalBytes > 0 || a.TotalFiles > 0)
            ? Loc.F("activity.summary.total", Fmt.Bytes(a.Bytes), Fmt.Bytes(a.TotalBytes), Fmt.Count(a.Files), Fmt.Count(a.TotalFiles), Fmt.Duration(elapsed))
            : Loc.F("activity.summary", Fmt.Bytes(a.Bytes), Fmt.Count(a.Files), Fmt.Duration(elapsed));
        PhaseText.Text = a.Phase == null ? "" : Loc.F(a.Phase, a.PhaseDetail ?? "");
        CurrentText.Text = a.Current == null ? "" : Loc.F("activity.current", a.Current);
        WalksList.ItemsSource = a.Walks.Select(w => Loc.F(w.Done ? "activity.walk.done" : "activity.walk", w.Source, Fmt.Count(w.Folders), Fmt.Count(w.Links))
            + (w is { Done: false, Current: { } at } ? " " + Loc.F("activity.walk.at", at) : "")).ToList();

        while (_rows.Count < a.Processes.Count) _rows.Add(new ProcessRow());
        for (var i = 0; i < a.Processes.Count; i++) _rows[i].Update(a.Processes[i]);

        var lines = string.Join(Environment.NewLine, a.Lines);
        if (lines != _lastLines)
        {
            _lastLines = lines;
            LinesBox.Text = lines;
            if (FollowBox.IsChecked == true) LinesBox.ScrollToEnd();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
