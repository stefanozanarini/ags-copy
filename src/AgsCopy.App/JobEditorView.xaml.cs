using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AgsCopy.Core.Ipc;
using AgsCopy.Core.Models;
using AgsCopy.Core.Robocopy;

namespace AgsCopy.App;

/// <summary>
/// A job in the right pane of the main window: sources and destinations, mode and options, schedule,
/// summary. Saving always goes through the summary page, which describes what will be copied, where,
/// and what will be deleted.
/// </summary>
public partial class JobEditorView : UserControl
{
    private static readonly TimeSpan PreviewTimeout = TimeSpan.FromMinutes(30);

    private readonly JobEditorViewModel _vm;
    private readonly DispatcherTimer _dirtyCheck = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private CancellationTokenSource? _previewCts;
    private string? _layoutShownFor;

    /// <summary>The job was saved; it carries the saved version.</summary>
    public event Action<CopyJob>? Saved;

    /// <summary>The changes were discarded (a new job: the editor closes).</summary>
    public event Action? Discarded;

    public JobEditorView(CopyJob job, bool isNew)
    {
        InitializeComponent();
        _vm = new JobEditorViewModel(job, isNew);
        DataContext = _vm;
        JobTitle.Text = isNew ? Loc.T("editor.title.new") : job.Name;
        ShowPage();
        UpdateFolderColumn();
        _vm.Sources.CollectionChanged += (_, _) => UpdateFolderColumn();
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(JobEditorViewModel.Step)) ShowPage();
        };
        _dirtyCheck.Tick += (_, _) => ShowDirty();
        Loaded += (_, _) =>
        {
            _dirtyCheck.Start();
            if (isNew)
            {
                NameBox.Focus();
                NameBox.SelectAll();
            }
        };
        Unloaded += (_, _) =>
        {
            _dirtyCheck.Stop();
            _previewCts?.Cancel();
        };
    }

    public Guid JobId => _vm.JobId;
    public bool IsNew => _vm.IsNew;
    public bool IsDirty => _vm.IsDirty;

    public int Page
    {
        get => _vm.Step;
        set => _vm.Step = value;
    }

    private Window Owner => Window.GetWindow(this)!;

    private void ShowPage() => PageTitle.Text = "· " + Loc.T(_vm.Steps[_vm.Step]);

    private void ShowDirty()
    {
        var dirty = _vm.IsDirty;
        DirtyText.Visibility = dirty ? Visibility.Visible : Visibility.Hidden;
        DiscardButton.IsEnabled = dirty;
    }

    /// <summary>The folder of each source in the destinations matters only with several sources.</summary>
    private void UpdateFolderColumn() => FolderColumn.Width = _vm.IsMultiSource ? 180 : 0;

    // Sources and destinations

    private void AddSource_Click(object sender, RoutedEventArgs e) => EditEndpoint(_vm.Sources, null);
    private void AddDestination_Click(object sender, RoutedEventArgs e) => EditEndpoint(_vm.Destinations, null);

    private void EditSource_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedSource is { } s) EditEndpoint(_vm.Sources, s);
    }

    private void EditDestination_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedDestination is { } d) EditEndpoint(_vm.Destinations, d);
    }

    private void RemoveSource_Click(object sender, RoutedEventArgs e) => RemoveEndpoint(_vm.Sources, _vm.SelectedSource, "sources.remove.confirm");
    private void RemoveDestination_Click(object sender, RoutedEventArgs e) => RemoveEndpoint(_vm.Destinations, _vm.SelectedDestination, "destinations.remove.confirm");

    private void RemoveEndpoint(ObservableCollection<EndpointViewModel> list, EndpointViewModel? e, string confirm)
    {
        if (e == null || !Ui.Confirm(Owner, Loc.F(confirm, e.Title))) return;
        _vm.Remove(list, e);
    }

    /// <summary>Edits a copy in its own window; the job gets it on OK.</summary>
    private void EditEndpoint(ObservableCollection<EndpointViewModel> list, EndpointViewModel? original)
    {
        var isDestination = ReferenceEquals(list, _vm.Destinations);
        var edited = original?.Copy() ?? new EndpointViewModel(new JobEndpoint());
        // A second source makes folders matter: show the field when adding it too.
        var showFolder = !isDestination && (_vm.Sources.Count > 1 || (original == null && _vm.Sources.Count > 0));
        var w = new EndpointWindow(edited, _vm.IsNew ? null : _vm.JobId, isDestination, original == null, showFolder) { Owner = Owner };
        if (w.ShowDialog() != true) return;
        _vm.Put(list, original, edited);

        // One source into one destination: show where the files will land, once per pair of paths.
        if (_vm.Sources.Count == 1 && _vm.Destinations.Count == 1)
        {
            _vm.SelectedSource ??= _vm.Sources[0];
            _vm.SelectedDestination ??= _vm.Destinations[0];
            var pair = _vm.Sources[0].Path + "|" + _vm.Destinations[0].Path;
            if (!pair.Equals(_layoutShownFor, StringComparison.OrdinalIgnoreCase))
            {
                _layoutShownFor = pair;
                ShowLayout();
            }
        }
    }

    private void Layout_Click(object sender, RoutedEventArgs e)
    {
        var source = _vm.SelectedSource ?? _vm.Sources.FirstOrDefault();
        var destination = _vm.SelectedDestination ?? _vm.Destinations.FirstOrDefault();
        if (source == null || destination == null || !PathUtil.IsAbsolute(source.Path) || !PathUtil.IsAbsolute(destination.Path))
        {
            Ui.Error(Owner, Loc.T("layout.needpaths"));
            return;
        }
        _vm.SelectedSource = source;
        _vm.SelectedDestination = destination;
        ShowLayout();
    }

    /// <summary>The selected source in the selected destination, side by side with what will be there.</summary>
    private void ShowLayout()
    {
        while (true)
        {
            var job = _vm.ToJob();
            var s = _vm.SelectedSource == null ? 0 : Math.Max(0, _vm.Sources.IndexOf(_vm.SelectedSource));
            var d = _vm.SelectedDestination == null ? 0 : Math.Max(0, _vm.Destinations.IndexOf(_vm.SelectedDestination));
            // Several sources: each has its own folder, set with the source; the destination itself is not offered for change.
            var w = new LayoutPreviewWindow(job.ForSource(s, d), _vm.IsNew ? null : _vm.JobId, !job.MultiSource) { Owner = Owner };
            if (w.ShowDialog() != true || w.ChosenDestination is not { } chosen || _vm.SelectedDestination is not { } dest) return;
            // Show the new layout too: it is the one that will be saved.
            dest.Path = chosen;
            _layoutShownFor = (_vm.SelectedSource?.Path ?? "") + "|" + chosen;
        }
    }

    // Mode and options

    private void GoToOptions_Click(object sender, RoutedEventArgs e) => VersionsCard.BringIntoView();

    // Save

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        // Nothing is saved before the summary has been shown.
        if (!_vm.IsSummaryStep)
        {
            _vm.Step = JobEditorViewModel.SummaryStep;
            return;
        }

        await SaveAsync();
    }

    /// <summary>Validates and saves the job; false when it was not saved (the user has been told why).</summary>
    public async Task<bool> SaveAsync()
    {
        var job = _vm.ToJob();
        var errors = JobValidator.Validate(job);
        if (errors.Count > 0)
        {
            Ui.Error(Owner, string.Join(Environment.NewLine, errors));
            return false;
        }

        SaveButton.IsEnabled = false;
        try
        {
            var saved = await PipeClient.CallAsync<CopyJob>(Commands.SaveJob, job);
            _vm.MarkSaved();
            ShowDirty();
            Saved?.Invoke(saved ?? job);
            return true;
        }
        catch (Exception ex)
        {
            Ui.Error(Owner, ex);
            return false;
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    /// <summary>Before a run of a job with unsaved changes: the summary is shown, then the changes saved if the user says so.</summary>
    public async Task<bool> SaveBeforeRunAsync()
    {
        _vm.Step = JobEditorViewModel.SummaryStep;
        return Ui.Confirm(Owner, Loc.T("editor.unsaved.run")) && await SaveAsync();
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (Ui.Confirm(Owner, Loc.T("editor.discard.confirm"))) Discarded?.Invoke();
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.PreviewRunning)
        {
            _previewCts?.Cancel();
            return;
        }

        var job = _vm.ToJob();
        var errors = JobValidator.Validate(job).Where(x => x != Loc.T("val.confirm")).ToList();
        if (errors.Count > 0)
        {
            Ui.Error(Owner, string.Join(Environment.NewLine, errors));
            return;
        }

        _previewCts = new CancellationTokenSource();
        _vm.Preview = null;
        _vm.PreviewRunning = true;
        PreviewButton.Content = Loc.T("summary.preview.stop");
        try
        {
            _vm.Preview = await PipeClient.CallAsync<PreviewResult>(Commands.Preview, job, PreviewTimeout, _previewCts.Token);
        }
        catch (OperationCanceledException) when (_previewCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Ui.Error(Owner, ex);
        }
        finally
        {
            _vm.PreviewRunning = false;
            PreviewButton.Content = Loc.T("summary.preview");
        }
    }
}
