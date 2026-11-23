using AgsCopy.Core.Ipc;
using AgsCopy.Core.Models;

namespace AgsCopy.App;

/// <summary>A page of a job in the tree: sources and destinations, options, schedule, summary.</summary>
public sealed class PageNode(JobRow row, int index, string titleKey) : ObservableObject
{
    private bool _selected;

    public JobRow Row { get; } = row;
    public int Index { get; } = index;
    public string Title => Loc.T(titleKey);

    public bool IsSelected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    /// <summary>Pages have nothing to expand; the tree binds it on every node.</summary>
    public bool IsExpanded { get => false; set { } }
}

/// <summary>A job in the tree, updated in place on every refresh.</summary>
public sealed class JobRow : ObservableObject
{
    private JobStatus _s;
    private bool _selected;
    private bool _expanded;

    public JobRow(JobStatus s)
    {
        _s = s;
        Pages = new[] { "page.endpoints", "page.options", "page.schedule", "page.summary" }.Select((k, i) => new PageNode(this, i, k)).ToList();
    }

    public IReadOnlyList<PageNode> Pages { get; }

    public bool IsSelected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public bool IsExpanded
    {
        get => _expanded;
        set => Set(ref _expanded, value);
    }

    public Guid Id => _s.Id;
    public JobStatus Status => _s;

    public void Update(JobStatus s)
    {
        _s = s;
        OnPropertyChanged(string.Empty); // every property
    }

    public string Name => _s.Name;
    public string Source => _s.Source;
    public string Destination => _s.Destination;
    public bool Running => _s.Running;
    public bool Enabled => _s.Enabled;
    public bool DeletesFiles => _s.Mode != CopyMode.Copy;

    public string ModeText => Loc.T("mode." + _s.Mode);

    public string ScheduleText => _s.Enabled ? JobDescriber.DescribeSchedule(_s.Schedule) : Loc.T("main.disabled");

    public string NextRunText => _s.NextRunLocal is { } n ? Fmt.DateTime(n) : "—";

    /// <summary>Under the job name in the tree: the schedule, or when it runs next.</summary>
    public string NextRunLine => _s is { Running: true, Current: { } c } ? Tail(c)
        : _s.NextRunLocal is { } n ? Loc.F("main.nextrun", Fmt.DateTime(n)) : ScheduleText;

    /// <summary>The whole line under the job name, for its tooltip: a path is shortened there.</summary>
    public string NextRunTip => _s is { Running: true, Current: { } c } ? c : NextRunLine;

    /// <summary>"…olderile": the end of a path is what tells where the copy is.</summary>
    private static string Tail(string path)
    {
        var parts = path.TrimEnd('\\').Split('\\');
        return parts.Length <= 2 ? path : "…\\" + string.Join('\\', parts[^2..]);
    }

    public string StateText
    {
        get
        {
            if (_s.Running) return Progress.Describe(_s);
            return _s.LastRun is { } r
                ? Loc.T("outcome." + r.Outcome) + " · " + Fmt.DateTime(r.StartUtc.ToLocalTime())
                : Loc.T("main.never");
        }
    }

    /// <summary>0-100 while running with known totals.</summary>
    public double Percent => Progress.Percent(_s) ?? 0;

    /// <summary>Running but the totals are not known yet: the bar just moves.</summary>
    public bool Indeterminate => _s.Running && Progress.Percent(_s) == null;

    /// <summary>Running, Success, Warning, Failed, Cancelled or Never: drives the status dot colour.</summary>
    public string StateKey => _s.Running ? "Running" : _s.LastRun?.Outcome.ToString() ?? "Never";
}
