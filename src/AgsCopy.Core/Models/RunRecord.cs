namespace AgsCopy.Core.Models;

public enum RunOutcome
{
    Running,
    /// <summary>Exit codes 0-3: everything copied (or nothing to do).</summary>
    Success,
    /// <summary>Exit codes 4-7: mismatched files or folders, nothing failed.</summary>
    Warning,
    /// <summary>Exit code 8 or more, or the job could not start.</summary>
    Failed,
    Cancelled,
}

public enum RunTrigger
{
    Manual,
    Scheduled,
    /// <summary>Run again after a crash of the service cut the previous run short.</summary>
    Resumed,
}

public sealed class RunRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public string JobName { get; set; } = "";
    public RunTrigger Trigger { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public RunOutcome Outcome { get; set; } = RunOutcome.Running;

    /// <summary>All robocopy exit codes of the run OR-ed together.</summary>
    public int ExitCode { get; set; }

    public long FilesCopied { get; set; }
    public long BytesCopied { get; set; }
    public long FilesSkipped { get; set; }
    public long FilesFailed { get; set; }
    public long FilesExtra { get; set; }
    public long DirsExtra { get; set; }

    /// <summary>Destination files and folders moved to the versions folder before the copy.</summary>
    public long VersionsArchived { get; set; }

    /// <summary>Distinct paths robocopy reported an error for.</summary>
    public long Errors { get; set; }

    public string? Message { get; set; }

    /// <summary>File name of the log inside the job's log folder.</summary>
    public string? LogFile { get; set; }

    /// <summary>File name of the HTML report inside the job's report folder.</summary>
    public string? ReportFile { get; set; }

    public TimeSpan Duration => (EndUtc ?? DateTime.UtcNow) - StartUtc;
}
