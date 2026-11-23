using System.Windows;
using System.Windows.Controls;
using AgsCopy.Core.Ipc;
using Microsoft.Win32;

namespace AgsCopy.App;

/// <summary>
/// One source or destination of a job: path, account, access check and a look at the folder. Edits a
/// copy; the job gets it only on OK.
/// </summary>
public partial class EndpointWindow : Window
{
    private const int ListingRows = 12;

    private readonly EndpointViewModel _ep;
    private readonly Guid? _jobId;
    private readonly bool _isDestination;

    /// <param name="showFolder">A source of a job with several sources: its folder in the destinations.</param>
    public EndpointWindow(EndpointViewModel ep, Guid? jobId, bool isDestination, bool isNew, bool showFolder)
    {
        InitializeComponent();
        _ep = ep;
        _jobId = jobId;
        _isDestination = isDestination;
        DataContext = ep;
        Title = Heading.Text = Loc.T(isDestination
            ? isNew ? "endpoint.destination.new" : "endpoint.destination.edit"
            : isNew ? "endpoint.source.new" : "endpoint.source.edit");
        HintText.Text = Loc.T(isDestination ? "destination.hint" : "source.hint");
        FolderPanel.Visibility = showFolder ? Visibility.Visible : Visibility.Collapsed;
        // The password box cannot be bound: show what was typed for this endpoint in this session.
        Loaded += (_, _) =>
        {
            if (Ui.FindChild<PasswordBox>(Editor) is { } box) box.Password = ep.Password;
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!PathUtil.IsAbsolute(_ep.Path))
        {
            Ui.Error(this, Loc.T(_isDestination ? "val.destination" : "val.source"));
            return;
        }
        DialogResult = true;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("endpoint.browse.title") };
        if (Directory.Exists(_ep.Path)) dialog.InitialDirectory = _ep.Path;
        if (dialog.ShowDialog(this) == true) _ep.Path = dialog.FolderName;
    }

    private void Password_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox box || box.Password == _ep.Password) return;
        _ep.Password = box.Password;
        _ep.TestResult = "";
    }

    private void ChooseKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = Loc.T("endpoint.key.title"), Filter = Loc.T("endpoint.key.filter") };
        var ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        if (Directory.Exists(ssh)) dialog.InitialDirectory = ssh;
        if (dialog.ShowDialog(this) != true) return;
        var info = new FileInfo(dialog.FileName);
        if (info.Length > 64 * 1024)
        {
            Ui.Error(this, Loc.T("endpoint.key.toobig"));
            return;
        }
        _ep.SetPrivateKey(File.ReadAllText(info.FullName), info.Name);
    }

    private void RemoveKey_Click(object sender, RoutedEventArgs e) => _ep.ClearPrivateKey();

    private void UseMapped_Click(object sender, RoutedEventArgs e)
    {
        if (_ep.MappedUnc is { } unc) _ep.Path = unc;
    }

    /// <summary>First folders and files, as the service reads them with the endpoint's account.</summary>
    private async Task ShowListingAsync()
    {
        var list = await PipeClient.CallAsync<PathListResult>(Commands.ListPath, new PathListRequest
        {
            Endpoint = _ep.ToEndpoint(),
            JobId = _jobId,
            IsDestination = _isDestination,
        }, TimeSpan.FromMinutes(1));
        if (list is not { Ok: true, Exists: true }) return;
        if (list.Total == 0)
        {
            _ep.ShowListing([new ListingItem("", Loc.T("endpoint.empty"), "")], 0);
            return;
        }
        var items = list.Entries
            .OrderBy(x => !x.IsDirectory).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(ListingRows)
            .Select(x => new ListingItem(x.IsDirectory ? "" : "", x.Name, x.IsDirectory ? "" : Fmt.Bytes(x.Size)));
        _ep.ShowListing(items, list.Total - Math.Min(list.Total, ListingRows));
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.IsEnabled = false;
        _ep.TestOk = null;
        _ep.TestResult = Loc.T("endpoint.testing");
        try
        {
            var result = await PipeClient.CallAsync<PathTestResult>(Commands.TestPath, new PathTestRequest
            {
                Endpoint = _ep.ToEndpoint(),
                JobId = _jobId,
                IsDestination = _isDestination,
            }, TimeSpan.FromMinutes(1));
            _ep.TestOk = result?.Ok;
            _ep.TestResult = result?.Message ?? "";
            // First contact with an SFTP server: record its key; runs will accept only that one.
            if (result?.HostKey != null && _ep.IsSftp) _ep.HostKey = result.HostKey;
            if (result?.Ok == true) await ShowListingAsync();
        }
        catch (Exception ex)
        {
            _ep.TestOk = false;
            _ep.TestResult = Ui.Describe(ex);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }
}
