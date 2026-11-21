using System.Windows;
using System.Windows.Controls;
using AgsCopy.Core.Ipc;
using AgsCopy.Core.Models;
using AgsCopy.Core.Robocopy;
using Microsoft.Win32;

namespace AgsCopy.App;

/// <summary>
/// Creates or edits a job in steps. Saving always goes through the summary step, which
/// describes what will be copied, where, and what will be deleted.
/// </summary>
public partial class JobEditorWindow : Window
{
    private static readonly TimeSpan PreviewTimeout = TimeSpan.FromMinutes(30);

    private readonly JobEditorViewModel _vm;
    private CancellationTokenSource? _previewCts;

    public JobEditorWindow(CopyJob job, bool isNew)
    {
        InitializeComponent();
        _vm = new JobEditorViewModel(job, isNew);
        DataContext = _vm;
        Title = isNew ? Loc.T("editor.title.new") : Loc.F("editor.title.edit", job.Name);
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
        Closed += (_, _) => _previewCts?.Cancel();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => _vm.Step--;

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        // Leaving the destination step: show where the files will land, once per pair of paths.
        if (_vm.Step == DestinationStep && PathUtil.IsAbsolute(_vm.Source.Path) && PathUtil.IsAbsolute(_vm.Destination.Path))
        {
            var pair = _vm.Source.Path + "|" + _vm.Destination.Path;
            if (!pair.Equals(_layoutShownFor, StringComparison.OrdinalIgnoreCase))
            {
                _layoutShownFor = pair;
                if (!ShowLayout()) return;
            }
        }
        _vm.Step++;
    }

    private const int DestinationStep = 2;
    private string? _layoutShownFor;

    private void Layout_Click(object sender, RoutedEventArgs e)
    {
        if (!PathUtil.IsAbsolute(_vm.Source.Path) || !PathUtil.IsAbsolute(_vm.Destination.Path))
        {
            Ui.Error(this, Loc.T("layout.needpaths"));
            return;
        }
        _layoutShownFor = _vm.Source.Path + "|" + _vm.Destination.Path;
        ShowLayout();
    }

    /// <summary>Opens the preview; false when the user changed the destination from it (stay on the step).</summary>
    private bool ShowLayout()
    {
        var changed = false;
        while (true)
        {
            var w = new LayoutPreviewWindow(_vm.ToJob(), _vm.IsNew ? null : _vm.JobId) { Owner = this };
            if (w.ShowDialog() != true || w.ChosenDestination is not { } chosen) return !changed;
            // Show the new layout too: it is the one that will be saved.
            changed = true;
            _vm.Destination.Path = chosen;
            _vm.Step = DestinationStep;
            _layoutShownFor = _vm.Source.Path + "|" + _vm.Destination.Path;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        // Nothing is saved before the summary has been shown.
        if (!_vm.IsSummaryStep)
        {
            _vm.Step = JobEditorViewModel.SummaryStep;
            return;
        }

        var job = _vm.ToJob();
        var errors = JobValidator.Validate(job);
        if (errors.Count > 0)
        {
            Ui.Error(this, string.Join(Environment.NewLine, errors));
            return;
        }

        SaveButton.IsEnabled = false;
        try
        {
            await PipeClient.CallAsync<CopyJob>(Commands.SaveJob, job);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Ui.Error(this, ex);
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
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
            Ui.Error(this, string.Join(Environment.NewLine, errors));
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
            Ui.Error(this, ex);
        }
        finally
        {
            _vm.PreviewRunning = false;
            PreviewButton.Content = Loc.T("summary.preview");
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not EndpointViewModel ep) return;
        var dialog = new OpenFolderDialog { Title = Loc.T("endpoint.browse.title") };
        if (Directory.Exists(ep.Path)) dialog.InitialDirectory = ep.Path;
        if (dialog.ShowDialog(this) == true) ep.Path = dialog.FolderName;
    }

    private void Password_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: EndpointViewModel ep } box)
        {
            ep.Password = box.Password;
            ep.TestResult = "";
        }
    }

    private const int OptionsStep = 4;

    private void GoToOptions_Click(object sender, RoutedEventArgs e) => _vm.Step = OptionsStep;

    private void UseMapped_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EndpointViewModel { MappedUnc: { } unc } ep) ep.Path = unc;
    }

    private const int ListingRows = 12;

    /// <summary>First folders and files, as the service reads them with the endpoint's account.</summary>
    private async Task ShowListingAsync(EndpointViewModel ep)
    {
        var list = await PipeClient.CallAsync<PathListResult>(Commands.ListPath, new PathListRequest
        {
            Endpoint = ep.ToEndpoint(),
            JobId = _vm.IsNew ? null : _vm.JobId,
            IsDestination = ReferenceEquals(ep, _vm.Destination),
        }, TimeSpan.FromMinutes(1));
        if (list is not { Ok: true, Exists: true }) return;
        if (list.Total == 0)
        {
            ep.ShowListing([new ListingItem("", Loc.T("endpoint.empty"), "")], 0);
            return;
        }
        var items = list.Entries
            .OrderBy(x => !x.IsDirectory).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(ListingRows)
            .Select(x => new ListingItem(x.IsDirectory ? "\uE8B7" : "\uE8A5", x.Name, x.IsDirectory ? "" : Fmt.Bytes(x.Size)));
        ep.ShowListing(items, list.Total - Math.Min(list.Total, ListingRows));
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not EndpointViewModel ep) return;
        var button = (Button)sender;
        button.IsEnabled = false;
        ep.TestOk = null;
        ep.TestResult = Loc.T("endpoint.testing");
        try
        {
            var result = await PipeClient.CallAsync<PathTestResult>(Commands.TestPath, new PathTestRequest
            {
                Endpoint = ep.ToEndpoint(),
                JobId = _vm.IsNew ? null : _vm.JobId,
                IsDestination = ReferenceEquals(ep, _vm.Destination),
            }, TimeSpan.FromMinutes(1));
            ep.TestOk = result?.Ok;
            ep.TestResult = result?.Message ?? "";
            if (result?.Ok == true) await ShowListingAsync(ep);
        }
        catch (Exception ex)
        {
            ep.TestOk = false;
            ep.TestResult = Ui.Describe(ex);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }
}
