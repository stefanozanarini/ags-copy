using AgsCopy.Core.Models;

namespace AgsCopy.Core.Scheduling;

public static class ScheduleCalculator
{
    /// <summary>
    /// First run strictly after <paramref name="after"/> (local time), or null when the schedule
    /// never runs again.
    /// </summary>
    public static DateTime? Next(Schedule s, DateTime after)
    {
        var time = s.Start.TimeOfDay;
        switch (s.Kind)
        {
            case ScheduleKind.Manual:
                return null;

            case ScheduleKind.Once:
                return s.Start > after ? s.Start : null;

            case ScheduleKind.Daily:
            {
                var day = Max(after.Date, s.Start.Date);
                for (var i = 0; i < 3; i++)
                {
                    var c = day.AddDays(i) + time;
                    if (c > after && c >= s.Start) return c;
                }
                return null;
            }

            case ScheduleKind.Weekly:
            {
                if (s.DaysOfWeek.Count == 0) return null;
                var day = Max(after.Date, s.Start.Date);
                for (var i = 0; i < 8; i++)
                {
                    var d = day.AddDays(i);
                    var c = d + time;
                    if (s.DaysOfWeek.Contains(d.DayOfWeek) && c > after && c >= s.Start) return c;
                }
                return null;
            }

            case ScheduleKind.Monthly:
            {
                var from = Max(after.Date, s.Start.Date);
                var month = new DateTime(from.Year, from.Month, 1);
                for (var i = 0; i < 3; i++)
                {
                    var m = month.AddMonths(i);
                    var dom = Math.Clamp(s.DayOfMonth, 1, DateTime.DaysInMonth(m.Year, m.Month));
                    var c = m.AddDays(dom - 1) + time;
                    if (c > after && c >= s.Start) return c;
                }
                return null;
            }

            case ScheduleKind.Interval:
            {
                if (s.IntervalMinutes <= 0) return null;
                if (after < s.Start) return s.Start;
                var step = TimeSpan.FromMinutes(s.IntervalMinutes);
                var n = (long)((after - s.Start).Ticks / step.Ticks) + 1;
                return s.Start + TimeSpan.FromTicks(step.Ticks * n);
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Next run when the service starts. A run that fell due while the service was down is
    /// returned as-is (so it happens now) when the schedule asks to catch up, skipped otherwise.
    /// </summary>
    public static DateTime? OnStartup(Schedule s, DateTime? lastRun, DateTime referenceIfNeverRun, DateTime now)
    {
        var next = Next(s, lastRun ?? referenceIfNeverRun);
        if (next is { } n && n <= now && !s.RunMissed) next = Next(s, now);
        return next;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
