using AgsCopy.Core.Ipc;
using AgsCopy.Core.Models;

namespace AgsCopy.App;

/// <summary>One line of the job list, updated in place on every refresh.</summary>
public sealed class JobRow : ObservableObject
{
    private JobStatus _s;

    public JobRow(JobStatus s) => _s = s;

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
