using System.Text.Json;
using AgsCopy.Core.Models;

namespace AgsCopy.Core.Ipc;

/// <summary>
/// The GUI and the tray talk to the service over a local named pipe: one JSON line as the
/// request, one JSON line as the response, then the connection closes.
/// </summary>
public static class Pipe
{
    public const string Name = "AgsCopy.Service.v1";
}

public static class Commands
{
    // Allowed to every interactive user (the tray runs without elevation).
    public const string Status = "status";
    public const string ListJobs = "jobs.list";
    public const string History = "history.list";

    // Administrators only.
    public const string GetJob = "jobs.get";
    public const string SaveJob = "jobs.save";
    public const string DeleteJob = "jobs.delete";
    public const string Preview = "jobs.preview";
    public const string RunJob = "jobs.run";
    public const string CancelJob = "jobs.cancel";
    public const string ReadLog = "log.read";
    public const string TestPath = "path.test";
    public const string ListPath = "path.list";
    public const string PauseScheduler = "scheduler.pause";
    public const string RunReport = "report.run";
    public const string ReportBundle = "report.bundle";

    public static bool IsReadOnly(string command) => command is Status or ListJobs or History;
}

public sealed class PipeRequest
{
    public string Command { get; set; } = "";
    /// <summary>Language of the client, for messages the service writes.</summary>
    public string? Lang { get; set; }
    public JsonElement? Payload { get; set; }
}

public sealed class PipeResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public JsonElement? Payload { get; set; }
}

public sealed class IdRequest
{
    public Guid Id { get; set; }
}

public sealed class BoolRequest
{
    public bool Value { get; set; }
}

public sealed class PathTestRequest
{
    public JobEndpoint Endpoint { get; set; } = new();
    /// <summary>Job being edited: a password left blank means "keep the stored one".</summary>
    public Guid? JobId { get; set; }
    public bool IsDestination { get; set; }
}

public sealed class PathTestResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
}

public sealed class PathListRequest
{
    public JobEndpoint Endpoint { get; set; } = new();
    public Guid? JobId { get; set; }
    public bool IsDestination { get; set; }
    /// <summary>Subfolder to list, relative to the endpoint path ("" = the endpoint itself).</summary>
    public string Relative { get; set; } = "";
}

public sealed class PathListResult
{
    /// <summary>False when the folder could not be read (Message says why).</summary>
    public bool Ok { get; set; }
    public bool Exists { get; set; }
    public string Message { get; set; } = "";
    public List<PathEntry> Entries { get; set; } = [];
    /// <summary>Items in the folder; more than Entries when the listing was cut.</summary>
    public int Total { get; set; }
}

public sealed class LogRequest
{
    public Guid JobId { get; set; }
    public Guid RunId { get; set; }
}

public sealed class ReportResponse
{
    public string? Html { get; set; }
}

public sealed class ReportBundleResponse
{
    /// <summary>Relative path (index.html, &lt;job&gt;/&lt;run&gt;.html) → page.</summary>
    public Dictionary<string, string> Files { get; set; } = [];
}

public sealed class LogResponse
{
    public string Text { get; set; } = "";
    public bool Truncated { get; set; }
}

public sealed class ServiceStatus
{
    public string Version { get; set; } = "";
    public bool SchedulerPaused { get; set; }
    public List<JobStatus> Jobs { get; set; } = [];
}

public sealed class JobStatus
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public CopyMode Mode { get; set; }
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public Schedule Schedule { get; set; } = new();

    public bool Running { get; set; }
    public DateTime? RunStartedUtc { get; set; }
    public long ProgressFiles { get; set; }
    public long ProgressBytes { get; set; }

    public DateTime? NextRunLocal { get; set; }
    public RunRecord? LastRun { get; set; }
}
