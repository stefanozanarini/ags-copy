using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AgsCopy.Core.Ipc;
using AgsCopy.Core.Sync;
using Microsoft.Win32;

namespace AgsCopy.App;

/// <summary>A file or folder of the backup at the chosen point; folders load when opened.</summary>
public sealed class RestoreNode : ObservableObject
{
    private static readonly RestoreNode Placeholder = new(null, null, null);
    private readonly Func<RestoreNode, Task>? _load;
    private bool _expanded;
    private bool _loaded;
    private bool _checked;

    public RestoreNode(RestoreItem? item, RestoreNode? parent, Func<RestoreNode, Task>? load)
    {
        Item = item;
        Parent = parent;
        _load = load;
        _checked = parent?.IsChecked == true;
        if (item is { IsDirectory: true } && load != null) Children.Add(Placeholder);
    }

    public RestoreItem? Item { get; }
    public RestoreNode? Parent { get; }
    public ObservableCollection<RestoreNode> Children { get; } = [];

    public string Name => Item?.Name ?? "";
    public bool Selectable => Item != null;
    public string Glyph => Item == null ? "" : Item.IsDirectory ? "" : "";
    public Brush GlyphBrush => Item?.IsDirectory == true ? Brushes.Goldenrod : Brushes.SlateGray;

    public string Detail => Item == null || Item.IsDirectory ? ""
        : Fmt.Bytes(Item.Size) + " · " + Fmt.DateTime(Item.ModifiedUtc.ToLocalTime())
          + (Item.Version != null ? " · " + Loc.T("restore.fromversion") : "");

    /// <summary>A ticked folder restores everything in it; its children follow the tick.</summary>
    public bool IsChecked
    {
        get => _checked;
        set
        {
            if (!Set(ref _checked, value)) return;
            foreach (var c in Children.Where(c => c.Item != null)) c.IsChecked = value;
        }
    }

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (!Set(ref _expanded, value) || !value || _loaded || _load == null) return;
            _loaded = true;
            _ = _load(this);
        }
    }
}

public sealed record PointOption(string? Stamp, string Text);

public partial class RestoreWindow : Window
{
    private readonly Guid _jobId;
    private readonly string _jobName;
    private readonly ObservableCollection<RestoreNode> _roots = [];
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private Guid? _operation;
    private string? _target;

    public RestoreWindow(Guid jobId, string jobName)
    {
        InitializeComponent();
        _jobId = jobId;
        _jobName = jobName;
        Title = Heading.Text = Loc.F("restore.title", jobName);
        Tree.ItemsSource = _roots;
        _poll.Tick += async (_, _) => await PollAsync();
        Loaded += async (_, _) => await LoadPointsAsync();
        Closing += (_, e) =>
        {
            if (_operation != null && !Ui.Confirm(this, Loc.T("restore.closing"))) e.Cancel = true;
        };
        Closed += (_, _) => _poll.Stop();
    }

    private string? Stamp => (PointBox.SelectedItem as PointOption)?.Stamp;

    private async Task LoadPointsAsync()
    {
        try
        {
            var points = await PipeClient.CallAsync<List<RestorePoint>>(Commands.RestorePoints, new IdRequest { Id = _jobId }, TimeSpan.FromMinutes(2)) ?? [];
            PointBox.ItemsSource = points.Select(p => new PointOption(p.Stamp, p.Stamp == null
                ? Loc.T("restore.point.current")
                : Loc.F("restore.point.before", Fmt.DateTime(p.Local ?? DateTime.MinValue)))).ToList();
            PointBox.SelectedIndex = 0;
        }
        catch (Exception e)
        {
            Loading.Visibility = Visibility.Collapsed;
            Ui.Error(this, e);
        }
    }

    private async void Point_Changed(object sender, SelectionChangedEventArgs e)
    {
        var name = string.Concat(_jobName.Split(Path.GetInvalidFileNameChars()));
        var when = Stamp ?? DateTime.Now.ToString("yyyy-MM-dd_HHmm");
        TargetBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Loc.T("restore.folder"), $"{name} {when}");
        _roots.Clear();
        Loading.Visibility = Visibility.Visible;
        try
        {
            foreach (var n in await ListAsync(null, "")) _roots.Add(n);
        }
        catch (Exception ex)
        {
            Ui.Error(this, ex);
        }
        finally
        {
            Loading.Visibility = Visibility.Collapsed;
        }
    }

    private async Task<List<RestoreNode>> ListAsync(RestoreNode? parent, string relative)
    {
        var items = await PipeClient.CallAsync<List<RestoreItem>>(Commands.RestoreList,
            new RestoreListRequest { JobId = _jobId, Stamp = Stamp, Relative = relative }, TimeSpan.FromMinutes(2)) ?? [];
        return items.Select(i => new RestoreNode(i, parent, LoadChildrenAsync)).ToList();
    }

    private async Task LoadChildrenAsync(RestoreNode node)
    {
        try
        {
            var children = await ListAsync(node, node.Item!.Relative);
            node.Children.Clear();
            foreach (var c in children) node.Children.Add(c);
        }
        catch (Exception e)
        {
            Ui.Error(this, e);
        }
    }

    /// <summary>Ticked items; a ticked folder stands for all it holds.</summary>
    private static IEnumerable<string> Selected(IEnumerable<RestoreNode> nodes)
    {
        foreach (var n in nodes.Where(n => n.Item != null))
        {
            if (n.IsChecked) yield return n.Item!.Relative;
            else foreach (var s in Selected(n.Children)) yield return s;
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("restore.target") };
        if (dialog.ShowDialog(this) == true) TargetBox.Text = dialog.FolderName;
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var items = Selected(_roots).ToList();
        if (items.Count == 0)
        {
            Ui.Error(this, Loc.T("restore.none"));
            return;
        }
        try
        {
            var started = await PipeClient.CallAsync<IdRequest>(Commands.RestoreStart, new RestoreStartRequest
            {
                JobId = _jobId,
                Stamp = Stamp,
                Items = items,
                Target = TargetBox.Text.Trim(),
                Overwrite = OverwriteBox.IsChecked == true,
            });
            _operation = started?.Id;
            _target = TargetBox.Text.Trim();
            RestoreButton.IsEnabled = PointBox.IsEnabled = TargetBox.IsEnabled = false;
            OpenButton.Visibility = Visibility.Collapsed;
            CloseButton.Content = Loc.T("restore.stop");
            Bar.Visibility = Visibility.Visible;
            Bar.IsIndeterminate = true;
            StatusText.Text = Loc.T("restore.scanning");
            _poll.Start();
        }
        catch (Exception ex)
        {
            Ui.Error(this, ex);
        }
    }

    private async Task PollAsync()
    {
        if (_operation is not { } id) return;
        RestoreStatusResponse? s;
        try
        {
            s = await PipeClient.CallAsync<RestoreStatusResponse>(Commands.RestoreStatus, new IdRequest { Id = id });
        }
        catch (Exception)
        {
            return; // try again at the next tick
        }
        if (s == null) return;

        Bar.IsIndeterminate = s.Scanning;
        if (!s.Scanning)
        {
            Bar.Value = s.TotalBytes > 0 ? 100.0 * s.Bytes / s.TotalBytes : s.TotalFiles > 0 ? 100.0 * s.Files / s.TotalFiles : 100;
            StatusText.Text = Loc.F("restore.progress", Fmt.Count(s.Files), Fmt.Count(s.TotalFiles), Fmt.Bytes(s.Bytes), Fmt.Bytes(s.TotalBytes));
        }
        if (s.Running) return;

        _poll.Stop();
        _operation = null;
        RestoreButton.IsEnabled = PointBox.IsEnabled = TargetBox.IsEnabled = true;
        CloseButton.Content = Loc.T("restore.close");
        OpenButton.Visibility = Directory.Exists(_target) ? Visibility.Visible : Visibility.Collapsed;
        if (s.Error != null)
        {
            StatusText.Text = s.Error;
            StatusText.Foreground = (Brush)FindResource("Danger");
            return;
        }
        StatusText.Foreground = s.ErrorCount > 0 ? (Brush)FindResource("Warning") : (Brush)FindResource("Ok");
        StatusText.Text = s.Cancelled ? Loc.T("restore.cancelled")
            : Loc.F("restore.done", Fmt.Count(s.Files), Fmt.Bytes(s.Bytes), Fmt.Count(s.Skipped), Fmt.Count(s.ErrorCount));
        if (s.ErrorCount > 0)
            new TextViewerWindow(Loc.T("restore.errors"), string.Join(Environment.NewLine, s.Errors.Select(x => $"{x.Path}: {x.Message}"))) { Owner = this }.Show();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_target != null && Directory.Exists(_target)) Process.Start(new ProcessStartInfo(_target) { UseShellExecute = true });
    }

    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is { } id)
        {
            await PipeClient.CallAsync(Commands.RestoreCancel, new IdRequest { Id = id });
            return;
        }
        Close();
    }
}
