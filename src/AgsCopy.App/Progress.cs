using AgsCopy.Core.Ipc;

namespace AgsCopy.App;

/// <summary>How far a running job is, for the job list and the tray.</summary>
public static class Progress
{
    /// <summary>Percentage of the bytes (or files, for empty files) to copy; null while they are not known.</summary>
    public static double? Percent(JobStatus s)
    {
        if (!s.Running || s.Scanning || s.CountsOnly) return null;
        if (s.TotalBytes > 0) return Math.Min(100, 100.0 * s.ProgressBytes / s.TotalBytes);
        if (s.TotalFiles > 0) return Math.Min(100, 100.0 * s.ProgressFiles / s.TotalFiles);
        return 100; // nothing to copy
    }

    public static string Describe(JobStatus s)
    {
        var elapsed = DateTime.UtcNow - (s.RunStartedUtc ?? DateTime.UtcNow);
        if (s is { Running: true, Scanning: false, CountsOnly: true })
            return Loc.F("main.copying", Fmt.Bytes(s.ProgressBytes), Fmt.Count(s.ProgressFiles), Fmt.Duration(elapsed));
        if (Percent(s) is not { } p) return Loc.F("main.scanning", Fmt.Duration(elapsed));
        var text = Loc.F("main.progress", Math.Floor(p), Fmt.Bytes(s.ProgressBytes), Fmt.Bytes(s.TotalBytes), Fmt.Count(s.ProgressFiles), Fmt.Count(s.TotalFiles));
        // Remaining time from the pace so far, once there is a pace to speak of.
        if (p is >= 2 and < 100 && elapsed.TotalSeconds >= 20)
            text += " · " + Loc.F("main.remaining", Fmt.Duration(TimeSpan.FromSeconds(elapsed.TotalSeconds * (100 - p) / p)));
        return text;
    }

    /// <summary>"42%", or the bytes copied while the totals are unknown.</summary>
    public static string Short(JobStatus s) =>
        Percent(s) is { } p ? $"{Math.Floor(p)}%" : s is { Running: true, Scanning: false, CountsOnly: true } ? Fmt.Bytes(s.ProgressBytes) : Loc.T("main.scanning.short");
}
