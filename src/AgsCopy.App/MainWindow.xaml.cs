using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AgsCopy.Core.Ipc;
using AgsCopy.Core.Models;
using AgsCopy.Core.Updates;

namespace AgsCopy.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<JobRow> _rows = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _online;
    private bool _refreshing;
    private bool _loadingLanguage;
    private UpdateInfo? _update;

    public MainWindow()
    {
        InitializeComponent();
        Icon = TrayIcons.WindowIcon();
        JobsGrid.ItemsSource = _rows;

        _loadingLanguage = true;
        LanguageBox.SelectedIndex = Loc.Language == "en" ? 1 : 0;
        _loadingLanguage = false;

        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) =>
        {
            await RefreshAsync();
            _timer.Start();
            ShowUpdate(await Updater.CheckAsync());
        };
        Closed += (_, _) => _timer.Stop();
        UpdateButtons();
    }

    private JobRow? Selected => JobsGrid.SelectedItem as JobRow;

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var status = await PipeClient.CallAsync<ServiceStatus>(Commands.Status, timeout: TimeSpan.FromSeconds(5));
            _online = status != null;
            if (status != null) Apply(status);
        }
        catch (Exception e)
        {
            _online = false;
            StatusText.Text = Ui.Describe(e);
        }
        finally
        {
            _refreshing = false;
        }
        OfflineBanner.Visibility = _online ? Visibility.Collapsed : Visibility.Visible;
        UpdateButtons();
    }

    private void Apply(ServiceStatus status)
    {
        var byId = status.Jobs.ToDictionary(j => j.Id);
        for (var i = _rows.Count - 1; i >= 0; i--)
        {
            if (byId.TryGetValue(_rows[i].Id, out var s)) _rows[i].Update(s);
            else _rows.RemoveAt(i);
        }
        foreach (var s in status.Jobs.Where(j => _rows.All(r => r.Id != j.Id)).OrderBy(j => j.Name, StringComparer.CurrentCultureIgnoreCase))
            _rows.Add(new JobRow(s));

        EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.IsChecked = status.SchedulerPaused;
        PausedBanner.Visibility = status.SchedulerPaused ? Visibility.Visible : Visibility.Collapsed;
        var running = status.Jobs.Count(j => j.Running);
        StatusText.Text = Loc.F("main.status", status.Version, status.Jobs.Count, running);
    }

    private void UpdateButtons()
    {
        var sel = Selected;
        NewButton.IsEnabled = _online;
        EditButton.IsEnabled = DuplicateButton.IsEnabled = _online && sel != null;
        DeleteButton.IsEnabled = _online && sel is { Running: false };
        RunButton.IsEnabled = _online && sel is { Running: false };
        StopButton.IsEnabled = _online && sel is { Running: true };
        HistoryButton.IsEnabled = _online && sel != null;
        PauseButton.IsEnabled = ReportButton.IsEnabled = _online;
    }

    private void Jobs_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void Jobs_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected != null) Edit_Click(sender, e);
    }

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        var job = new CopyJob { Name = Loc.T("editor.newname") };
        if (new JobEditorWindow(job, isNew: true) { Owner = this }.ShowDialog() == true) await RefreshAsync();
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        var job = await LoadSelectedAsync();
        if (job == null) return;
        if (new JobEditorWindow(job, isNew: false) { Owner = this }.ShowDialog() == true) await RefreshAsync();
    }

    private async void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        var job = await LoadSelectedAsync();
        if (job == null) return;
        job.Id = Guid.NewGuid();
        job.Name = Loc.F("editor.copyof", job.Name);
        // Stored passwords belong to the original job: they must be typed again.
        foreach (var c in new[] { job.Source.Credential, job.Destination.Credential })
            if (c != null) c.HasPassword = false;
        if (new JobEditorWindow(job, isNew: true) { Owner = this }.ShowDialog() == true) await RefreshAsync();
    }

    private async Task<CopyJob?> LoadSelectedAsync()
    {
        if (Selected is not { } row) return null;
        try
        {
            return await PipeClient.CallAsync<CopyJob>(Commands.GetJob, new IdRequest { Id = row.Id });
        }
        catch (Exception ex)
        {
            Ui.Error(this, ex);
            return null;
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        if (!Ui.Confirm(this, Loc.F("main.confirmdelete", row.Name))) return;
        await CallAsync(Commands.DeleteJob, new IdRequest { Id = row.Id });
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        if (row.DeletesFiles && !Ui.Confirm(this, Loc.F("main.confirmrun", row.Name, row.ModeText), danger: true)) return;
        await CallAsync(Commands.RunJob, new IdRequest { Id = row.Id });
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        if (!Ui.Confirm(this, Loc.F("main.confirmstop", row.Name))) return;
        await CallAsync(Commands.CancelJob, new IdRequest { Id = row.Id });
    }

    private void History_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row) new HistoryWindow(row.Id, row.Name) { Owner = this }.Show();
    }

    private async void Report_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ReportViewer.OpenIndexAsync();
        }
        catch (Exception ex)
        {
            Ui.Error(this, ex);
        }
    }

    private async void Pause_Click(object sender, RoutedEventArgs e) =>
        await CallAsync(Commands.PauseScheduler, new BoolRequest { Value = PauseButton.IsChecked == true });

    private async Task CallAsync(string command, object payload)
    {
        try
        {
            await PipeClient.CallAsync(command, payload);
        }
        catch (Exception ex)
        {
            Ui.Error(this, ex);
        }
        await RefreshAsync();
    }

    private void ShowUpdate(UpdateInfo? update)
    {
        _update = update;
        UpdateBanner.Visibility = update == null ? Visibility.Collapsed : Visibility.Visible;
        if (update != null) UpdateText.Text = Loc.F("update.available", update.Version, UpdateCheck.Current);
    }

    private async void UpdateInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_update == null) return;
        if (_rows.Any(r => r.Running) && !Ui.Confirm(this, Loc.T("update.running"), danger: true)) return;
        UpdateInstallButton.IsEnabled = false;
        UpdateText.Text = Loc.F("update.downloading", _update.Version);
        try
        {
            await Updater.InstallAsync(_update);
        }
        catch (Exception ex)
        {
            Ui.Error(this, Loc.F("update.failed", ex.Message));
            UpdateInstallButton.IsEnabled = true;
            ShowUpdate(_update);
        }
    }

    private void UpdateNotes_Click(object sender, RoutedEventArgs e)
    {
        if (_update != null) Updater.OpenPage(_update);
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingLanguage || LanguageBox.SelectedItem is not ComboBoxItem { Tag: string lang } || lang == Loc.Language) return;
        Loc.Language = lang;
        UserSettings.Current.Language = lang;
        UserSettings.Save();
        // Texts are resolved when a window loads: open a fresh one.
        ((App)Application.Current).ShowMainWindow();
    }
}
