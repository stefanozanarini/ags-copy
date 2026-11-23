using System.Text;
using AgsCopy.Core;
using AgsCopy.Core.Reports;

namespace AgsCopy.Service;

/// <summary>
/// HTML reports in %ProgramData%\AGS-Copy\reports: index.html for all jobs and
/// &lt;job id&gt;\&lt;run&gt;.html per run. The index is rebuilt after every run and job change.
/// </summary>
public sealed class ReportStore
{
    private readonly JobStore _jobs;
    private readonly HistoryStore _history;
    private readonly ServiceSettings _settings;
    private readonly ILogger<ReportStore> _log;
    private readonly object _lock = new();

    public ReportStore(JobStore jobs, HistoryStore history, ServiceSettings settings, ILogger<ReportStore> log)
    {
        _jobs = jobs;
        _history = history;
        _settings = settings;
        _log = log;
        _jobs.Changed += _ => RebuildIndex();
    }

    /// <summary>Writes a run report and returns its file name.</summary>
    public string WriteRun(Guid jobId, string stamp, RunReport report, string? lang)
    {
        var dir = AppPaths.JobReportDir(jobId);
        Directory.CreateDirectory(dir);
        var file = stamp + ".html";
        File.WriteAllText(Path.Combine(dir, file), ReportBuilder.Run(report, lang), Encoding.UTF8);
        return file;
    }

    public void RebuildIndex()
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(AppPaths.ReportsDir);
                File.WriteAllText(Path.Combine(AppPaths.ReportsDir, ReportBuilder.IndexFile), BuildIndex(), Encoding.UTF8);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(e, "Cannot write the report index");
        }
    }

    /// <summary>One run's report, or null when there is none.</summary>
    public string? ReadRun(Guid jobId, Guid runId)
    {
        var record = _history.Find(jobId, runId);
        if (record?.ReportFile == null) return null;
        var path = Path.Combine(AppPaths.JobReportDir(jobId), record.ReportFile);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>
    /// The index with the run reports it links to, by relative path, so the GUI can rebuild
    /// the folder where the user can read it.
    /// </summary>
    public Dictionary<string, string> Bundle()
    {
        var files = new Dictionary<string, string> { [ReportBuilder.IndexFile] = BuildIndex() };
        foreach (var job in _jobs.All())
        {
            foreach (var run in _history.List(job.Id, ReportBuilder.IndexDots))
            {
                if (run.ReportFile == null) continue;
                var path = Path.Combine(AppPaths.JobReportDir(job.Id), run.ReportFile);
                if (File.Exists(path)) files[Link(job.Id, run.ReportFile)] = File.ReadAllText(path);
            }
        }
        return files;
    }

    public void DeleteJob(Guid jobId)
    {
        try
        {
            if (Directory.Exists(AppPaths.JobReportDir(jobId))) Directory.Delete(AppPaths.JobReportDir(jobId), true);
        }
        catch (IOException e)
        {
            _log.LogWarning(e, "Cannot delete the reports of job {Id}", jobId);
        }
        RebuildIndex();
    }

    private string BuildIndex()
    {
        var entries = _jobs.All()
            .OrderBy(j => j.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(j => new IndexEntry
            {
                JobName = j.Name,
                Mode = j.Mode,
                Source = j.SourceSummary,
                Destination = j.DestinationSummary,
                Enabled = j.Enabled,
                Schedule = WithLanguage(() => JobDescriber.DescribeSchedule(j.Schedule)),
                Folder = j.Id.ToString("N"),
                Runs = _history.List(j.Id, ReportBuilder.IndexDots),
            })
            .ToList();
        return ReportBuilder.Index(entries, (e, r) => r.ReportFile == null ? null : e.Folder + "/" + r.ReportFile, _settings.Language);
    }

    private static string Link(Guid jobId, string file) => jobId.ToString("N") + "/" + file;

    /// <summary>DescribeSchedule speaks Loc.Language, which the service does not otherwise use.</summary>
    private string WithLanguage(Func<string> f)
    {
        lock (_lock)
        {
            var old = Loc.Language;
            Loc.Language = _settings.Language;
            try { return f(); }
            finally { Loc.Language = old; }
        }
    }
}
