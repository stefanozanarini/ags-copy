using System.Windows;
using System.Windows.Controls;
using AgsCopy.Core.Ipc;
using AgsCopy.Core.Models;

namespace AgsCopy.App;

public sealed class RunRow(RunRecord r)
{
    public RunRecord Record => r;
    public string Start => Fmt.DateTime(r.StartUtc.ToLocalTime());
    public string Duration => Fmt.Duration(r.Duration);
    public string OutcomeKey => r.Outcome.ToString();
    public string OutcomeText => Loc.T("outcome." + r.Outcome);
    public string Trigger => Loc.T("trigger." + r.Trigger);
    public string Files => Fmt.Count(r.FilesCopied);
    public string Bytes => Fmt.Bytes(r.BytesCopied);
    public string Failed => Fmt.Count(Math.Max(r.FilesFailed, r.Errors));
    public string Archived => Fmt.Count(r.VersionsArchived);
    public int ExitCode => r.ExitCode;
    public string Message => r.Message ?? "";
}

public partial class HistoryWindow : Window
{
    private readonly Guid _jobId;

    public HistoryWindow(Guid jobId, string jobName)
    {
        InitializeComponent();
        _jobId = jobId;
        Title = Loc.F("history.title", jobName);
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var runs = await PipeClient.CallAsync<List<RunRecord>>(Commands.History, new IdRequest { Id = _jobId }) ?? [];
            RunsGrid.ItemsSource = runs.Select(r => new RunRow(r)).ToList();
        }
        catch (Exception e)
        {
            Ui.Error(this, e);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void Runs_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        LogButton.IsEnabled = ReportButton.IsEnabled = RunsGrid.SelectedItem is RunRow;

    private async void Report_Click(object sender, RoutedEventArgs e)
    {
        if (RunsGrid.SelectedItem is not RunRow row) return;
        try
        {
            if (!await ReportViewer.OpenRunAsync(_jobId, row.Record.Id)) Ui.Error(this, Loc.T("history.noreport"));
        }
        catch (Exception ex)
        {
            Ui.Error(this, ex);
        }
    }

    private async void Log_Click(object sender, RoutedEventArgs e)
    {
        if (RunsGrid.SelectedItem is not RunRow row) return;
        try
        {
            var log = await PipeClient.CallAsync<LogResponse>(Commands.ReadLog, new LogRequest { JobId = _jobId, RunId = row.Record.Id });
            var text = log?.Text ?? "";
            if (log?.Truncated == true) text = Loc.T("log.truncated") + Environment.NewLine + Environment.NewLine + text;
            new TextViewerWindow(Loc.F("log.title", row.Start), text) { Owner = this }.Show();
        }
        catch (Exception ex)
        {
            Ui.Error(this, ex);
        }
    }
}
