namespace AgsCopy.Core;

/// <summary>
/// Runs a crash of the service cut short (a run still marked as running when the service starts).
/// They run again once the service is back: the watchdog asks for it at every check. A job that keeps
/// bringing the service down is not run again forever: after <see cref="MaxAttempts"/> interruptions in
/// a row it waits for its schedule or a manual run. A run that ends by itself starts the count again.
/// </summary>
public sealed class Interruptions
{
    public const int MaxAttempts = 2;

    /// <summary>Interruptions in a row, per job. Saved, so the count survives the crashes it counts.</summary>
    public Dictionary<Guid, int> Counts { get; set; } = [];

    /// <summary>Counts an interruption; true when the job should run again.</summary>
    public bool Interrupted(Guid jobId)
    {
        var n = Counts.GetValueOrDefault(jobId) + 1;
        Counts[jobId] = n;
        return n <= MaxAttempts;
    }

    /// <summary>A run ended by itself, whatever its outcome; false when nothing was counted.</summary>
    public bool Finished(Guid jobId) => Counts.Remove(jobId);
}
