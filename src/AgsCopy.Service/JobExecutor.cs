using System.Collections.Concurrent;
using System.ComponentModel;
using ManagementException = System.Management.ManagementException;
using System.Diagnostics;
using System.Text;
using AgsCopy.Core;
using AgsCopy.Core.Credentials;
using AgsCopy.Core.Ipc;
using AgsCopy.Core.Models;
using AgsCopy.Core.Reports;
using AgsCopy.Core.Robocopy;

namespace AgsCopy.Service;

public sealed class RunningJob
{
    public required RunRecord Record { get; init; }
    public required CancellationTokenSource Cancel { get; init; }
    public bool ShadowCopyFailed { get; set; }
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
public sealed class JobExecutor(HistoryStore history, ReportStore reports, ServiceSettings settings, ILogger<JobExecutor> log) : IHostedService
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

    public Task StartAsync(CancellationToken ct)
    {
        ShadowCopy.CleanupLeftovers(log);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        foreach (var rj in _running.Values) rj.Cancel.Cancel();
        await Task.WhenAny(Task.WhenAll(_tasks.Values), Task.Delay(TimeSpan.FromSeconds(20), ct));
    }

    private async Task RunAsync(CopyJob job, RunningJob rj)
    {
        var record = rj.Record;
        var ct = rj.Cancel.Token;
        var lang = settings.Language;
        var logDir = AppPaths.JobLogDir(job.Id);
        Directory.CreateDirectory(logDir);
        var startLocal = record.StartUtc.ToLocalTime();
        var stamp = startLocal.ToString("yyyyMMdd-HHmmss");
        record.LogFile = stamp + ".log";
        history.Put(record);
        log.LogInformation("Job {Name} started ({Trigger})", job.Name, record.Trigger);

        var src = PathUtil.NormalizeDir(job.Source.Path);
        var dst = PathUtil.NormalizeDir(job.Destination.Path);
        var parts = new List<(string Label, string Command, string File)>();
        var leases = new List<NetworkShare.Lease>();
        var notes = new List<string>();
        var versions = new ArchiveResult();
        ShadowCopy? snapshot = null;
        try
        {
            leases.AddRange(Connect(job, null, null));
            if (!Directory.Exists(src)) throw new ServiceException(Loc.Fl(lang, "err.nosource", src));

            // From here on runJob reads the source from the snapshot, when there is one.
            var runJob = job;
            if (job.Options.UseShadowCopy && !job.Source.IsNetwork)
            {
                try
                {
                    snapshot = await ShadowCopy.CreateAsync(src, record.Id, ct);
                    runJob = SnapshotPaths.ForSnapshot(job, snapshot.VolumeRoot, snapshot.LinkPath);
                    notes.Add(Loc.Fl(lang, "run.vss", snapshot.VolumeRoot, runJob.Source.Path));
                    log.LogInformation("Job {Name}: shadow copy {Id} of {Volume}", job.Name, snapshot.ShadowId, snapshot.VolumeRoot);
                }
                catch (Exception e) when (e is ShadowCopyException or ManagementException or Win32Exception or UnauthorizedAccessException or IOException)
                {
                    // Copy anyway: everything but the open files still gets copied.
                    rj.ShadowCopyFailed = true;
                    notes.Add(Loc.Fl(lang, "run.vss.failed", e.Message));
                    log.LogWarning(e, "Job {Name}: shadow copy failed, copying without it", job.Name);
                }
            }
            src = PathUtil.NormalizeDir(runJob.Source.Path);

            var names = job.Options.AutoTune || job.Options.Processes > 1 ? TopLevelFolders(src, job.Options.SkipJunctions) : [];
            if (job.Options.AutoTune)
            {
                var tune = AutoTune.Compute(await ResourceProbe.MeasureAsync(ct), IsNetwork(job), names.Count);
                runJob = Json.Clone(runJob);
                runJob.Options.Processes = tune.Processes;
                runJob.Options.Threads = tune.Threads;
                notes.Add(DescribeTune(tune, lang));
                log.LogInformation("Job {Name}: auto-tuned to {Processes} processes x {Threads} threads", job.Name, tune.Processes, tune.Threads);
            }

            if (job.Options.KeepVersions && Directory.Exists(dst))
            {
                versions = await ArchiveVersionsAsync(runJob, src, dst, startLocal, ct);
                record.VersionsArchived = versions.Count;
                if (versions.Count > 0) notes.Add(Loc.Fl(lang, "run.versions", Fmt.Count(versions.Count, Loc.CultureOf(lang)), versions.Folder));
            }

            var tasks = ParallelPlan.Build(runJob, names);
            var consoleSource = RobocopyProcess.AsConsoleText(src);
            var combined = 0;
            var total = RobocopySummary.Empty;

            for (var i = 0; i < tasks.Count; i++)
                parts.Add((tasks[i].Label, "", Path.Combine(logDir, $"{stamp}-{i:0000}.part")));

            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, runJob.Options.Processes), CancellationToken = ct };
            await Parallel.ForEachAsync(Enumerable.Range(0, tasks.Count), options, async (i, token) =>
            {
                var t = tasks[i];
                var args = RobocopyArgs.Build(runJob, t.Source, t.Destination, t.Pass, false, parts[i].File, true, t.RootExcludedDirs);
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
            if (tasks.Count > 1) notes.Add(Loc.Fl(lang, "run.parallel", tasks.Count));

            if (job.Options.KeepVersions)
            {
                versions.FoldersRemoved = RemoveExpiredVersions(dst, job.Options.VersionRetentionDays);
                if (versions.FoldersRemoved > 0) notes.Add(Loc.Fl(lang, "run.versions.removed", versions.FoldersRemoved, job.Options.VersionRetentionDays));
            }

            // Without the snapshot open files may be missing, and a file that could not be
            // archived may have been overwritten: never report a clean success then.
            if ((rj.ShadowCopyFailed || versions.Errors.Count > 0) && record.Outcome == RunOutcome.Success) record.Outcome = RunOutcome.Warning;
        }
        catch (OperationCanceledException)
        {
            record.Outcome = RunOutcome.Cancelled;
        }
        catch (Exception e)
        {
            record.Outcome = RunOutcome.Failed;
            notes.Insert(0, e.Message);
            log.LogError(e, "Job {Name} failed", job.Name);
        }
        finally
        {
            if (snapshot != null)
            {
                try
                {
                    snapshot.Dispose();
                }
                catch (Exception e)
                {
                    log.LogWarning(e, "Job {Name}: cannot remove shadow copy {Id}", job.Name, snapshot.ShadowId);
                }
            }
            foreach (var l in leases) l.Dispose();
            if (notes.Count > 0) record.Message = string.Join(" ", notes);
            record.EndUtc = DateTime.UtcNow;
            if (record.Outcome == RunOutcome.Running) record.Outcome = RunOutcome.Failed;

            var found = new LogFindings(dst, job.Mode == CopyMode.Mirror && !job.Options.KeepVersions);
            try
            {
                MergeLogs(Path.Combine(logDir, record.LogFile), job, record, parts, found);
            }
            catch (IOException e)
            {
                log.LogWarning(e, "Cannot write the log of job {Name}", job.Name);
            }

            try
            {
                var errors = versions.Errors.Concat(found.Errors.Errors).ToList();
                record.Errors = versions.Errors.Count + found.Errors.Count;
                record.ReportFile = reports.WriteRun(job.Id, stamp, new RunReport
                {
                    JobName = job.Name,
                    Source = job.Source.Path,
                    Destination = job.Destination.Path,
                    Mode = job.Mode,
                    Record = record,
                    Notes = notes,
                    Errors = errors,
                    ErrorCount = (int)record.Errors,
                    KeepVersions = job.Options.KeepVersions,
                    RetentionDays = job.Options.VersionRetentionDays,
                    VersionsFolder = versions.Folder ?? Versions.Root(dst),
                    Archived = versions.Moved,
                    ArchivedCount = versions.Count,
                    VersionFoldersRemoved = versions.FoldersRemoved,
                    Deleted = found.Deleted,
                    DeletedCount = found.DeletedCount,
                    LogFile = record.LogFile,
                }, lang);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.LogWarning(e, "Cannot write the report of job {Name}", job.Name);
            }

            history.Put(record);
            reports.RebuildIndex();
            _running.TryRemove(job.Id, out _);
            _tasks.TryRemove(job.Id, out _);
            rj.Cancel.Dispose();
            log.LogInformation("Job {Name} finished: {Outcome}, exit code {Code}", job.Name, record.Outcome, record.ExitCode);
        }
    }

    private sealed class ArchiveResult
    {
        public string? Folder { get; set; }
        public long Count { get; set; }
        public List<VersionMove> Moved { get; } = [];
        public List<CopyError> Errors { get; } = [];
        public int FoldersRemoved { get; set; }
    }

    /// <summary>
    /// Moves what the copy is about to overwrite or (mirror) delete in the destination into
    /// this run's versions folder. Robocopy /L tells what that is; nothing in the source moves.
    /// </summary>
    private async Task<ArchiveResult> ArchiveVersionsAsync(CopyJob runJob, string src, string dst, DateTime startLocal, CancellationToken ct)
    {
        var result = new ArchiveResult { Folder = Versions.RunFolder(dst, startLocal) };
        var listFile = Path.Combine(AppPaths.TempDir, $"versions-{Guid.NewGuid():N}.log");
        try
        {
            var args = RobocopyArgs.Build(runJob, src, dst, RobocopyPass.Full, true, listFile, false);
            await RobocopyProcess.RunAsync(args, null, ct);
            if (!File.Exists(listFile)) return result;

            foreach (var m in Versions.Plan(File.ReadLines(listFile), src, dst, runJob.Mode))
            {
                ct.ThrowIfCancellationRequested();
                var target = PathUtil.Combine(result.Folder, m.RelativePath);
                try
                {
                    if (m.IsDirectory ? !Directory.Exists(m.DestinationPath) : !File.Exists(m.DestinationPath)) continue; // a new file
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (m.IsDirectory) Directory.Move(m.DestinationPath, target);
                    else File.Move(m.DestinationPath, target);
                    result.Count++;
                    if (result.Moved.Count < RunReport.MaxListedItems) result.Moved.Add(m);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (result.Errors.Count < ErrorCollector.MaxErrors)
                        result.Errors.Add(new CopyError(m.DestinationPath, e.HResult & 0xFFFF, "→ " + Versions.FolderName, e.Message));
                }
            }
            return result;
        }
        finally
        {
            try { File.Delete(listFile); } catch (IOException) { }
        }
    }

    /// <summary>Deletes version folders older than the retention; returns how many.</summary>
    private int RemoveExpiredVersions(string dst, int retentionDays)
    {
        var root = new DirectoryInfo(Versions.Root(dst));
        if (!root.Exists || root.Attributes.HasFlag(FileAttributes.ReparsePoint)) return 0;
        var removed = 0;
        var names = root.EnumerateDirectories().Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint)).Select(d => d.Name).ToList();
        foreach (var name in Versions.Expired(names, DateTime.Now, retentionDays))
        {
            var dir = Path.Combine(root.FullName, name);
            try
            {
                // Copies keep the read-only attribute, which would stop the delete.
                foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    if (f.IsReadOnly) f.IsReadOnly = false;
                Directory.Delete(dir, recursive: true);
                removed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.LogWarning(e, "Cannot delete the old versions in {Dir}", dir);
            }
        }
        return removed;
    }

    private static bool IsNetwork(CopyJob job) => job.Source.IsNetwork || job.Destination.IsNetwork;

    private static string DescribeTune(TuneResult t, string? lang) =>
        Loc.Fl(lang, "run.auto", t.Processes, t.Threads, t.Resources.LogicalCores, (int)Math.Round(t.Resources.CpuLoad * 100), Fmt.Bytes(t.Resources.AvailableRamBytes, Loc.CultureOf(lang)));

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
            result.KeepVersions = job.Options.KeepVersions;

            if (job.Options.AutoTune)
            {
                var tune = AutoTune.Compute(await ResourceProbe.MeasureAsync(ct), IsNetwork(job), 1);
                job = Json.Clone(job);
                job.Options.Processes = 1;
                job.Options.Threads = tune.Threads;
            }
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
    private const int MaxListed = 2000;

    /// <summary>One folder level of a source or destination, for the layout preview. Only reads.</summary>
    public PathListResult ListPath(PathListRequest req, CopyJob? stored, string? lang)
    {
        var root = PathUtil.NormalizeDir(req.Endpoint.Path);
        if (!PathUtil.IsAbsolute(root))
            return new PathListResult { Message = Loc.Tl(lang, req.IsDestination ? "val.destination" : "val.source") };
        var rel = req.Relative.Replace('/', '\\').Trim('\\');
        if (rel.Split('\\').Any(p => p is "." or ".."))
            return new PathListResult { Message = Loc.Tl(lang, "test.notfound") };
        var path = rel.Length == 0 ? root : PathUtil.Combine(root, rel);

        NetworkShare.Lease? lease = null;
        try
        {
            var storedEndpoint = req.IsDestination ? stored?.Destination : stored?.Source;
            if (req.Endpoint.IsNetwork && req.Endpoint.HasCredential)
                lease = NetworkShare.Connect(root, req.Endpoint.Credential!.UserName, PasswordFor(req.Endpoint, storedEndpoint, lang));

            var dir = new DirectoryInfo(path);
            if (!dir.Exists) return new PathListResult { Ok = true, Message = Loc.Tl(lang, "test.notfound") };

            var result = new PathListResult { Ok = true, Exists = true };
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };
            foreach (var i in dir.EnumerateFileSystemInfos("*", options))
            {
                result.Total++;
                if (result.Entries.Count >= MaxListed) continue;
                var isDir = (i.Attributes & FileAttributes.Directory) != 0;
                result.Entries.Add(new PathEntry
                {
                    Name = i.Name,
                    IsDirectory = isDir,
                    Size = isDir ? 0 : ((FileInfo)i).Length,
                    ModifiedUtc = i.LastWriteTimeUtc,
                });
            }
            return result;
        }
        catch (UnauthorizedAccessException)
        {
            return new PathListResult { Message = Loc.Tl(lang, "test.denied") };
        }
        catch (Exception e) when (e is IOException or NetworkShareException or ServiceException)
        {
            return new PathListResult { Message = e.Message };
        }
        finally
        {
            lease?.Dispose();
        }
    }

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

    /// <summary>What the report needs from the run's log: errors and, for a mirror, what it deleted.</summary>
    private sealed class LogFindings(string destination, bool collectDeleted)
    {
        private readonly string _destination = PathUtil.WithSlash(destination);

        public ErrorCollector Errors { get; } = new();
        public List<string> Deleted { get; } = [];
        public long DeletedCount { get; private set; }

        public void Add(string line)
        {
            Errors.Add(line);
            if (!collectDeleted || RobocopyOutput.ParseLine(line) is not { } l || !PathUtil.IsUnder(l.Path, _destination)) return;
            DeletedCount++;
            if (Deleted.Count < RunReport.MaxListedItems) Deleted.Add(l.Path);
        }
    }

    /// <summary>Joins the per-process Unicode logs into one UTF-8 log and removes them.</summary>
    private static void MergeLogs(string target, CopyJob job, RunRecord record, List<(string Label, string Command, string File)> parts, LogFindings found)
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
                while ((line = r.ReadLine()) != null)
                {
                    w.WriteLine(line);
                    found.Add(line);
                }
            }
            found.Errors.Complete();
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
