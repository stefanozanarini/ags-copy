using System.Text.Json;
using AgsCopy.Core;
using AgsCopy.Core.Models;

namespace AgsCopy.Service;

/// <summary>Run records per job (history\&lt;id&gt;.json, newest first) and their logs.</summary>
public sealed class HistoryStore
{
    public const int MaxRunsPerJob = 200;

    private readonly object _lock = new();
    private readonly Dictionary<Guid, List<RunRecord>> _cache = [];
    private readonly ILogger<HistoryStore> _log;

    public HistoryStore(ILogger<HistoryStore> log)
    {
        _log = log;
        // Runs still marked as running were cut short by a stop or a crash.
        foreach (var file in Directory.EnumerateFiles(AppPaths.HistoryDir, "*.json"))
        {
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var id)) continue;
            var runs = Read(id);
            var stale = runs.Where(r => r.Outcome == RunOutcome.Running).ToList();
            if (stale.Count == 0) continue;
            foreach (var r in stale)
            {
                r.Outcome = RunOutcome.Failed;
                r.EndUtc ??= File.GetLastWriteTimeUtc(file);
                r.Message = "Interrupted: the service stopped during the run.";
            }
            Write(id, runs);
        }
    }

    public List<RunRecord> List(Guid jobId, int max = MaxRunsPerJob)
    {
        lock (_lock) return Read(jobId).Take(max).Select(Json.Clone).ToList();
    }

    public RunRecord? Last(Guid jobId)
    {
        lock (_lock) return Read(jobId).FirstOrDefault() is { } r ? Json.Clone(r) : null;
    }

    public RunRecord? Find(Guid jobId, Guid runId)
    {
        lock (_lock) return Read(jobId).FirstOrDefault(r => r.Id == runId) is { } r ? Json.Clone(r) : null;
    }

    /// <summary>Adds or updates a run; trims old runs and their logs.</summary>
    public void Put(RunRecord record)
    {
        lock (_lock)
        {
            var runs = Read(record.JobId);
            var i = runs.FindIndex(r => r.Id == record.Id);
            if (i >= 0) runs[i] = Json.Clone(record);
            else runs.Insert(0, Json.Clone(record));

            while (runs.Count > MaxRunsPerJob)
            {
                var old = runs[^1];
                runs.RemoveAt(runs.Count - 1);
                if (old.LogFile != null) TryDelete(Path.Combine(AppPaths.JobLogDir(old.JobId), old.LogFile));
            }
            Write(record.JobId, runs);
        }
    }

    public void DeleteJob(Guid jobId)
    {
        lock (_lock)
        {
            _cache.Remove(jobId);
            TryDelete(FileOf(jobId));
            try
            {
                if (Directory.Exists(AppPaths.JobLogDir(jobId))) Directory.Delete(AppPaths.JobLogDir(jobId), true);
            }
            catch (IOException e)
            {
                _log.LogWarning(e, "Cannot delete the logs of job {Id}", jobId);
            }
        }
    }

    private static string FileOf(Guid jobId) => Path.Combine(AppPaths.HistoryDir, jobId.ToString("N") + ".json");

    private List<RunRecord> Read(Guid jobId)
    {
        if (_cache.TryGetValue(jobId, out var cached)) return cached;
        List<RunRecord> runs = [];
        try
        {
            if (File.Exists(FileOf(jobId)))
                runs = JsonSerializer.Deserialize<List<RunRecord>>(File.ReadAllText(FileOf(jobId)), Json.Options) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            _log.LogError(e, "Cannot read the history of job {Id}", jobId);
        }
        _cache[jobId] = runs;
        return runs;
    }

    private void Write(Guid jobId, List<RunRecord> runs)
    {
        _cache[jobId] = runs;
        DataDirectory.WriteAtomic(FileOf(jobId), JsonSerializer.Serialize(runs, Json.Options));
    }

    private void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException e) { _log.LogWarning(e, "Cannot delete {Path}", path); }
    }
}
