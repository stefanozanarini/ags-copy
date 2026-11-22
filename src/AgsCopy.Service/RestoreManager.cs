using System.Collections.Concurrent;
using System.Text;
using AgsCopy.Core;
using AgsCopy.Core.Credentials;
using AgsCopy.Core.Ipc;
using AgsCopy.Core.Models;
using AgsCopy.Core.Robocopy;
using AgsCopy.Core.Sync;

namespace AgsCopy.Service;

/// <summary>
/// Browsing a job's backup and restoring from it. A restore reads only the backup and writes only
/// into the folder the user picked, which may be neither inside the job's source (read-only, always)
/// nor inside the backup.
/// </summary>
public sealed class RestoreManager(JobStore store, ILogger<RestoreManager> log)
{
    private sealed class Operation
    {
        public required Guid Id { get; init; }
        public required string Target { get; init; }
        public required CancellationTokenSource Cancel { get; init; }
        public ProgressCounter Progress { get; } = new("x");
        public bool Running { get; set; } = true;
        public bool Scanning { get; set; } = true;
        public long TotalFiles { get; set; }
        public long TotalBytes { get; set; }
        public RestoreResult? Result { get; set; }
        public string? Error { get; set; }
        public bool Cancelled { get; set; }
        public DateTime Finished { get; set; }
    }

    private readonly ConcurrentDictionary<Guid, Operation> _operations = new();

    /// <summary>The job's destination, opened for reading (with its share connected while it is in use).</summary>
    private sealed class Backup(ISourceStore store, List<NetworkShare.Lease> leases) : IDisposable
    {
        public ISourceStore Store => store;

        public void Dispose()
        {
            store.Dispose();
            foreach (var l in leases) l.Dispose();
        }
    }

    /// <summary>With several sources, one view over the source folders and their versions.</summary>
    private Backup Open(CopyJob job, string? lang)
    {
        if (job.Destination.IsSftp) return new Backup(View(job, Sftp.Open(job.Destination, null, lang)), []);
        var leases = JobExecutor.Connect(new CopyJob { Destination = job.Destination, Source = new JobEndpoint() }, null, lang);
        return new Backup(View(job, new LocalSource(PathUtil.NormalizeDir(job.Destination.Path))), leases);
    }

    private static ISourceStore View(CopyJob job, ISourceStore backup) =>
        job.MultiSource ? new SourceFoldersBackup(backup, job.Sources.Select(s => s.Folder ?? "").ToList()) : backup;

    private CopyJob Job(Guid id, string? lang) => store.Get(id) ?? throw new ServiceException(Loc.Tl(lang, "err.nojob"));

    private static RestorePoint Point(string? stamp) => stamp == null ? RestorePoint.Current : new RestorePoint(stamp, null);

    public List<RestorePoint> Points(Guid jobId, string? lang)
    {
        using var backup = Open(Job(jobId, lang), lang);
        return new RestoreBrowser(backup.Store).Points();
    }

    public List<RestoreItem> List(RestoreListRequest req, string? lang)
    {
        using var backup = Open(Job(req.JobId, lang), lang);
        return new RestoreBrowser(backup.Store).List(Point(req.Stamp), req.Relative.Replace('\\', '/').Trim('/'));
    }

    public Guid Start(RestoreStartRequest req, string? lang)
    {
        var job = Job(req.JobId, lang);
        var target = CheckTarget(job, req.Target, lang, DfsClient.Resolve);
        if (req.Items.Count == 0) throw new ServiceException(Loc.Tl(lang, "err.restore.nothing"));

        var op = new Operation { Id = Guid.NewGuid(), Target = target, Cancel = new CancellationTokenSource() };
        _operations[op.Id] = op;
        Forget();
        _ = Task.Run(() => Run(op, job, req, lang));
        return op.Id;
    }

    private void Run(Operation op, CopyJob job, RestoreStartRequest req, string? lang)
    {
        var ct = op.Cancel.Token;
        var logFile = Path.Combine(AppPaths.JobLogDir(job.Id), $"restore-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
            using var writer = new StreamWriter(logFile, false, new UTF8Encoding(true));
            writer.WriteLine($"AGS-Copy restore — {job.Name} — {(req.Stamp ?? "current")} → {op.Target}");
            using var backup = Open(job, lang);
            var browser = new RestoreBrowser(backup.Store);
            var items = browser.Expand(Point(req.Stamp), req.Items, ct).ToList();
            op.TotalFiles = items.Count(i => !i.IsDirectory);
            op.TotalBytes = items.Where(i => !i.IsDirectory).Sum(i => i.Size);
            op.Scanning = false;
            op.Result = browser.Restore(items, op.Target, req.Overwrite, op.Progress, writer.WriteLine, ct);
            foreach (var e in op.Result.Errors) writer.WriteLine($"ERROR {e.Code} {e.Action} {e.Path}: {e.Message}");
            log.LogInformation("Restore of {Job} into {Target}: {Files} files, {Errors} errors", job.Name, op.Target, op.Result.Files, op.Result.Errors.Count);
        }
        catch (OperationCanceledException)
        {
            op.Cancelled = true;
        }
        catch (Exception e)
        {
            op.Error = e.Message;
            log.LogError(e, "Restore of {Job} failed", job.Name);
        }
        finally
        {
            op.Running = false;
            op.Finished = DateTime.UtcNow;
            op.Cancel.Dispose();
        }
    }

    public RestoreStatusResponse Status(Guid id, string? lang)
    {
        if (!_operations.TryGetValue(id, out var op)) throw new ServiceException(Loc.Tl(lang, "err.restore.unknown"));
        return new RestoreStatusResponse
        {
            Id = op.Id,
            Running = op.Running,
            Scanning = op.Scanning && op.Running,
            Files = op.Result?.Files ?? op.Progress.Files,
            Bytes = op.Result?.Bytes ?? op.Progress.Bytes,
            TotalFiles = op.TotalFiles,
            TotalBytes = op.TotalBytes,
            Skipped = op.Result?.Skipped ?? 0,
            Cancelled = op.Cancelled,
            Error = op.Error,
            Target = op.Target,
            Errors = op.Result?.Errors.Take(200).ToList() ?? [],
            ErrorCount = op.Result?.Errors.Count ?? 0,
        };
    }

    public bool Cancel(Guid id)
    {
        if (!_operations.TryGetValue(id, out var op) || !op.Running) return false;
        try { op.Cancel.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }

    /// <summary>Finished operations are kept an hour, for the window that started them.</summary>
    private void Forget()
    {
        foreach (var (id, op) in _operations)
            if (!op.Running && DateTime.UtcNow - op.Finished > TimeSpan.FromHours(1)) _operations.TryRemove(id, out _);
    }

    /// <summary>
    /// A local or \\server\share folder, not inside the job's source (the source is read-only, also for
    /// restores) and not inside the backup.
    /// </summary>
    /// <param name="resolve">Maps a DFS path to the share behind it (the same folder may have two names).</param>
    public static string CheckTarget(CopyJob job, string target, string? lang, Func<string, string>? resolve = null)
    {
        if (PathUtil.IsSftp(target) || !PathUtil.IsAbsolute(target)) throw new ServiceException(Loc.Tl(lang, "err.restore.target"));
        var t = PathUtil.NormalizeDir(target);
        foreach (var source in job.Sources.Where(s => !s.IsSftp))
            if (Overlap(t, source.Path, resolve)) throw new ServiceException(Loc.Fl(lang, "err.restore.source", source.Path));
        if (!job.Destination.IsSftp && Overlap(t, job.Destination.Path, resolve)) throw new ServiceException(Loc.Fl(lang, "err.restore.backup", job.Destination.Path));
        return t;
    }

    private static bool Overlap(string a, string b, Func<string, string>? resolve) =>
        PathUtil.Overlaps(a, b) || (resolve != null && PathUtil.Overlaps(resolve(a), resolve(b)));
}
