using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using AgsCopy.Core;
using AgsCopy.Core.Credentials;
using AgsCopy.Core.Ipc;
using AgsCopy.Core.Models;
using AgsCopy.Core.Robocopy;

namespace AgsCopy.Service;

public sealed class RunningJob
{
    public required RunRecord Record { get; init; }
    public required CancellationTokenSource Cancel { get; init; }
    public List<ProgressCounter> Counters { get; } = [];

    public long Files
    {
        get { lock (Counters) return Counters.Sum(c => c.Files); }
    }

    public long Bytes
    {
        get { lock (Counters) return Counters.Sum(c => c.Bytes); }
    }
}

/// <summary>Runs, previews and checks jobs.</summary>
public sealed class JobExecutor(HistoryStore history, ILogger<JobExecutor> log) : IHostedService
{
    private const int MaxExtraEntriesCounted = 200_000;
    private const long MaxLogBytes = 4 * 1024 * 1024;

    private readonly ConcurrentDictionary<Guid, RunningJob> _running = new();
    private readonly ConcurrentDictionary<Guid, Task> _tasks = new();

    public RunningJob? Running(Guid jobId) => _running.TryGetValue(jobId, out var r) ? r : null;

    public bool TryStart(CopyJob job, RunTrigger trigger)
    {
        var rj = new RunningJob
        {
            Record = new RunRecord { JobId = job.Id, JobName = job.Name, Trigger = trigger, StartUtc = DateTime.UtcNow },
            Cancel = new CancellationTokenSource(),
        };
        if (!_running.TryAdd(job.Id, rj)) return false;
        _tasks[job.Id] = Task.Run(() => RunAsync(job, rj));
        return true;
    }

    public bool Cancel(Guid jobId)
    {
        if (!_running.TryGetValue(jobId, out var rj)) return false;
        rj.Cancel.Cancel();
        return true;
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken ct)
    {
        foreach (var rj in _running.Values) rj.Cancel.Cancel();
        await Task.WhenAny(Task.WhenAll(_tasks.Values), Task.Delay(TimeSpan.FromSeconds(20), ct));
    }

    private async Task RunAsync(CopyJob job, RunningJob rj)
    {
        var record = rj.Record;
        var ct = rj.Cancel.Token;
        var logDir = AppPaths.JobLogDir(job.Id);
        Directory.CreateDirectory(logDir);
        var stamp = record.StartUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss");
        record.LogFile = stamp + ".log";
        history.Put(record);
        log.LogInformation("Job {Name} started ({Trigger})", job.Name, record.Trigger);

        var src = PathUtil.NormalizeDir(job.Source.Path);
        var dst = PathUtil.NormalizeDir(job.Destination.Path);
        var parts = new List<(string Label, string Command, string File)>();
        var leases = new List<NetworkShare.Lease>();
        try
        {
            leases.AddRange(Connect(job, null, null));
            if (!Directory.Exists(src)) throw new ServiceException(Loc.Fl(null, "err.nosource", src));

            var names = job.Options.Processes > 1 ? TopLevelFolders(src, job.Options.SkipJunctions) : [];
            var tasks = ParallelPlan.Build(job, names);
            var consoleSource = RobocopyProcess.AsConsoleText(src);
            var combined = 0;
            var total = RobocopySummary.Empty;

            for (var i = 0; i < tasks.Count; i++)
                parts.Add((tasks[i].Label, "", Path.Combine(logDir, $"{stamp}-{i:0000}.part")));

            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, job.Options.Processes), CancellationToken = ct };
            await Parallel.ForEachAsync(Enumerable.Range(0, tasks.Count), options, async (i, token) =>
            {
                var t = tasks[i];
                var args = RobocopyArgs.Build(job, t.Source, t.Destination, t.Pass, false, parts[i].File, true, t.RootExcludedDirs);
                lock (parts) parts[i] = parts[i] with { Command = "robocopy " + CommandLine.Join(args) };

                var counter = new ProgressCounter(consoleSource);
                lock (rj.Counters) rj.Counters.Add(counter);
                var summary = new SummaryCollector();
                var code = await RobocopyProcess.RunAsync(args, line =>
                {
                    counter.Add(line);
                    summary.Add(line);
                }, token);

                lock (parts)
                {
                    combined |= code;
                    if (summary.Summary is { } s) total = total.Add(s);
                }
            });

            record.ExitCode = combined;
            record.Outcome = RobocopyExitCode.Outcome(combined);
            record.FilesCopied = total.Files.Copied;
            record.BytesCopied = total.Bytes.Copied;
            record.FilesSkipped = total.Files.Skipped;
            record.FilesFailed = total.Files.Failed;
            record.FilesExtra = total.Files.Extras;
            record.DirsExtra = total.Dirs.Extras;
            if (tasks.Count > 1) record.Message = Loc.Fl(null, "run.parallel", tasks.Count);
        }
        catch (OperationCanceledException)
        {
            record.Outcome = RunOutcome.Cancelled;
        }
        catch (Exception e)
        {
            record.Outcome = RunOutcome.Failed;
            record.Message = e.Message;
            log.LogError(e, "Job {Name} failed", job.Name);
        }
        finally
        {
            foreach (var l in leases) l.Dispose();
            record.EndUtc = DateTime.UtcNow;
            if (record.Outcome == RunOutcome.Running) record.Outcome = RunOutcome.Failed;
            try
            {
                MergeLogs(Path.Combine(logDir, record.LogFile), job, record, parts);
            }
            catch (IOException e)
            {
                log.LogWarning(e, "Cannot write the log of job {Name}", job.Name);
            }
            history.Put(record);
            _running.TryRemove(job.Id, out _);
            _tasks.TryRemove(job.Id, out _);
            rj.Cancel.Dispose();
            log.LogInformation("Job {Name} finished: {Outcome}, exit code {Code}", job.Name, record.Outcome, record.ExitCode);
        }
    }

    /// <summary>
    /// Runs robocopy /L over the whole tree and reports what a real run would copy and delete.
    /// </summary>
    public async Task<PreviewResult> PreviewAsync(CopyJob job, CopyJob? stored, string? lang, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var src = PathUtil.NormalizeDir(job.Source.Path);
        var dst = PathUtil.NormalizeDir(job.Destination.Path);
        var agg = new PreviewAggregator(src, dst, job.Mode);
        var result = agg.Result;
        var leases = new List<NetworkShare.Lease>();
        var logFile = Path.Combine(AppPaths.TempDir, $"preview-{Guid.NewGuid():N}.log");
        try
        {
            leases.AddRange(Connect(job, stored, lang));
            if (!Directory.Exists(src))
            {
                result.Error = Loc.Fl(lang, "err.nosource", src);
                return result;
            }
            result.DestinationExists = Directory.Exists(dst);

            var args = RobocopyArgs.Build(job, src, dst, RobocopyPass.Full, true, logFile, false);
            var code = await RobocopyProcess.RunAsync(args, null, ct);
            if (File.Exists(logFile))
            {
                foreach (var line in File.ReadLines(logFile)) agg.Add(line);
            }
            agg.Complete(code);
            if (job.Mode == CopyMode.Mirror) CountExtraFolders(result, ct);

            var real = RobocopyArgs.Build(job, src, dst, RobocopyPass.Full, false, null, false);
            result.CommandLine = "robocopy " + CommandLine.Join(real);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            result.Error = e.Message;
        }
        finally
        {
            foreach (var l in leases) l.Dispose();
            try { File.Delete(logFile); } catch (IOException) { }
            result.Elapsed = sw.Elapsed;
        }
        return result;
    }

    /// <summary>Robocopy lists an extra folder once; count what it holds, since all of it goes.</summary>
    private static void CountExtraFolders(PreviewResult r, CancellationToken ct)
    {
        var budget = MaxExtraEntriesCounted;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var item in r.ExtraItems.Where(i => i.IsDirectory))
        {
            if (budget <= 0) break;
            long files = 0, bytes = 0;
            try
            {
                foreach (var f in new DirectoryInfo(item.Path).EnumerateFiles("*", options))
                {
                    ct.ThrowIfCancellationRequested();
                    files++;
                    bytes += f.Length;
                    if (--budget <= 0) break;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
            item.Files = files;
            item.Size = bytes;
            r.ExtraFiles += files;
            r.ExtraBytes += bytes;
        }
    }

    /// <summary>"Check access" in the editor: can the service reach (and, for a destination, write) the path.</summary>
    public PathTestResult TestPath(PathTestRequest req, CopyJob? stored, string? lang)
    {
        var path = PathUtil.NormalizeDir(req.Endpoint.Path);
        if (!PathUtil.IsAbsolute(path))
            return new PathTestResult { Message = Loc.Tl(lang, req.IsDestination ? "val.destination" : "val.source") };

        NetworkShare.Lease? lease = null;
        try
        {
            var storedEndpoint = req.IsDestination ? stored?.Destination : stored?.Source;
            if (req.Endpoint.IsNetwork && req.Endpoint.HasCredential)
                lease = NetworkShare.Connect(path, req.Endpoint.Credential!.UserName, PasswordFor(req.Endpoint, storedEndpoint, lang));

            if (Directory.Exists(path))
            {
                if (req.IsDestination)
                {
                    var probe = Path.Combine(path, $".agscopy-{Guid.NewGuid():N}.tmp");
                    File.WriteAllText(probe, "AGS-Copy");
                    File.Delete(probe);
                }
                else
                {
                    using var e = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
                    e.MoveNext();
                }
                return new PathTestResult { Ok = true, Message = Loc.Tl(lang, req.IsDestination ? "test.ok.write" : "test.ok.read") };
            }

            if (req.IsDestination && Path.GetDirectoryName(path) is { } parent && Directory.Exists(parent))
                return new PathTestResult { Ok = true, Message = Loc.Tl(lang, "test.willcreate") };
            return new PathTestResult { Message = Loc.Tl(lang, "test.notfound") };
        }
        catch (UnauthorizedAccessException)
        {
            return new PathTestResult { Message = Loc.Tl(lang, "test.denied") };
        }
        catch (Exception e) when (e is IOException or NetworkShareException or ServiceException)
        {
            return new PathTestResult { Message = e.Message };
        }
        finally
        {
            lease?.Dispose();
        }
    }

    /// <summary>
    /// Connects the network endpoints that have credentials. <paramref name="stored"/> is the
    /// saved version of a job being edited, whose passwords fill those not retyped.
    /// </summary>
    private static List<NetworkShare.Lease> Connect(CopyJob job, CopyJob? stored, string? lang)
    {
        var leases = new List<NetworkShare.Lease>();
        try
        {
            foreach (var (e, s) in new[] { (job.Source, stored?.Source), (job.Destination, stored?.Destination) })
            {
                if (e.IsNetwork && e.HasCredential)
                    leases.Add(NetworkShare.Connect(e.Path, e.Credential!.UserName, PasswordFor(e, s, lang)));
            }
            return leases;
        }
        catch
        {
            foreach (var l in leases) l.Dispose();
            throw;
        }
    }

    private static string PasswordFor(JobEndpoint e, JobEndpoint? stored, string? lang)
    {
        var c = e.Credential!;
        if (!string.IsNullOrEmpty(c.Password)) return c.Password;
        if (!string.IsNullOrEmpty(c.ProtectedPassword)) return CredentialProtector.Unprotect(c.ProtectedPassword);
        if (stored?.Credential is { ProtectedPassword: { } p } s && string.Equals(s.UserName, c.UserName, StringComparison.OrdinalIgnoreCase))
            return CredentialProtector.Unprotect(p);
        throw new ServiceException(Loc.Fl(lang, "err.password", c.UserName));
    }

    private static List<string> TopLevelFolders(string source, bool skipJunctions) =>
        new DirectoryInfo(source)
            .EnumerateDirectories()
            .Where(d => !skipJunctions || !d.Attributes.HasFlag(FileAttributes.ReparsePoint))
            .Select(d => d.Name)
            .ToList();

    /// <summary>Joins the per-process Unicode logs into one UTF-8 log and removes them.</summary>
    private static void MergeLogs(string target, CopyJob job, RunRecord record, List<(string Label, string Command, string File)> parts)
    {
        using var w = new StreamWriter(target, false, new UTF8Encoding(true));
        w.WriteLine($"AGS-Copy — {job.Name}");
        w.WriteLine($"Start: {record.StartUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}   End: {record.EndUtc?.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        w.WriteLine($"Outcome: {record.Outcome}   Exit code: {record.ExitCode}");
        if (record.Message != null) w.WriteLine(record.Message);
        w.WriteLine();

        foreach (var (label, command, file) in parts)
        {
            w.WriteLine(new string('=', 79));
            w.WriteLine($"[{label}] {command}");
            w.WriteLine(new string('=', 79));
            if (!File.Exists(file)) continue;
            using (var r = new StreamReader(file, Encoding.Unicode, detectEncodingFromByteOrderMarks: true))
            {
                string? line;
                while ((line = r.ReadLine()) != null) w.WriteLine(line);
            }
            File.Delete(file);
        }
    }

    public LogResponse ReadLog(Guid jobId, Guid runId, string? lang)
    {
        var record = history.Find(jobId, runId);
        if (record?.LogFile == null) return new LogResponse { Text = Loc.Tl(lang, "log.none") };
        var path = Path.Combine(AppPaths.JobLogDir(jobId), record.LogFile);
        if (!File.Exists(path))
            return new LogResponse { Text = Loc.Tl(lang, record.Outcome == RunOutcome.Running ? "log.running" : "log.none") };

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var truncated = fs.Length > MaxLogBytes;
        if (truncated) fs.Seek(-MaxLogBytes, SeekOrigin.End);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        return new LogResponse { Text = reader.ReadToEnd(), Truncated = truncated };
    }
}
