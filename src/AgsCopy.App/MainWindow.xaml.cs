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
        JobTree.ItemsSource = _rows;

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
        Closing += (_, e) =>
        {
            if (!Replaced && _editor is { IsDirty: true } && !Ui.Confirm(this, Loc.T("editor.unsaved.close"))) e.Cancel = true;
        };
        if (AppPaths.Portable)
        {
            Title = HeaderTitle.Text = "AGS-Copy Portable";
            // Closing the window ends the engine and any copy it is running.
            Closing += (_, e) =>
            {
                if (!Replaced && _rows.Any(r => r.Running) && !Ui.Confirm(this, Loc.T("portable.closing"), danger: true)) e.Cancel = true;
            };
        }
        UpdateButtons();
    }

    /// <summary>True when a fresh window takes this one's place (language change): nothing ends.</summary>
    public bool Replaced { get; set; }

    /// <summary>The job selected in the tree (itself or one of its pages).</summary>
    private JobRow? Selected => JobTree.SelectedItem switch
    {
        JobRow r => r,
        PageNode p => p.Row,
        _ => null,
    };

    private JobEditorView? _editor;
    private object? _shownNode;
    private bool _reverting;

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

        ShowEmptyState();
        PauseButton.IsChecked = status.SchedulerPaused;
        PausedBanner.Visibility = status.SchedulerPaused ? Visibility.Visible : Visibility.Collapsed;
        var running = status.Jobs.Count(j => j.Running);
        StatusText.Text = AppPaths.Portable
            ? Loc.F("portable.status", status.Version, status.Jobs.Count, running, AppPaths.DataDir)
            : Loc.F("main.status", status.Version, status.Jobs.Count, running);
    }

    private void UpdateButtons()
    {
        var sel = Selected;
        NewButton.IsEnabled = _online;
        DuplicateButton.IsEnabled = _online && sel != null;
        DeleteButton.IsEnabled = _online && sel is { Running: false };
        RunButton.IsEnabled = _online && sel is { Running: false };
        StopButton.IsEnabled = _online && sel is { Running: true };
        HistoryButton.IsEnabled = RestoreButton.IsEnabled = _online && sel != null;
        PauseButton.IsEnabled = ReportButton.IsEnabled = _online;
    }

    private void ShowEmptyState()
    {
        EmptyState.Visibility = _editor == null ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = Loc.T(_rows.Count == 0 ? "main.empty.title" : "main.select.title");
        EmptyText.Text = Loc.T(_rows.Count == 0 ? "main.empty.text" : "main.select.text");
    }

    /// <summary>Unsaved changes in the editor are lost only when the user says so.</summary>
    private bool LeaveEditor() => _editor is not { IsDirty: true } || Ui.Confirm(this, Loc.T("editor.unsaved.leave"));

    private async void JobTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        UpdateButtons();
        if (_reverting || e.NewValue == null) return;
        var row = e.NewValue as JobRow ?? (e.NewValue as PageNode)?.Row;
        var page = (e.NewValue as PageNode)?.Index ?? 0;
        if (row == null) return;

        if (_editor != null && _editor.JobId == row.Id && !_editor.IsNew)
        {
            _editor.Page = page;
            _shownNode = e.NewValue;
            return;
        }
        if (!LeaveEditor())
        {
            // Back to the node of the job being edited.
            _reverting = true;
            SetSelected(e.NewValue, false);
            if (_shownNode != null) SetSelected(_shownNode, true);
            _reverting = false;
            return;
        }
        row.IsExpanded = true;
        _shownNode = e.NewValue;
        try
        {
            var job = await PipeClient.CallAsync<CopyJob>(Commands.GetJob, new IdRequest { Id = row.Id });
            if (job != null) ShowEditor(job, isNew: false, page);
        }
        catch (Exception ex)
        {
            Ui.Error(this, ex);
        }
    }

    private static void SetSelected(object node, bool value)
    {
        if (node is JobRow r) r.IsSelected = value;
        else if (node is PageNode p) p.IsSelected = value;
    }

    private void ShowEditor(CopyJob job, bool isNew, int page = 0)
    {
        var editor = new JobEditorView(job, isNew) { Page = page };
        editor.Saved += async saved =>
        {
            await RefreshAsync();
            // The saved job, at the page it was saved from.
            var row = _rows.FirstOrDefault(r => r.Id == saved.Id);
            if (row == null) return;
            _reverting = true;
            row.IsExpanded = true;
            row.Pages[editor.Page].IsSelected = true;
            _shownNode = row.Pages[editor.Page];
            _reverting = false;
            ShowEditor(saved, isNew: false, editor.Page);
        };
        editor.Discarded += async () =>
        {
            if (editor.IsNew)
            {
                CloseEditor();
                return;
            }
            try
            {
                var fresh = await PipeClient.CallAsync<CopyJob>(Commands.GetJob, new IdRequest { Id = editor.JobId });
                if (fresh != null) ShowEditor(fresh, isNew: false, editor.Page);
            }
            catch (Exception ex)
            {
                Ui.Error(this, ex);
            }
        };
        _editor = editor;
        EditorHost.Content = editor;
        ShowEmptyState();
    }

    private void CloseEditor()
    {
        _editor = null;
        EditorHost.Content = null;
        _shownNode = null;
        ShowEmptyState();
    }

    /// <summary>A new job opens in the right pane, with nothing selected in the tree until it is saved.</summary>
    private void OpenNew(CopyJob job)
    {
        if (!LeaveEditor()) return;
        _reverting = true;
        if (JobTree.SelectedItem != null) SetSelected(JobTree.SelectedItem, false);
        _reverting = false;
        _shownNode = null;
        ShowEditor(job, isNew: true);
    }

    /// <summary>
    /// For <c>--selftest</c>: shows every page of the first jobs, the endpoint, layout, restore and history
    /// windows, and a new job. Throws when something does not appear.
    /// </summary>
    internal async Task SelfTestAsync(Action<string> log)
    {
        // The refresh of Loaded may still be on its way: a second one returns at once while it is.
        for (var i = 0; i < 60 && _rows.Count == 0; i++)
        {
            await RefreshAsync();
            if (_rows.Count == 0) await Task.Delay(500);
        }
        if (_rows.Count == 0) throw new InvalidOperationException("No jobs to show.");
        foreach (var row in _rows.Take(4).ToList())
        {
            row.IsExpanded = true;
            await Task.Delay(300);
            foreach (var page in row.Pages)
            {
                page.IsSelected = true;
                await Task.Delay(800);
                if (_editor?.JobId != row.Id || _editor.Page != page.Index)
                    throw new InvalidOperationException($"{row.Name}: page {page.Title} not shown.");
                _editor.UpdateLayout();
            }
            log($"ok   {row.Name}: {row.Pages.Count} pages");
        }

        var job = await PipeClient.CallAsync<CopyJob>(Commands.GetJob, new IdRequest { Id = _rows[0].Id }) ?? throw new InvalidOperationException("No job.");
        foreach (var w in new Window[]
                 {
                     new EndpointWindow(new EndpointViewModel(job.Source), job.Id, false, false, true),
                     new EndpointWindow(new EndpointViewModel(job.Destination), job.Id, true, false, false),
                     new LayoutPreviewWindow(job.ForSource(0), job.Id),
                     new RestoreWindow(job.Id, job.Name),
                     new HistoryWindow(job.Id, job.Name),
                 })
        {
            w.Owner = this;
            w.Show();
            await Task.Delay(1500);
            w.Close();
            log($"ok   {w.GetType().Name}");
        }

        New_Click(this, new RoutedEventArgs());
        await Task.Delay(500);
        if (_editor is not { IsNew: true }) throw new InvalidOperationException("New job not shown.");
        log("ok   new job");
    }

    private void New_Click(object sender, RoutedEventArgs e) => OpenNew(new CopyJob { Name = Loc.T("editor.newname") });

    private async void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        var job = await LoadSelectedAsync();
        if (job == null) return;
        job.Id = Guid.NewGuid();
        job.Name = Loc.F("editor.copyof", job.Name);
        // Stored passwords belong to the original job: they must be typed again.
        foreach (var c in job.Sources.Concat(job.Destinations).Select(e => e.Credential))
            if (c != null) c.HasPassword = false;
        OpenNew(job);
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
        if (_editor?.JobId == row.Id) CloseEditor();
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

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row) new RestoreWindow(row.Id, row.Name) { Owner = this }.Show();
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
