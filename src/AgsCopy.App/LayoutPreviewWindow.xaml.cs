using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using AgsCopy.Core.Ipc;
using AgsCopy.Core.Models;

namespace AgsCopy.App;

/// <summary>A row of the source or destination tree.</summary>
public sealed class LayoutNode : ObservableObject
{
    private static readonly LayoutNode Placeholder = new(null, "", "", false, null, 0);
    private readonly Func<LayoutNode, Task>? _load;
    private bool _expanded;
    private bool _loaded;

    public LayoutNode(Func<LayoutNode, Task>? load, string relative, string name, bool isDirectory, LayoutFate? fate, long size)
    {
        _load = load;
        Relative = relative;
        Name = name;
        IsDirectory = isDirectory;
        Fate = fate;
        Size = size;
        if (isDirectory && load != null) Children.Add(Placeholder);
    }

    public string Relative { get; }
    public string Name { get; }
    public bool IsDirectory { get; }
    public LayoutFate? Fate { get; }
    public long Size { get; }
    public ObservableCollection<LayoutNode> Children { get; } = [];

    public string Glyph => _load == null && Name.Length > 0 && !IsDirectory && Fate == null && Size < 0 ? "" : IsDirectory ? "" : "";
    public Brush GlyphBrush => IsDirectory ? Brushes.Goldenrod : Brushes.SlateGray;
    public string Detail { get; init; } = "";
    public bool Struck => Fate == LayoutFate.Deleted;

    public bool HasBadge => Fate is not null and not LayoutFate.Kept;
    public string Badge => Fate == null ? "" : Loc.T("layout.fate." + Fate);
    public Brush BadgeBackground => Fate switch
    {
        LayoutFate.New => Res("AccentLight"),
        LayoutFate.Deleted => Res("DangerLight"),
        LayoutFate.Archived => Res("WarningLight"),
        _ => Res("Surface"),
    };
    public Brush BadgeForeground => Fate switch
    {
        LayoutFate.New => Res("Accent"),
        LayoutFate.Deleted => Res("Danger"),
        LayoutFate.Archived => Res("Warning"),
        _ => Res("Muted"),
    };

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

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
}

/// <summary>
/// Opens on the source folder and shows, side by side, what the destination will contain after the
/// copy. Catches the classic mistake of a destination one level too deep or too shallow.
/// </summary>
public partial class LayoutPreviewWindow : Window
{
    private readonly CopyJob _job;
    private readonly Guid? _jobId;
    private readonly bool _canChangeDestination;

    /// <summary>Destination path the user chose from the suggestion, or null.</summary>
    public string? ChosenDestination { get; private set; }

    /// <param name="canChangeDestination">False for one source of several: its folder is set in the source step.</param>
    public LayoutPreviewWindow(CopyJob job, Guid? jobId, bool canChangeDestination = true)
    {
        InitializeComponent();
        _job = job;
        _jobId = jobId;
        _canChangeDestination = canChangeDestination;
        SourcePath.Text = job.Source.Path;
        DestinationPath.Text = job.Destination.Path;
        Loaded += async (_, _) => await LoadTopAsync();
    }

    private async Task LoadTopAsync()
    {
        try
        {
            var (src, dst) = await ListBothAsync("");
            if (src == null) return;
            var layout = DestinationLayout.Build(_job, "", src.Entries, dst is { Exists: true } ? dst.Entries : null);
            Notes.ItemsSource = layout.Notes;
            SourceTree.ItemsSource = SourceNodes("", src);
            DestinationTree.ItemsSource = DestinationNodes("", layout, src, dst);

            if (layout.Suggestion != null && _canChangeDestination)
            {
                SuggestButton.Content = Loc.F("layout.suggest", layout.Suggestion);
                SuggestButton.Tag = layout.Suggestion;
                SuggestButton.Visibility = Visibility.Visible;
                if (layout.SuggestionRecommended) SuggestButton.Style = (Style)FindResource("PrimaryButton");
            }
        }
        catch (Exception e)
        {
            Ui.Error(this, e);
        }
        finally
        {
            Loading.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Lists the same relative folder on both sides; shows an error and returns null for an unreadable source.</summary>
    private async Task<(PathListResult? Source, PathListResult? Destination)> ListBothAsync(string relative)
    {
        var src = ListAsync(_job.Source, false, relative);
        var dst = ListAsync(_job.Destination, true, relative);
        await Task.WhenAll(src, dst);
        if (src.Result is not { Ok: true, Exists: true })
        {
            Ui.Error(this, Loc.F("layout.unreadable", PathFor(_job.Source.Path, relative), src.Result?.Message ?? ""));
            return (null, null);
        }
        return (src.Result, dst.Result);
    }

    private Task<PathListResult?> ListAsync(JobEndpoint endpoint, bool isDestination, string relative) =>
        PipeClient.CallAsync<PathListResult>(Commands.ListPath, new PathListRequest
        {
            Endpoint = endpoint,
            JobId = _jobId,
            IsDestination = isDestination,
            Relative = relative,
        }, TimeSpan.FromMinutes(1));

    private List<LayoutNode> SourceNodes(string relative, PathListResult list)
    {
        var nodes = list.Entries
            .OrderBy(e => !e.IsDirectory).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(e => new LayoutNode(e.IsDirectory ? LoadSourceAsync : null, Join(relative, e.Name), e.Name, e.IsDirectory, null, e.Size)
            {
                Detail = e.IsDirectory ? "" : Fmt.Bytes(e.Size),
            })
            .ToList();
        AddMore(nodes, list);
        return nodes;
    }

    private List<LayoutNode> DestinationNodes(string relative, LayoutResult layout, PathListResult src, PathListResult? dst)
    {
        var nodes = layout.Items
            .Select(i => new LayoutNode(i.IsDirectory && i.Fate is LayoutFate.New or LayoutFate.Update ? LoadDestinationAsync : null,
                Join(relative, i.Name), i.Name, i.IsDirectory, i.Fate, i.Size)
            {
                Detail = i.IsDirectory ? "" : Fmt.Bytes(i.Size),
            })
            .ToList();
        AddMore(nodes, src);
        if (dst != null) AddMore(nodes, dst);
        return nodes;
    }

    private static void AddMore(List<LayoutNode> nodes, PathListResult list)
    {
        if (list.Total > list.Entries.Count)
            nodes.Add(new LayoutNode(null, "", Loc.F("layout.more", Fmt.Count(list.Total - list.Entries.Count)), false, null, -1));
    }

    private async Task LoadSourceAsync(LayoutNode node)
    {
        try
        {
            var list = await ListAsync(_job.Source, false, node.Relative);
            node.Children.Clear();
            if (list is { Ok: true, Exists: true })
                foreach (var n in SourceNodes(node.Relative, list)) node.Children.Add(n);
        }
        catch (Exception e)
        {
            Ui.Error(this, e);
        }
    }

    private async Task LoadDestinationAsync(LayoutNode node)
    {
        try
        {
            var (src, dst) = await ListBothAsync(node.Relative);
            node.Children.Clear();
            if (src == null) return;
            var layout = DestinationLayout.Build(_job, node.Relative, src.Entries, dst is { Exists: true } ? dst.Entries : null);
            foreach (var n in DestinationNodes(node.Relative, layout, src, dst)) node.Children.Add(n);
        }
        catch (Exception e)
        {
            Ui.Error(this, e);
        }
    }

    private static string Join(string relative, string name) => relative.Length == 0 ? name : relative + "\\" + name;

    private static string PathFor(string root, string relative) => relative.Length == 0 ? root : PathUtil.Combine(root, relative);

    private void Suggest_Click(object sender, RoutedEventArgs e)
    {
        ChosenDestination = SuggestButton.Tag as string;
        DialogResult = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
