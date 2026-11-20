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

    private void Next_Click(object sender, RoutedEventArgs e) => _vm.Step++;

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
