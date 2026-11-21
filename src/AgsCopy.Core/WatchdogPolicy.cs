namespace AgsCopy.Core;

/// <summary>What the watchdog sees of the main service at one check.</summary>
public enum ServiceHealth
{
    /// <summary>Not installed, disabled, or a setup is running: none of the watchdog's business.</summary>
    Ignored,
    Stopped,
    /// <summary>Start, stop, pause or continue pending.</summary>
    Changing,
    /// <summary>Running and answering on its pipe.</summary>
    Healthy,
    /// <summary>Running but its pipe does not answer.</summary>
    Unresponsive,
}

public enum WatchdogAction { None, Start, Restart }

/// <summary>
/// Decides when the watchdog steps in. A single bad check never triggers anything: the service may
/// be in the middle of starting, of an update or of an administrator's restart.
/// </summary>
public sealed class WatchdogPolicy
{
    /// <summary>Consecutive checks the service must be stopped before it is started again.</summary>
    public int StoppedChecks { get; init; } = 2;

    /// <summary>Consecutive checks it must be running without answering before it is killed and restarted.</summary>
    public int UnresponsiveChecks { get; init; } = 3;

    /// <summary>Consecutive checks a pending state may last before it counts as hung.</summary>
    public int ChangingChecks { get; init; } = 5;

    private ServiceHealth _last = ServiceHealth.Healthy;
    private int _count;

    public WatchdogAction Next(ServiceHealth health)
    {
        _count = health == _last ? _count + 1 : 1;
        _last = health;

        var limit = health switch
        {
            ServiceHealth.Stopped => StoppedChecks,
            ServiceHealth.Unresponsive => UnresponsiveChecks,
            ServiceHealth.Changing => ChangingChecks,
            _ => 0,
        };
        if (limit == 0 || _count < limit) return WatchdogAction.None;

        _count = 0;
        return health == ServiceHealth.Stopped ? WatchdogAction.Start : WatchdogAction.Restart;
    }
}
