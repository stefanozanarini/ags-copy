using System.Collections.Concurrent;
using AgsCopy.Core.Models;
using AgsCopy.Core.Scheduling;

namespace AgsCopy.Service;

/// <summary>Starts scheduled jobs when they fall due. Checks every 10 seconds.</summary>
public sealed class Scheduler : BackgroundService
{
    private readonly JobStore _store;
    private readonly HistoryStore _history;
    private readonly JobExecutor _executor;
    private readonly ServiceSettings _settings;
    private readonly ILogger<Scheduler> _log;
    private readonly ConcurrentDictionary<Guid, DateTime?> _next = new();

    public Scheduler(JobStore store, HistoryStore history, JobExecutor executor, ServiceSettings settings, ILogger<Scheduler> log)
    {
        _store = store;
        _history = history;
        _executor = executor;
        _settings = settings;
        _log = log;
        // An edited job gets its next run computed again.
        _store.Changed += id => _next.TryRemove(id, out _);
    }

    public bool Paused
    {
        get => _settings.SchedulerPaused;
        set
        {
            _settings.SchedulerPaused = value;
            _settings.Save();
            _log.LogInformation("Scheduler {State}", value ? "paused" : "resumed");
        }
    }

    public DateTime? NextRun(CopyJob job) =>
        job.Enabled && job.Schedule.Kind != ScheduleKind.Manual ? NextFor(job, DateTime.Now) : null;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            try
            {
                Tick();
            }
            catch (Exception e)
            {
                _log.LogError(e, "Scheduler error");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    private void Tick()
    {
        var now = DateTime.Now;
        foreach (var job in _store.All())
        {
            if (!job.Enabled || job.Schedule.Kind == ScheduleKind.Manual)
            {
                _next.TryRemove(job.Id, out _);
                continue;
            }

            var next = NextFor(job, now);
            if (next is null || next > now) continue;

            if (Paused)
                _log.LogInformation("Job {Name} skipped: scheduler paused", job.Name);
            else if (!_executor.TryStart(job, RunTrigger.Scheduled))
                _log.LogWarning("Job {Name} skipped: still running from the previous time", job.Name);

            _next[job.Id] = ScheduleCalculator.Next(job.Schedule, now);
        }
    }

    private DateTime? NextFor(CopyJob job, DateTime now) =>
        _next.GetOrAdd(job.Id, _ => ScheduleCalculator.OnStartup(
            job.Schedule,
            _history.Last(job.Id)?.StartUtc.ToLocalTime(),
            job.ModifiedUtc.ToLocalTime(),
            now));
}
