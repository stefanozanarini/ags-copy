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
using AgsCopy.Core.Sync;

namespace AgsCopy.Service;

public sealed class RunningJob
{
    public required RunRecord Record { get; init; }
    public required CancellationTokenSource Cancel { get; init; }
    public bool ShadowCopyFailed { get; set; }
    public List<ProgressCounter> Counters { get; } = [];

    /// <summary>Set once the run knows what it has to copy; until then the GUI shows a busy bar.</summary>
    public long TotalFiles { get; set; }
    public long TotalBytes { get; set; }
    public bool Scanning { get; set; } = true;

    /// <summary>
    /// The totals cover the whole run. Robocopy runs copy without listing first unless versions need it,
    /// and then each process lists its own part: until the last list is in, only counts are shown.
    /// </summary>
    public bool TotalsKnown { get; set; }

    public long Files
    {
        get { lock (Counters) return Counters.Sum(c => c.Files); }
    }

    public long Bytes
    {
        get { lock (Counters) return Counters.Sum(c => c.Bytes); }
    }

    // What the activity window shows: written by the run's threads, copied out under the locks.
    public const int MaxLines = 500;
    private readonly List<(ProcessActivity Info, ProgressCounter Counter)> _processes = [];
    private readonly List<(string Source, WalkProgress Progress)> _walks = [];
    private readonly List<(string Source, SyncWalkProgress Progress)> _compares = [];
    private readonly Queue<string> _lines = new();
    private string? _phase;
    private string? _phaseDetail;

    /// <summary>What the run is preparing (an <c>activity.phase.*</c> key); null once it copies.</summary>
    public void SetPhase(string? key, string? detail = null)
    {
        lock (_lines)
        {
            _phase = key;
            _phaseDetail = detail;
        }
    }

    public ProcessActivity AddProcess(string label, string source, string destination, ProgressCounter counter)
    {
        var p = new ProcessActivity { Label = label, Source = source, Destination = destination };
        lock (_processes) _processes.Add((p, counter));
        return p;
    }

    public WalkProgress AddWalk(string source)
    {
        var w = new WalkProgress();
        lock (_walks) _walks.Add((source, w));
        return w;
    }

    /// <summary>A copy-engine run reading both trees while it copies.</summary>
    public void AddCompare(string source, SyncWalkProgress progress)
    {
        lock (_walks) _compares.Add((source, progress));
    }

    public void Line(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_lines)
        {
            _lines.Enqueue(line.TrimEnd());
            while (_lines.Count > MaxLines) _lines.Dequeue();
        }
    }

    /// <summary>
    /// The file or folder the run is on: what a process copies, else what one lists, else the folder a
    /// walk reads, else what the phase is about.
    /// </summary>
    public string? Current()
    {
        lock (_processes)
        {
            foreach (var state in new[] { ProcessState.Copying, ProcessState.Listing, ProcessState.Preparing })
                foreach (var (p, _) in _processes)
                    lock (p)
                        if (p.State == state && (p.Current ?? p.Source) is { } c) return c;
        }
        lock (_walks)
        {
            if (_walks.FirstOrDefault(w => !w.Progress.Done && w.Progress.Current != null) is { Progress: { } walk }) return walk.Current;
            if (_compares.FirstOrDefault(w => !w.Progress.Done && w.Progress.Current != null) is { Progress: { } compare }) return compare.Current;
        }
        lock (_lines) return _phaseDetail;
    }

    public JobActivity Snapshot()
    {
        var a = new JobActivity
        {
            Running = true,
            StartedUtc = Record.StartUtc,
            Files = Files,
            Bytes = Bytes,
            TotalFiles = TotalFiles,
            TotalBytes = TotalBytes,
            TotalsKnown = TotalsKnown,
            Current = Current(),
        };
        lock (_lines)
        {
            a.Phase = _phase;
            a.PhaseDetail = _phaseDetail;
            a.Lines = [.. _lines];
        }
        lock (_walks)
            a.Walks = _walks.Select(w => new WalkActivity { Source = w.Source, Folders = w.Progress.Folders, Links = w.Progress.Links, Done = w.Progress.Done, Current = w.Progress.Current })
                .Concat(_compares.Select(w => new WalkActivity
                {
                    Compare = true, Source = w.Source, Folders = w.Progress.Folders, FilesToCopy = w.Progress.FilesToCopy, BytesToCopy = w.Progress.BytesToCopy,
                    Done = w.Progress.Done, Current = w.Progress.Current,
                })).ToList();
        lock (_processes)
        {
            foreach (var (p, c) in _processes)
            {
                lock (p)
                {
                    a.Processes.Add(new ProcessActivity
                    {
                        Label = p.Label, Source = p.Source, Destination = p.Destination, State = p.State, Current = p.Current,
                        Files = c.Files, Bytes = c.Bytes, Errors = p.Errors, StartedUtc = p.StartedUtc, EndedUtc = p.EndedUtc, ExitCode = p.ExitCode,
                    });
                }
            }
        }
        return a;
    }
}

/// <summary>Runs, previews and checks jobs.</summary>
public sealed class JobExecutor(JobStore store, HistoryStore history, ReportStore reports, ServiceSettings settings, ILogger<JobExecutor> log) : IHostedService
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

    private static string InterruptionsFile => Path.Combine(AppPaths.DataDir, "interrupted.json");
    private readonly object _interruptionsLock = new();
    private Interruptions _interruptions = new();
    private readonly List<Guid> _toResume = [];

    public Task StartAsync(CancellationToken ct)
    {
        ShadowCopy.CleanupLeftovers(log);
        lock (_interruptionsLock)
        {
            try
            {
                if (File.Exists(InterruptionsFile)) _interruptions = System.Text.Json.JsonSerializer.Deserialize<Interruptions>(File.ReadAllText(InterruptionsFile), Json.Options) ?? new();
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
            {
                log.LogWarning(e, "Cannot read {File}", InterruptionsFile);
            }
            foreach (var id in history.Interrupted)
            {
                if (store.Get(id) is not { Enabled: true } job) continue;
                var again = _interruptions.Interrupted(id);
                if (again) _toResume.Add(id);
                // Say in the history what happens next.
                if (history.Last(id) is { } last)
                {
                    last.Message += " " + Loc.Fl(settings.Language, again ? "run.resume.will" : "run.resume.exhausted", Interruptions.MaxAttempts);
                    history.Put(last);
                }
                log.LogWarning("Job {Name}: the last run was cut short by a crash; {What}", job.Name, again ? "it will run again" : "not running it again");
            }
            SaveInterruptions();
        }
        return Task.CompletedTask;
    }

    private void SaveInterruptions()
    {
        try
        {
            File.WriteAllText(InterruptionsFile, System.Text.Json.JsonSerializer.Serialize(_interruptions, Json.Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(e, "Cannot write {File}", InterruptionsFile);
        }
    }

    /// <summary>Starts again the runs a crash cut short; the watchdog asks at every check. Returns how many started.</summary>
    public int ResumeInterrupted()
    {
        List<Guid> ids;
        lock (_interruptionsLock)
        {
            ids = [.. _toResume];
            _toResume.Clear();
        }
        var started = 0;
        foreach (var id in ids)
        {
            if (store.Get(id) is not { Enabled: true } job) continue;
            if (TryStart(job, RunTrigger.Resumed))
            {
                started++;
                log.LogInformation("Job {Name} resumed after a crash of the service", job.Name);
            }
        }
        return started;
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

        var destinations = job.Destinations.Select(d => PathUtil.NormalizeDir(d.Path)).ToList();
        var parts = new List<(string Label, string Command, string File)>();
        var leases = new List<NetworkShare.Lease>();
        var notes = new List<string>();
        var versions = new ArchiveResult();
        var engineResults = new List<SyncResult>();
        var snapshots = new Dictionary<string, ShadowCopy>(StringComparer.OrdinalIgnoreCase);
        var runs = new List<SourceRun>();
        using var walks = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            rj.SetPhase("activity.phase.connect");
            leases.AddRange(Connect(job, null, null));

            // Every source is prepared and scanned for every destination first, so the progress bar knows
            // the whole run. With several pairs one that fails (a source unreadable, a destination offline)
            // does not stop the others. Each source has one snapshot and one look for links, whatever the destinations.
            var pairs = job.Pairs.ToList();
            var linkScans = new Dictionary<int, SourceLinks>();
            var failed = 0;
            foreach (var (i, d) in pairs)
            {
                var part = job.ForSource(i, d);
                var label = job.PairLabel(i, d);
                try
                {
                    if (!linkScans.TryGetValue(i, out var found)) linkScans[i] = found = new SourceLinks { WalkToken = walks.Token };
                    runs.Add(await PrepareAsync(part, i, d * job.Sources.Count + i, label, rj, snapshots, leases, notes, found, lang, ct));
                }
                catch (Exception e) when (pairs.Count > 1 && e is not OperationCanceledException)
                {
                    failed++;
                    notes.Add(label + e.Message);
                    log.LogError(e, "Job {Name}: {Source} → {Destination} failed", job.Name, part.Source.Path, part.Destination.Path);
                }
            }
            rj.TotalFiles = runs.Sum(r => r.TotalFiles);
            rj.TotalBytes = runs.Sum(r => r.TotalBytes);
            if (runs.LastOrDefault(r => !r.Engine) is { } lastRobocopy) lastRobocopy.Last = true;
            // Both robocopy and the copy engine learn their totals as they go; an engine-only job knows
            // them once the walk of its last run is done.
            if (runs.Count > 0 && runs.All(r => r.Engine)) runs[^1].Last = true;
            rj.TotalsKnown = false;
            rj.Scanning = false;
            rj.SetPhase(null);

            foreach (var run in runs)
            {
                ct.ThrowIfCancellationRequested();
                if (run.Engine)
                    engineResults.Add(await RunEngineAsync(run, rj, startLocal, Path.Combine(logDir, $"{stamp}-{run.Index:00}-engine.part"), parts, notes, lang, ct));
                else
                    await RunRobocopyAsync(run, rj, startLocal, logDir, stamp, parts, notes, versions, lang, ct);
            }

            // Links the walks found while the copy ran: each copied now, for every destination of its source.
            foreach (var (i, found) in linkScans)
            {
                if (found.Walk == null) continue;
                var label = job.Sources.Count > 1 ? job.PairLabel(i, 0) : "";
                LinkScan scan;
                try
                {
                    scan = await found.Walk;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
                {
                    notes.Add(label + Loc.Fl(lang, "run.links.failed", e.Message));
                    continue;
                }
                SaveLinks(job.Id, found.Src, scan);
                notes.AddRange(DescribeLinks(scan, lang).Select(n => label + n));
                var known = found.Links ?? [];
                var added = scan.Links.Where(l => !known.Any(k => k.Relative.Equals(l.Relative, StringComparison.OrdinalIgnoreCase))).ToList();
                log.LogInformation("Source {Source}: walk done, {Count} links, {Added} not known when the copy started", found.Src, scan.Links.Count, added.Count);
                if (added.Count == 0) continue;
                notes.Add(label + Loc.Fl(lang, "run.links.late", added.Count, string.Join(", ", added.Take(10).Select(l => l.Relative))));
                foreach (var run in runs.Where(r => r.Source == i && !r.Engine))
                {
                    ct.ThrowIfCancellationRequested();
                    leases.AddRange(ConnectDfsTargets(run.Part, null, run.Src, added, lang));
                    run.Segments.Clear();
                    AddSegments(run, run.RunJob, added, scan.Links);
                    rj.TotalsKnown = false;
                    await RunRobocopyAsync(run, rj, startLocal, logDir, stamp, parts, notes, versions, lang, ct);
                }
            }
            if (failed > 0) record.Outcome = Worse(record.Outcome, failed == pairs.Count ? RunOutcome.Failed : RunOutcome.Warning);

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
            walks.Cancel();
            foreach (var snapshot in snapshots.Values)
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

            // The copy engine lists what it deleted itself; robocopy's deletions come from its log.
            var found = new LogFindings(destinations, job.Mode == CopyMode.Mirror && !job.Options.KeepVersions);
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
                var engineErrors = engineResults.SelectMany(r => r.Errors).ToList();
                var errors = versions.Errors.Concat(engineErrors).Concat(found.Errors.Errors).ToList();
                record.Errors = versions.Errors.Count + engineErrors.Count + found.Errors.Count;
                record.ReportFile = reports.WriteRun(job.Id, stamp, new RunReport
                {
                    JobName = job.Name,
                    Source = string.Join(Environment.NewLine, job.Sources.Select(s => s.Path)),
                    Destination = string.Join(Environment.NewLine, job.Destinations.Select(d => d.Path)),
                    Mode = job.Mode,
                    Record = record,
                    Notes = notes,
                    Errors = errors,
                    ErrorCount = (int)record.Errors,
                    KeepVersions = job.Options.KeepVersions,
                    RetentionDays = job.Options.VersionRetentionDays,
                    VersionsFolder = runs.Count > 1
                        ? string.Join(Environment.NewLine, runs.Select(r => Versions.Root(r.Dst)).Distinct(StringComparer.OrdinalIgnoreCase))
                        : versions.Folder ?? Versions.Root(runs.FirstOrDefault()?.Dst ?? destinations[0]),
                    Archived = versions.Moved.Concat(engineResults.SelectMany(r => r.Archived)).Take(RunReport.MaxListedItems).ToList(),
                    ArchivedCount = versions.Count + engineResults.Sum(r => r.ArchivedCount),
                    VersionFoldersRemoved = versions.FoldersRemoved,
                    Deleted = versions.Deleted.Concat(found.Deleted).Concat(engineResults.SelectMany(r => r.Deleted)).Take(RunReport.MaxListedItems).ToList(),
                    DeletedCount = versions.DeletedCount + found.DeletedCount + engineResults.Sum(r => r.DeletedCount),
                    LogFile = record.LogFile,
                }, lang);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.LogWarning(e, "Cannot write the report of job {Name}", job.Name);
            }

            history.Put(record);
            reports.RebuildIndex();
            // Ended by itself: a later crash counts from zero again.
            lock (_interruptionsLock)
                if (_interruptions.Finished(job.Id)) SaveInterruptions();
            _running.TryRemove(job.Id, out _);
            _tasks.TryRemove(job.Id, out _);
            rj.Cancel.Dispose();
            log.LogInformation("Job {Name} finished: {Outcome}, exit code {Code}", job.Name, record.Outcome, record.ExitCode);
        }
    }

    /// <summary>The worse of two outcomes; Running stands for "nothing yet".</summary>
    private static RunOutcome Worse(RunOutcome a, RunOutcome b) =>
        a == RunOutcome.Running ? b : b == RunOutcome.Running ? a : (RunOutcome)Math.Max((int)a, (int)b);

    /// <summary>One robocopy copy of a source: the source itself, or a DFS link below it (<see cref="Prefix"/>).</summary>
    private sealed record Segment(CopyJob Job, string Src, string Dst, string Prefix)
    {
        public string? ListFile { get; set; }

        public void DeleteList()
        {
            if (ListFile == null) return;
            try { File.Delete(ListFile); } catch (IOException) { }
            ListFile = null;
        }
    }

    /// <summary>
    /// The links of one source, looked for once whatever the destinations. With share junctions the copy
    /// does not wait for the walk of the whole share: it starts with the links already known (those of the
    /// last walk that are still there, and the namespace's DFS links) while <see cref="Walk"/> goes on;
    /// links the walk finds besides are copied once the rest is done.
    /// </summary>
    private sealed class SourceLinks
    {
        public required CancellationToken WalkToken { get; init; }
        public string Src { get; set; } = "";
        public List<LinkedFolder>? Links { get; set; }
        public Task<LinkScan>? Walk { get; set; }
    }

    /// <summary>One source of a run, ready to copy: robocopy segments, or the copy engine.</summary>
    private sealed class SourceRun
    {
        public required CopyJob Part { get; init; }
        public required int Index { get; init; }
        public required string Label { get; init; }
        /// <summary>The part reading from the shadow copy, when there is one, and auto-tuned.</summary>
        public required CopyJob RunJob { get; set; }
        public required string Src { get; set; }
        public required string Dst { get; init; }
        public List<Segment> Segments { get; } = [];
        /// <summary>Copied by the copy engine, which reads both trees while it copies.</summary>
        public bool Engine { get; set; }
        public Func<ISourceStore>? OpenSource { get; set; }
        public Func<ITargetStore>? OpenTarget { get; set; }
        public int Threads { get; set; }
        /// <summary>Folders the engine reads at the same time.</summary>
        public int Walkers { get; set; }
        public long TotalFiles { get; set; }
        public long TotalBytes { get; set; }
        /// <summary>The last robocopy run of the job, or the last run of an engine-only job.</summary>
        public bool Last { get; set; }
        /// <summary>Which of the job's sources this run copies.</summary>
        public required int Source { get; init; }
    }

    /// <summary>
    /// Checks a source, takes its shadow copy (one per volume and run), finds the DFS links below it and
    /// scans what there is to copy.
    /// </summary>
    /// <param name="found">The links of this source: looked for with its first destination, then reused.</param>
    private async Task<SourceRun> PrepareAsync(CopyJob part, int source, int index, string label, RunningJob rj, Dictionary<string, ShadowCopy> snapshots,
        List<NetworkShare.Lease> leases, List<string> notes, SourceLinks found, string? lang, CancellationToken ct)
    {
        var src = PathUtil.NormalizeDir(part.Source.Path);
        var dst = PathUtil.NormalizeDir(part.Destination.Path);
        if (!part.Source.IsSftp && !Directory.Exists(src)) throw new ServiceException(Loc.Fl(lang, "err.nosource", src));
        // Through DFS two different names can be the same folder: never copy a source into itself.
        if ((part.Source.IsNetwork || part.Destination.IsNetwork) && PathUtil.Overlaps(DfsClient.Resolve(src), DfsClient.Resolve(dst)))
            throw new ServiceException(Loc.Fl(lang, "err.overlap.dfs", src, dst));

        if (part.Source.IsNetwork && found.Links == null)
        {
            found.Src = src;
            var exclusions = new Exclusions(part);
            var follow = part.Options.FollowsJunctionsOn(part.Source);
            var depth = part.Options.LinkSearchDepth;
            try
            {
                rj.SetPhase("activity.phase.links", src);
                if (follow && !UsesEngine(part, dst, lang, out _))
                {
                    // The walk of the whole share goes on while the copy runs.
                    found.Links = await Task.Run(() => QuickLinks(part.Id, src, exclusions, depth, ct), ct);
                    var progress = rj.AddWalk(src);
                    found.Walk = Task.Run(() =>
                    {
                        try
                        {
                            return DfsClient.FindLinks(src, exclusions, true, found.WalkToken, progress, depth);
                        }
                        finally
                        {
                            progress.Done = true;
                        }
                    }, found.WalkToken);
                    log.LogInformation("Source {Source}: copying with {Count} known links while the share is walked", src, found.Links.Count);
                }
                else
                {
                    // The copy engine reads the links as folders of one tree: it needs them all first.
                    var progress = rj.AddWalk(src);
                    var scan = await Task.Run(() => DfsClient.FindLinks(src, exclusions, follow, ct, progress, depth), ct);
                    progress.Done = true;
                    found.Links = scan.Links;
                    if (follow) SaveLinks(part.Id, src, scan);
                    notes.AddRange(DescribeLinks(scan, lang).Select(n => label + n));
                    log.LogInformation("Source {Source}: {Count} links followed, {Loops} loops left out", src, scan.Links.Count, scan.Loops.Count);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                notes.Add(label + Loc.Fl(lang, "run.links.failed", e.Message));
            }
            found.Links ??= [];
            rj.SetPhase("activity.phase.dfstargets", src);
            leases.AddRange(ConnectDfsTargets(part, null, src, found.Links, lang));
        }
        var links = found.Links ?? [];

        // From here on runJob reads the source from the snapshot, when there is one.
        var runJob = part;
        if (part.Options.UseShadowCopy && !part.Source.IsRemote)
        {
            try
            {
                var volume = ShadowCopy.VolumeOf(src);
                rj.SetPhase("activity.phase.vss", volume);
                if (!snapshots.TryGetValue(volume, out var snapshot))
                {
                    snapshot = await ShadowCopy.CreateAsync(src, $"{rj.Record.Id:N}-{snapshots.Count}", ct);
                    snapshots[volume] = snapshot;
                    log.LogInformation("Job {Name}: shadow copy {Id} of {Volume}", part.Name, snapshot.ShadowId, snapshot.VolumeRoot);
                }
                runJob = SnapshotPaths.ForSnapshot(part, snapshot.VolumeRoot, snapshot.LinkPath);
                notes.Add(label + Loc.Fl(lang, "run.vss", snapshot.VolumeRoot, runJob.Source.Path));
            }
            catch (Exception e) when (e is ShadowCopyException or ManagementException or Win32Exception or UnauthorizedAccessException or IOException)
            {
                // Copy anyway: everything but the open files still gets copied.
                rj.ShadowCopyFailed = true;
                notes.Add(label + Loc.Fl(lang, "run.vss.failed", e.Message));
                log.LogWarning(e, "Job {Name}: shadow copy failed, copying without it", part.Name);
            }
        }
        var run = new SourceRun { Part = part, Source = source, Index = index, Label = label, RunJob = runJob, Src = PathUtil.NormalizeDir(runJob.Source.Path), Dst = dst };

        if (UsesEngine(part, dst, lang, out var engineNote))
        {
            notes.Add(label + engineNote);
            run.OpenSource = SourceOpener(runJob, null, run.Src, lang, links);
            run.OpenTarget = TargetOpener(part, null, dst, lang);
            var threads = part.Options.Threads;
            if (part.Options.AutoTune) threads = AutoTune.Compute(await ResourceProbe.MeasureAsync(ct), true, 1).Threads;
            // Each worker holds its own connection: servers limit sessions, and disks seek.
            run.Threads = Math.Clamp(threads, 1, part.Source.IsSftp || part.Destination.IsSftp ? 8 : 16);
            run.Walkers = EngineWalkers(part);
            run.Engine = true;
            return run;
        }

        if (part.Options.AutoTune)
        {
            var names = TopLevelFolders(run.Src, part.Options.SkipJunctions);
            var tune = AutoTune.Compute(await ResourceProbe.MeasureAsync(ct), IsNetwork(part), names.Count + links.Count);
            run.RunJob = runJob = Json.Clone(runJob);
            runJob.Options.Processes = tune.Processes;
            runJob.Options.Threads = tune.Threads;
            notes.Add(label + DescribeTune(tune, lang));
            log.LogInformation("Job {Name}: auto-tuned to {Processes} processes x {Threads} threads", part.Name, tune.Processes, tune.Threads);
        }

        if (links.Count == 0)
        {
            run.Segments.Add(new Segment(runJob, run.Src, dst, ""));
        }
        else
        {
            run.Segments.Add(new Segment(LinkedFolders.WithoutLinks(runJob, links), run.Src, dst, ""));
            AddSegments(run, runJob, links, links);
        }
        return run;
    }

    /// <summary>One segment per link, each without the links below it (<paramref name="all"/>).</summary>
    private static void AddSegments(SourceRun run, CopyJob runJob, IEnumerable<LinkedFolder> links, IReadOnlyList<LinkedFolder> all)
    {
        foreach (var l in links)
        {
            var j = LinkedFolders.ForLink(runJob, l, all);
            run.Segments.Add(new Segment(j, l.Path, PathUtil.NormalizeDir(j.Destination.Path), l.Relative));
        }
    }

    private static string LinksFile(Guid jobId) => Path.Combine(AppPaths.DataDir, "links", jobId.ToString("N") + ".json");

    /// <summary>The links a copy can start with: those of the last walk still there, and the DFS links the namespace lists.</summary>
    private List<LinkedFolder> QuickLinks(Guid jobId, string src, Exclusions exclusions, int depth, CancellationToken ct)
    {
        var links = new List<LinkedFolder>();
        // A walk made before the depth was lowered may hold links further down.
        if (LoadLinks(jobId, src) is { } last)
            links.AddRange(LinkedFolders.Recheck(src, last.Links.Where(l => depth <= 0 || l.Relative.Split('\\').Length <= depth),
                DfsClient.EntryAt, DfsClient.FolderId, exclusions, true, ct));
        foreach (var l in DfsClient.NamespaceLinks(src, exclusions)?.Links ?? [])
            if (!links.Any(k => k.Relative.Equals(l.Relative, StringComparison.OrdinalIgnoreCase))) links.Add(l);
        return links;
    }

    private LinkCache.Entry? LoadLinks(Guid jobId, string src)
    {
        try
        {
            var file = LinksFile(jobId);
            if (!File.Exists(file)) return null;
            return System.Text.Json.JsonSerializer.Deserialize<LinkCache>(File.ReadAllText(file), Json.Options)?.Get(src);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            log.LogWarning(e, "Cannot read the links found last time for {Source}", src);
            return null;
        }
    }

    private void SaveLinks(Guid jobId, string src, LinkScan scan)
    {
        var file = LinksFile(jobId);
        try
        {
            lock (_linksLock)
            {
                var cache = File.Exists(file) ? System.Text.Json.JsonSerializer.Deserialize<LinkCache>(File.ReadAllText(file), Json.Options) ?? new() : new LinkCache();
                cache.Put(src, scan, DateTime.UtcNow);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(cache, Json.Options));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            log.LogWarning(e, "Cannot save the links found for {Source}", src);
        }
    }

    private readonly object _linksLock = new();

    private static IEnumerable<string> DescribeLinks(LinkScan scan, string? lang)
    {
        static string Names(IEnumerable<string> names) => string.Join(", ", names.Take(10)) + (names.Count() > 10 ? ", …" : "");
        var dfs = scan.Links.Where(l => l.Kind == LinkKind.Dfs).Select(l => l.Relative).ToList();
        var junctions = scan.Links.Where(l => l.Kind == LinkKind.Junction).Select(l => l.Relative).ToList();
        if (dfs.Count > 0) yield return Loc.Fl(lang, "run.dfs", dfs.Count, Names(dfs));
        if (junctions.Count > 0) yield return Loc.Fl(lang, "run.junctions", junctions.Count, Names(junctions));
        if (scan.Loops.Count > 0) yield return Loc.Fl(lang, "run.junctions.loop", scan.Loops.Count, Names(scan.Loops));
        if (scan.Truncated) yield return Loc.Fl(lang, "run.links.truncated", Fmt.Count(LinkedFolders.MaxFolders, Loc.CultureOf(lang)));
    }

    /// <summary>
    /// A share reached through DFS may sit on another server, which wants the same account: the
    /// targets of the source and of its links are connected with the source's credentials.
    /// A target that refuses is left to the DFS client.
    /// </summary>
    private List<NetworkShare.Lease> ConnectDfsTargets(CopyJob part, JobEndpoint? stored, string src, IReadOnlyList<LinkedFolder> links, string? lang)
    {
        var leases = new List<NetworkShare.Lease>();
        if (!part.Source.HasCredential) return leases;
        var password = PasswordFor(part.Source, stored, lang);
        // All at once: a server that does not answer keeps its link waiting until the network gives up.
        Parallel.ForEach(links.Where(l => l.Kind == LinkKind.Dfs).Select(l => l.Path).Prepend(src), new ParallelOptions { MaxDegreeOfParallelism = 16 }, path =>
        {
            var target = DfsClient.Resolve(path);
            if (string.Equals(target, path, StringComparison.OrdinalIgnoreCase) || PathUtil.ShareRoot(target) is not { } share) return;
            try
            {
                var lease = NetworkShare.Connect(share, part.Source.Credential!.UserName, password);
                lock (leases) leases.Add(lease);
            }
            catch (NetworkShareException e)
            {
                log.LogInformation("DFS target {Share} of {Path} not connected: {Message}", share, path, e.Message);
            }
        });
        return leases;
    }

    private async Task RunRobocopyAsync(SourceRun run, RunningJob rj, DateTime startLocal, string logDir, string stamp,
        List<(string Label, string Command, string File)> parts, List<string> notes, ArchiveResult versions, string? lang, CancellationToken ct)
    {
        var record = rj.Record;
        var part = run.Part;
        var culture = Loc.CultureOf(lang);

        // Room first: expired versions go before anything is copied, and so may the extras of a mirror.
        if (part.Options.KeepVersions)
        {
            var removed = RemoveExpiredVersions(run.Dst, part.Options.VersionRetentionDays);
            versions.FoldersRemoved += removed;
            if (removed > 0) notes.Add(run.Label + Loc.Fl(lang, "run.versions.removed", removed, part.Options.VersionRetentionDays));
        }
        var extrasFirst = part.Mode == CopyMode.Mirror && part.Options.DeleteExtrasFirst && !part.Options.KeepVersions;
        var needsList = NeedsList(part);
        var extrasBefore = versions.DeletedCount;
        var archivedBefore = versions.Count;

        // Each segment is split by its top-level folders. The segments are read all at once: a DFS link
        // whose server does not answer holds its reading until the network gives up, and it must not
        // hold the others. A link that cannot be read is left out, its copy in the destination untouched.
        rj.SetPhase("activity.phase.segments", run.Src);
        (Segment Segment, IReadOnlyList<RobocopyTask> Tasks, string? Error) Plan(Segment s)
        {
            try
            {
                return (s, ParallelPlan.Build(s.Job, s.Job.Options.Processes > 1 ? TopLevelFolders(s.Src, s.Job.Options.SkipJunctions) : []), null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The source itself was checked: robocopy says what goes wrong with it.
                return s.Prefix.Length == 0 ? (s, ParallelPlan.Build(s.Job, []), null) : (s, [], e.Message);
            }
        }
        var plans = await Task.WhenAll(run.Segments.Select(s => Task.Factory.StartNew(() => Plan(s), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default)));
        rj.SetPhase(null);
        var tasks = new List<(RobocopyTask Task, Segment Segment, string ConsoleSource)>();
        foreach (var (s, planned, error) in plans)
        {
            if (error != null)
            {
                notes.Add(run.Label + Loc.Fl(lang, "run.link.unreachable", s.Prefix, error));
                record.Outcome = Worse(record.Outcome, RunOutcome.Warning);
                log.LogWarning("Job {Name}: link {Link} not copied: {Message}", part.Name, s.Src, error);
                continue;
            }
            var consoleSource = RobocopyProcess.AsConsoleText(s.Src);
            tasks.AddRange(planned.Select(t => (t, s, consoleSource)));
        }

        var first = parts.Count;
        var activities = new ProcessActivity[tasks.Count];
        var counters = new ProgressCounter[tasks.Count];
        for (var i = 0; i < tasks.Count; i++)
        {
            parts.Add((run.Label + tasks[i].Task.Label, "", Path.Combine(logDir, $"{stamp}-{run.Index:00}-{first + i:0000}.part")));
            counters[i] = new ProgressCounter(tasks[i].ConsoleSource);
            activities[i] = rj.AddProcess(run.Label + tasks[i].Task.Label, tasks[i].Task.Source, tasks[i].Task.Destination, counters[i]);
        }

        // Each process copies as soon as its own part is ready: with versions (or extras deleted first) it
        // lists its folder with robocopy /L, moves or deletes what that shows, then copies, while the
        // other processes are still listing or already copying theirs.
        var listsLeft = needsList ? tasks.Count : 0;
        var combined = 0;
        var total = RobocopySummary.Empty;
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, run.RunJob.Options.Processes), CancellationToken = ct };
        await Parallel.ForEachAsync(Enumerable.Range(0, tasks.Count), options, async (i, token) =>
        {
            var (t, segment, _) = tasks[i];
            var job = segment.Job;
            var activity = activities[i];
            lock (activity) activity.StartedUtc = DateTime.UtcNow;
            if (needsList)
            {
                lock (activity) activity.State = ProcessState.Listing;
                var piece = new Segment(job, t.Source, t.Destination, PieceOf(segment, t));
                piece.ListFile = await ListAsync(job, t, line =>
                {
                    if (RobocopyOutput.ParseLine(line) is { } l)
                        lock (activity) activity.Current = l.Path;
                }, token);
                try
                {
                    var totals = new PreviewAggregator(t.Source, t.Destination, part.Mode);
                    foreach (var line in File.Exists(piece.ListFile) ? File.ReadLines(piece.ListFile) : []) totals.Add(line);
                    lock (activity) activity.State = ProcessState.Preparing;
                    lock (versions)
                    {
                        rj.TotalFiles += totals.Result.FilesToCopy;
                        rj.TotalBytes += totals.Result.BytesToCopy;
                        if (Directory.Exists(t.Destination))
                        {
                            if (extrasFirst) DeleteExtras(piece, versions, record, token);
                            if (part.Options.KeepVersions) ArchiveVersions(piece, run.Dst, versions, startLocal, token);
                        }
                        // The last run's lists complete the totals: the bar can show a percentage from here on.
                        if (--listsLeft == 0 && run.Last) rj.TotalsKnown = true;
                    }
                }
                finally
                {
                    piece.DeleteList();
                }
            }

            var args = RobocopyArgs.Build(job, t.Source, t.Destination, t.Pass, false, parts[first + i].File, true, t.RootExcludedDirs);
            lock (parts) parts[first + i] = parts[first + i] with { Command = "robocopy " + CommandLine.Join(args) };

            var counter = counters[i];
            lock (rj.Counters) rj.Counters.Add(counter);
            lock (activity) activity.State = ProcessState.Copying;
            var summary = new SummaryCollector();
            var code = await RobocopyProcess.RunAsync(args, line =>
            {
                counter.Add(line);
                summary.Add(line);
                rj.Line(line);
                if (RobocopyOutput.ParseLine(line) is { } l)
                    lock (activity) activity.Current = l.Path;
                else if (line?.Contains(" ERROR ", StringComparison.Ordinal) == true)
                    lock (activity) activity.Errors++;
            }, token);

            lock (activity)
            {
                activity.State = ProcessState.Done;
                activity.EndedUtc = DateTime.UtcNow;
                activity.ExitCode = code;
            }
            lock (parts)
            {
                combined |= code;
                if (summary.Summary is { } s) total = total.Add(s);
            }
        });

        if (versions.DeletedCount > extrasBefore)
            notes.Add(run.Label + Loc.Fl(lang, "run.extrasfirst", Fmt.Count(versions.DeletedCount - extrasBefore, culture)));
        var archived = versions.Count - archivedBefore;
        record.VersionsArchived += archived;
        if (archived > 0) notes.Add(run.Label + Loc.Fl(lang, "run.versions", Fmt.Count(archived, culture), Versions.RunFolder(run.Dst, startLocal)));

        record.ExitCode |= combined;
        record.Outcome = Worse(record.Outcome, RobocopyExitCode.Outcome(combined));
        record.FilesCopied += total.Files.Copied;
        record.BytesCopied += total.Bytes.Copied;
        record.FilesSkipped += total.Files.Skipped;
        record.FilesFailed += total.Files.Failed;
        record.FilesExtra += total.Files.Extras;
        record.DirsExtra += total.Dirs.Extras;
        if (tasks.Count > 1) notes.Add(run.Label + Loc.Fl(lang, "run.parallel", tasks.Count));

        if (part.Options.Compress)
        {
            var c = NtfsCompression.CompressTree(run.Dst, ct);
            notes.Add(run.Label + Loc.Fl(lang, "run.ntfs", Fmt.Count(c.Files, culture), Fmt.Bytes(c.Saved, culture)));
            foreach (var error in c.Errors) versions.Errors.Add(new CopyError(error, 0, "Compressing", ""));
        }
    }

    /// <summary>
    /// Robocopy reaches neither SFTP servers nor writes .gz files: those jobs go to the copy engine.
    /// Compression on an NTFS destination stays with robocopy, followed by NTFS compression.
    /// </summary>
    private static bool UsesEngine(CopyJob job, string dst, string? lang, out string? note)
    {
        note = null;
        if (job.Source.IsSftp || job.Destination.IsSftp)
        {
            note = Loc.Fl(lang, job.Options.Compress ? "run.engine.sftp.gz" : "run.engine.sftp");
            return true;
        }
        if (!job.Options.Compress || NtfsCompression.Supported(dst)) return false;
        note = Loc.Fl(lang, "run.engine.gz");
        return true;
    }

    /// <param name="links">Links below a share source (DFS links, junctions), each read through its own path.</param>
    private static Func<ISourceStore> SourceOpener(CopyJob job, CopyJob? stored, string src, string? lang, IReadOnlyList<LinkedFolder>? links = null)
    {
        // Like robocopy without /XJ, the engine then goes through junctions and symbolic links.
        var follow = !job.Options.SkipJunctions;
        return job.Source.IsSftp ? () => Sftp.Open(job.Source, stored?.Source, lang)
            : links is { Count: > 0 } ? () => new MountedSource(new LocalSource(src, follow, true), links.ToDictionary(l => l.Relative, l => (ISourceStore)new LocalSource(l.Path, follow, true)))
            : () => new LocalSource(src, follow, true);
    }

    private static Func<ITargetStore> TargetOpener(CopyJob job, CopyJob? stored, string dst, string? lang) =>
        job.Destination.IsSftp ? () => Sftp.Open(job.Destination, stored?.Destination, lang) : () => new LocalTarget(dst);

    /// <summary>Folders the copy engine reads at once: a share answers many at a time, an SFTP server is read one folder after the other.</summary>
    private static int EngineWalkers(CopyJob job) => job.Source.IsSftp || job.Destination.IsSftp ? 1 : 16;

    private static string IndexFile(Guid jobId, string dst) =>
        Path.Combine(AppPaths.DataDir, "index", $"{jobId:N}-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(dst.ToUpperInvariant())))[..16]}.gz");

    /// <summary>Both trees, scanned at the same time, and what a run would do with them (the preview).</summary>
    private static SyncPlan PlanEngine(CopyJob job, Func<ISourceStore> openSource, Func<ITargetStore> openTarget, string? lang, CancellationToken ct)
    {
        var exclusions = new Exclusions(job);
        var walkers = EngineWalkers(job);
        using (var s = openSource())
            if (!s.Exists()) throw new ServiceException(Loc.Fl(lang, "err.nosource", job.Source.Path));
        var source = Task.Run(() => SyncEngine.Scan(openSource, walkers, exclusions, false, ct), ct);
        var destination = Task.Run(() =>
        {
            using (var t = openTarget())
                if (!t.Exists()) return null;
            return SyncEngine.Scan(openTarget, walkers, exclusions, true, ct);
        }, ct);
        Task.WaitAll([source, destination], ct);
        return SyncPlan.Build(source.Result, destination.Result, job.Options.Compress);
    }

    private async Task<SyncResult> RunEngineAsync(SourceRun run, RunningJob rj, DateTime startLocal, string partFile,
        List<(string Label, string Command, string File)> parts, List<string> notes, string? lang, CancellationToken ct)
    {
        var record = rj.Record;
        var job = run.Part;
        var culture = Loc.CultureOf(lang);
        var counter = new ProgressCounter(run.Src);
        lock (rj.Counters) rj.Counters.Add(counter);
        parts.Add((run.Label + "engine", Loc.Fl(lang, "run.engine.cmd", run.Threads, job.Options.Compress ? Compression.Suffix : "-"), partFile));
        var activity = rj.AddProcess(run.Label + "engine", run.Src, run.Dst, counter);
        activity.State = ProcessState.Copying;
        activity.StartedUtc = DateTime.UtcNow;
        await using var writer = new StreamWriter(partFile, false, new UTF8Encoding(true));
        void Log(string line)
        {
            lock (writer) writer.WriteLine(line);
            rj.Line(line);
            // "New File<TAB>size<TAB>path": the file it has just copied.
            if (line.Split('\t') is [_, _, var path, ..])
                lock (activity) activity.Current = path;
        }

        // Room first: expired versions go before anything is copied.
        if (job.Options.KeepVersions)
        {
            using var t = run.OpenTarget!();
            var removed = SyncEngine.RemoveExpiredVersions(t, job.Options.VersionRetentionDays, Log);
            if (removed > 0) notes.Add(run.Label + Loc.Fl(lang, "run.versions.removed", removed, job.Options.VersionRetentionDays));
        }

        // What the destination held after the last runs spares reading it again, but for a full check now and then.
        var indexFile = IndexFile(job.Id, run.Dst);
        var index = DestinationIndex.Load(indexFile) ?? new DestinationIndex();
        var walk = new SyncWalk
        {
            Exclusions = new Exclusions(job),
            Threads = run.Walkers,
            Index = index,
            Verify = index.NeedsVerify(DateTime.UtcNow),
        };
        walk.Progress.OnPlanned = bytes =>
        {
            lock (rj)
            {
                rj.TotalFiles++;
                rj.TotalBytes += bytes;
            }
        };
        rj.AddCompare(run.Src, walk.Progress);

        SyncResult result;
        try
        {
            result = await SyncEngine.RunAsync(walk, run.OpenSource!, run.OpenTarget!, new SyncSettings
            {
                Mode = job.Mode,
                Compress = job.Options.Compress,
                KeepVersions = job.Options.KeepVersions,
                StartLocal = startLocal,
                Threads = run.Threads,
                Retries = job.Options.Retries,
                RetryWaitSeconds = job.Options.RetryWaitSeconds,
                ExtrasFirst = job.Options.DeleteExtrasFirst && !job.Options.KeepVersions,
            }, counter, Log, ct);
        }
        finally
        {
            // Saved even when the run stops half-way: the folders it read are up to date, the others as they were.
            try
            {
                index.Save(indexFile);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.LogWarning(e, "Cannot save the destination index {File}", indexFile);
            }
        }
        if (run.Last) rj.TotalsKnown = true;
        notes.Add(run.Label + (result.DestinationVerified
            ? Loc.Fl(lang, "run.index.verified", Fmt.Count(result.FoldersRead, culture), DestinationIndex.VerifyDays)
            : Loc.Fl(lang, "run.index.used", Fmt.Count(result.FoldersRead, culture), Fmt.Count(result.DestinationFoldersRead, culture),
                index.VerifiedUtc.ToLocalTime().ToString("g", culture), DestinationIndex.VerifyDays)));
        if (result.ArchivedCount > 0) notes.Add(run.Label + Loc.Fl(lang, "run.versions", Fmt.Count(result.ArchivedCount, culture), PathUtil.Combine(run.Dst, result.VersionsFolder!)));
        if (result.FilesCompressed > 0) notes.Add(run.Label + Loc.Fl(lang, "run.gz", Fmt.Count(result.FilesCompressed, culture)));

        record.FilesCopied += result.FilesCopied;
        record.BytesCopied += result.BytesCopied;
        record.FilesSkipped += result.FilesSkipped;
        record.FilesFailed += result.FilesFailed;
        record.FilesExtra += result.FilesExtra;
        record.DirsExtra += result.DirsExtra;
        record.VersionsArchived += result.ArchivedCount;
        // Robocopy-like exit code, so history and reports read the same: 1 copied, 2 extras, 8 failures.
        record.ExitCode |= (result.FilesCopied > 0 ? 1 : 0) | (result.FilesExtra + result.DirsExtra > 0 ? 2 : 0) | (result.FilesFailed > 0 || result.Errors.Count > 0 ? 8 : 0);
        record.Outcome = Worse(record.Outcome, result.FilesFailed > 0 && result.FilesCopied == 0 ? RunOutcome.Failed
            : result.FilesFailed > 0 || result.Errors.Count > 0 ? RunOutcome.Warning
            : RunOutcome.Success);
        return result;
    }

    private sealed class ArchiveResult
    {
        public string? Folder { get; set; }
        public long Count { get; set; }
        public List<VersionMove> Moved { get; } = [];
        public List<CopyError> Errors { get; } = [];
        public int FoldersRemoved { get; set; }

        /// <summary>Extras of a mirror deleted before the copy.</summary>
        public List<string> Deleted { get; } = [];
        public long DeletedCount { get; set; }
    }

    /// <summary>Runs robocopy /L over one process's part into a temporary list file and returns its path.</summary>
    /// <param name="onLine">Each line as robocopy prints it, to show where the listing is.</param>
    private static async Task<string> ListAsync(CopyJob job, RobocopyTask t, Action<string>? onLine, CancellationToken ct)
    {
        var listFile = Path.Combine(AppPaths.TempDir, $"list-{Guid.NewGuid():N}.log");
        var args = RobocopyArgs.Build(job, t.Source, t.Destination, t.Pass, true, listFile, onLine != null, t.RootExcludedDirs);
        await RobocopyProcess.RunAsync(args, onLine, ct);
        return listFile;
    }

    /// <summary>
    /// Only versions and extras deleted first need to know beforehand what robocopy will overwrite or
    /// delete; any other run starts copying at once and learns its totals as it goes.
    /// </summary>
    private static bool NeedsList(CopyJob part) =>
        part.Options.KeepVersions || (part.Mode == CopyMode.Mirror && part.Options.DeleteExtrasFirst);

    /// <summary>Where a process's part sits in the versions folder: the segment's place, then its top-level folder.</summary>
    private static string PieceOf(Segment s, RobocopyTask t)
    {
        if (t.Pass != RobocopyPass.Folder) return s.Prefix;
        var name = PathUtil.LastName(t.Source);
        return s.Prefix.Length == 0 ? name : s.Prefix + "\\" + name;
    }

    /// <summary>
    /// Moves what the copy is about to overwrite or (mirror) delete in the destination into
    /// this run's versions folder. The robocopy /L list tells what that is; nothing in the source moves.
    /// A DFS link keeps its versions in the source's versions folder, under its relative path.
    /// </summary>
    private static void ArchiveVersions(Segment s, string versionsRoot, ArchiveResult result, DateTime startLocal, CancellationToken ct)
    {
        result.Folder = Versions.RunFolder(versionsRoot, startLocal);
        if (s.ListFile == null || !File.Exists(s.ListFile)) return;
        foreach (var m in Versions.Plan(File.ReadLines(s.ListFile), s.Src, s.Dst, s.Job.Mode))
        {
            ct.ThrowIfCancellationRequested();
            var target = PathUtil.Combine(result.Folder, s.Prefix.Length == 0 ? m.RelativePath : s.Prefix + "\\" + m.RelativePath);
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
    }

    /// <summary>
    /// Mirror with <see cref="CopyOptions.DeleteExtrasFirst"/>: deletes what the robocopy /L list shows
    /// only in the destination, before the copy, to make room. Robocopy then finds nothing to purge.
    /// </summary>
    private static void DeleteExtras(Segment s, ArchiveResult result, RunRecord record, CancellationToken ct)
    {
        if (s.ListFile == null || !File.Exists(s.ListFile)) return;
        foreach (var m in Versions.Plan(File.ReadLines(s.ListFile), s.Src, s.Dst, CopyMode.Mirror).Where(m => m.Reason == VersionReason.Deleted).ToList())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (m.IsDirectory)
                {
                    if (!Directory.Exists(m.DestinationPath)) continue;
                    // Copies keep the read-only attribute, which would stop the delete.
                    foreach (var f in new DirectoryInfo(m.DestinationPath).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    {
                        if (f.IsReadOnly) f.IsReadOnly = false;
                        record.FilesExtra++;
                    }
                    Directory.Delete(m.DestinationPath, recursive: true);
                    record.DirsExtra++;
                }
                else
                {
                    var f = new FileInfo(m.DestinationPath);
                    if (!f.Exists) continue;
                    if (f.IsReadOnly) f.IsReadOnly = false;
                    f.Delete();
                    record.FilesExtra++;
                }
                result.DeletedCount++;
                if (result.Deleted.Count < RunReport.MaxListedItems) result.Deleted.Add(m.DestinationPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (result.Errors.Count < ErrorCollector.MaxErrors)
                    result.Errors.Add(new CopyError(m.DestinationPath, e.HResult & 0xFFFF, "Deleting Extra", e.Message));
            }
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
    /// Runs robocopy /L over the whole tree (every source, every DFS link) and reports what a real
    /// run would copy and delete.
    /// </summary>
    public async Task<PreviewResult> PreviewAsync(CopyJob job, CopyJob? stored, string? lang, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        PreviewResult? total = null;
        var leases = new List<NetworkShare.Lease>();
        try
        {
            leases.AddRange(Connect(job, stored, lang));
            foreach (var (i, d) in job.Pairs)
            {
                var part = job.ForSource(i, d);
                var storedPart = stored == null ? null : new CopyJob
                {
                    Source = StoredSource(stored, job.Sources[i], i) ?? new JobEndpoint(),
                    Destination = StoredDestination(stored, job.Destinations[d], d) ?? new JobEndpoint(),
                };
                var r = await PreviewSourceAsync(part, storedPart, leases, lang, ct);
                if (r.Error != null) r.Error = job.PairLabel(i, d) + r.Error;
                if (total == null) total = r;
                else total.Merge(r);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            total ??= new PreviewResult { Mode = job.Mode, Source = job.Source.Path };
            total.Error = e.Message;
        }
        finally
        {
            foreach (var l in leases) l.Dispose();
        }
        total!.Destination = string.Join(", ", job.Destinations.Select(x => PathUtil.NormalizeDir(x.Path)));
        total.Elapsed = sw.Elapsed;
        return total;
    }

    private async Task<PreviewResult> PreviewSourceAsync(CopyJob job, CopyJob? stored, List<NetworkShare.Lease> leases, string? lang, CancellationToken ct)
    {
        var src = PathUtil.NormalizeDir(job.Source.Path);
        var dst = PathUtil.NormalizeDir(job.Destination.Path);
        var agg = new PreviewAggregator(src, dst, job.Mode);
        var result = agg.Result;
        try
        {
            if (!job.Source.IsSftp && !Directory.Exists(src))
            {
                result.Error = Loc.Fl(lang, "err.nosource", src);
                return result;
            }
            var links = job.Source.IsNetwork
                ? (await Task.Run(() => DfsClient.FindLinks(src, new Exclusions(job), job.Options.FollowsJunctionsOn(job.Source), ct, maxDepth: job.Options.LinkSearchDepth), ct)).Links
                : [];
            leases.AddRange(ConnectDfsTargets(job, stored?.Source, src, links, lang));

            if (UsesEngine(job, dst, lang, out var engineNote))
            {
                var openSource = SourceOpener(job, stored, src, lang, links);
                var openTarget = TargetOpener(job, stored, dst, lang);
                var plan = await Task.Run(() => PlanEngine(job, openSource, openTarget, lang, ct), ct);
                using var s = openSource();
                using var t = openTarget();
                var preview = SyncPreview.From(plan, s, t, job);
                preview.CommandLine = engineNote;
                return preview;
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
            List<(CopyJob, string, string)> segments = links.Count == 0
                ? [(job, src, dst)]
                : links.Select(l => LinkedFolders.ForLink(job, l, links)).Select(j => (j, PathUtil.NormalizeDir(j.Source.Path), PathUtil.NormalizeDir(j.Destination.Path)))
                    .Prepend((LinkedFolders.WithoutLinks(job, links), src, dst)).ToList();
            var commands = new List<string>();
            foreach (var (segJob, segSrc, segDst) in segments)
            {
                // Each segment is read with its own roots, then counted into this source's preview.
                var segAgg = segSrc == src ? agg : new PreviewAggregator(segSrc, segDst, job.Mode);
                var logFile = Path.Combine(AppPaths.TempDir, $"preview-{Guid.NewGuid():N}.log");
                try
                {
                    var code = await RobocopyProcess.RunAsync(RobocopyArgs.Build(segJob, segSrc, segDst, RobocopyPass.Full, true, logFile, false), null, ct);
                    if (File.Exists(logFile))
                    {
                        foreach (var line in File.ReadLines(logFile)) segAgg.Add(line);
                    }
                    segAgg.Complete(code);
                }
                finally
                {
                    try { File.Delete(logFile); } catch (IOException) { }
                }
                if (segAgg != agg)
                {
                    var r = segAgg.Result;
                    r.Source = src;
                    r.DestinationExists = result.DestinationExists;
                    result.Merge(r);
                }
                commands.Add("robocopy " + CommandLine.Join(RobocopyArgs.Build(segJob, segSrc, segDst, RobocopyPass.Full, false, null, false)));
            }
            if (job.Mode == CopyMode.Mirror) CountExtraFolders(result, ct);
            result.CommandLine = string.Join(Environment.NewLine, commands);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            result.Error = e.Message;
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
        if (req.Endpoint.IsSftp) return ListSftp(req, stored, lang);
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
            var storedEndpoint = req.IsDestination ? StoredDestination(stored, req.Endpoint, -1) : StoredSource(stored, req.Endpoint, -1);
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

    /// <summary>
    /// Connects with the given account; with no host key recorded yet, accepts the server's and
    /// returns it, so the editor records it with the endpoint (later runs require that same key).
    /// </summary>
    private static PathTestResult TestSftp(PathTestRequest req, CopyJob? stored, string? lang)
    {
        try
        {
            var storedEndpoint = req.IsDestination ? StoredDestination(stored, req.Endpoint, -1) : StoredSource(stored, req.Endpoint, -1);
            var client = Sftp.Connect(req.Endpoint, storedEndpoint, NullIfEmpty(req.Endpoint.HostKey), lang, out var hostKey);
            using var store = new SftpStore(client, SftpLocation.TryParse(req.Endpoint.Path)!);
            if (store.Exists())
            {
                if (req.IsDestination)
                {
                    var probe = $".agscopy-{Guid.NewGuid():N}.tmp";
                    using (var w = store.Create(probe)) w.WriteByte(0);
                    store.Delete(probe, false);
                }
                else
                {
                    store.List("");
                }
                return new PathTestResult { Ok = true, HostKey = hostKey, Message = Loc.Tl(lang, req.IsDestination ? "test.ok.write" : "test.ok.read") };
            }
            return req.IsDestination
                ? new PathTestResult { Ok = true, HostKey = hostKey, Message = Loc.Tl(lang, "test.willcreate") }
                : new PathTestResult { HostKey = hostKey, Message = Loc.Tl(lang, "test.notfound") };
        }
        catch (Exception e) when (e is ServiceException || SyncEngine.IsIo(e))
        {
            return new PathTestResult { Message = e.Message };
        }
    }

    private static PathListResult ListSftp(PathListRequest req, CopyJob? stored, string? lang)
    {
        try
        {
            var storedEndpoint = req.IsDestination ? StoredDestination(stored, req.Endpoint, -1) : StoredSource(stored, req.Endpoint, -1);
            var client = Sftp.Connect(req.Endpoint, storedEndpoint, NullIfEmpty(req.Endpoint.HostKey), lang, out _);
            using var store = new SftpStore(client, SftpLocation.TryParse(req.Endpoint.Path)!);
            var rel = req.Relative.Replace('\\', '/').Trim('/');
            if (rel.Split('/').Any(p => p is "." or "..")) return new PathListResult { Message = Loc.Tl(lang, "test.notfound") };
            if (rel.Length == 0 ? !store.Exists() : !client.Exists(SftpLocation.TryParse(req.Endpoint.Path)!.Resolve(rel)))
                return new PathListResult { Ok = true, Message = Loc.Tl(lang, "test.notfound") };
            var entries = store.List(rel);
            return new PathListResult
            {
                Ok = true,
                Exists = true,
                Total = entries.Count,
                Entries = entries.Take(MaxListed).Select(e => new PathEntry { Name = e.Name, IsDirectory = e.IsDirectory, Size = e.Size, ModifiedUtc = e.ModifiedUtc }).ToList(),
            };
        }
        catch (Exception e) when (e is ServiceException || SyncEngine.IsIo(e))
        {
            return new PathListResult { Message = e.Message };
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    public PathTestResult TestPath(PathTestRequest req, CopyJob? stored, string? lang)
    {
        if (req.Endpoint.IsSftp) return TestSftp(req, stored, lang);
        var path = PathUtil.NormalizeDir(req.Endpoint.Path);
        if (!PathUtil.IsAbsolute(path))
            return new PathTestResult { Message = Loc.Tl(lang, req.IsDestination ? "val.destination" : "val.source") };

        NetworkShare.Lease? lease = null;
        try
        {
            var storedEndpoint = req.IsDestination ? StoredDestination(stored, req.Endpoint, -1) : StoredSource(stored, req.Endpoint, -1);
            if (req.Endpoint.IsNetwork && req.Endpoint.HasCredential)
                lease = NetworkShare.Connect(path, req.Endpoint.Credential!.UserName, PasswordFor(req.Endpoint, storedEndpoint, lang));

            if (Directory.Exists(path))
            {
                // Reading and writing are tried apart: a share often lets an account read a folder it cannot write.
                var account = req.Endpoint.HasCredential ? req.Endpoint.Credential!.UserName : Loc.Tl(lang, "test.account.computer");
                try
                {
                    using var e = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
                    e.MoveNext();
                }
                catch (UnauthorizedAccessException)
                {
                    return new PathTestResult { Message = Loc.Fl(lang, "test.denied.read", account, path) };
                }
                if (req.IsDestination)
                {
                    var probe = Path.Combine(path, $"agscopy-test-{Guid.NewGuid():N}.txt");
                    try
                    {
                        File.WriteAllText(probe, "AGS-Copy");
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return new PathTestResult { Message = Loc.Fl(lang, "test.denied.write", account, path) };
                    }
                    try
                    {
                        File.Delete(probe);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return new PathTestResult { Message = Loc.Fl(lang, "test.denied.delete", account, path, Path.GetFileName(probe)) };
                    }
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
    internal static List<NetworkShare.Lease> Connect(CopyJob job, CopyJob? stored, string? lang)
    {
        var leases = new List<NetworkShare.Lease>();
        try
        {
            var endpoints = job.Sources.Select((e, i) => (e, StoredSource(stored, e, i)))
                .Concat(job.Destinations.Select((e, i) => (e, StoredDestination(stored, e, i))));
            foreach (var (e, s) in endpoints)
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

    /// <summary>
    /// The saved version of a source being edited, whose secrets fill those not retyped: the one with
    /// the same path, else the one at the same place, else one with the same user.
    /// </summary>
    internal static JobEndpoint? StoredSource(CopyJob? stored, JobEndpoint e, int index) => Match(stored?.Sources, e, index);

    /// <summary>Likewise for a destination.</summary>
    internal static JobEndpoint? StoredDestination(CopyJob? stored, JobEndpoint e, int index) => Match(stored?.Destinations, e, index);

    private static JobEndpoint? Match(IReadOnlyList<JobEndpoint>? saved, JobEndpoint e, int index)
    {
        if (saved == null) return null;
        return saved.FirstOrDefault(s => string.Equals(PathUtil.NormalizeDir(s.Path), PathUtil.NormalizeDir(e.Path), StringComparison.OrdinalIgnoreCase))
            ?? (index >= 0 && index < saved.Count ? saved[index] : null)
            ?? saved.FirstOrDefault(s => e.Credential != null && string.Equals(s.Credential?.UserName, e.Credential.UserName, StringComparison.OrdinalIgnoreCase));
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
    private sealed class LogFindings(IEnumerable<string> destinations, bool collectDeleted)
    {
        private readonly List<string> _destinations = destinations.Select(PathUtil.WithSlash).ToList();

        public ErrorCollector Errors { get; } = new();
        public List<string> Deleted { get; } = [];
        public long DeletedCount { get; private set; }

        public void Add(string line)
        {
            Errors.Add(line);
            if (!collectDeleted || RobocopyOutput.ParseLine(line) is not { } l || !_destinations.Any(d => PathUtil.IsUnder(l.Path, d))) return;
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
