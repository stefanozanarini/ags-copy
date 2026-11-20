namespace AgsCopy.Core.Models;

public enum ScheduleKind
{
    Manual,
    Once,
    Daily,
    Weekly,
    Monthly,
    Interval,
}

/// <summary>When a job runs. All times are local to the machine running the service.</summary>
public sealed class Schedule
{
    public ScheduleKind Kind { get; set; } = ScheduleKind.Manual;

    /// <summary>
    /// First possible run. Its time of day is the run time for Daily, Weekly and Monthly;
    /// for Once it is the exact run; for Interval it is the first run.
    /// </summary>
    public DateTime Start { get; set; } = DateTime.Today.AddHours(22);

    public List<DayOfWeek> DaysOfWeek { get; set; } = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    /// <summary>1-31; months that are shorter run on their last day.</summary>
    public int DayOfMonth { get; set; } = 1;

    public int IntervalMinutes { get; set; } = 60;

    /// <summary>A run missed while the computer was off or the service stopped happens at the next start.</summary>
    public bool RunMissed { get; set; } = true;
}
