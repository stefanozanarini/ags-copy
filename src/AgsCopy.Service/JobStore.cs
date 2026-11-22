using System.Text.Json;
using AgsCopy.Core;
using AgsCopy.Core.Credentials;
using AgsCopy.Core.Ipc;
using AgsCopy.Core.Models;

namespace AgsCopy.Service;

/// <summary>Jobs in %ProgramData%\AGS-Copy\jobs.json. Hands out copies only.</summary>
public sealed class JobStore
{
    private readonly object _lock = new();
    private readonly List<CopyJob> _jobs;
    private readonly ILogger<JobStore> _log;

    /// <summary>Raised with the id of a job saved or deleted.</summary>
    public event Action<Guid>? Changed;

    public JobStore(ILogger<JobStore> log)
    {
        _log = log;
        _jobs = Load();
    }

    public List<CopyJob> All()
    {
        lock (_lock) return _jobs.Select(Json.Clone).ToList();
    }

    public CopyJob? Get(Guid? id)
    {
        if (id is null) return null;
        lock (_lock) return _jobs.FirstOrDefault(j => j.Id == id) is { } j ? Json.Clone(j) : null;
    }

    /// <summary>Adds or replaces a job, encrypting any new password.</summary>
    public CopyJob Save(CopyJob job, string? lang)
    {
        lock (_lock)
        {
            var existing = _jobs.FirstOrDefault(j => j.Id == job.Id);
            job = Json.Clone(job);
            for (var i = 0; i < job.Sources.Count; i++)
            {
                var source = job.Sources[i];
                source.Credential = Protect(source, JobExecutor.StoredSource(existing, source, i), lang);
                // The folder is a plain name; with a single source there is none.
                source.Folder = job.MultiSource ? source.Folder?.Trim() : null;
            }
            job.Destination.Credential = Protect(job.Destination, existing?.Destination, lang);
            job.CreatedUtc = existing?.CreatedUtc ?? DateTime.UtcNow;
            job.ModifiedUtc = DateTime.UtcNow;

            if (existing != null) _jobs[_jobs.IndexOf(existing)] = job;
            else _jobs.Add(job);
            Persist();
        }
        _log.LogInformation("Job {Name} ({Id}) saved", job.Name, job.Id);
        Changed?.Invoke(job.Id);
        return Json.Clone(job);
    }

    public bool Delete(Guid id)
    {
        bool removed;
        lock (_lock)
        {
            removed = _jobs.RemoveAll(j => j.Id == id) > 0;
            if (removed) Persist();
        }
        if (removed) Changed?.Invoke(id);
        return removed;
    }

    /// <summary>The job as clients may see it: no password or key material at all.</summary>
    public static CopyJob Sanitize(CopyJob job)
    {
        var copy = Json.Clone(job);
        foreach (var c in copy.Sources.Append(copy.Destination).Select(e => e.Credential))
        {
            if (c == null) continue;
            c.HasPassword = !string.IsNullOrEmpty(c.ProtectedPassword);
            c.HasPrivateKey = !string.IsNullOrEmpty(c.ProtectedPrivateKey);
            c.ProtectedPassword = c.ProtectedPrivateKey = null;
            c.Password = c.PrivateKey = null;
        }
        return copy;
    }

    /// <summary>
    /// Encrypts what was typed and keeps what was stored (same user, field left empty). A password is
    /// required, except with an SFTP private key, whose passphrase is optional.
    /// </summary>
    private static StoredCredential? Protect(JobEndpoint endpoint, JobEndpoint? stored, string? lang)
    {
        var c = endpoint.Credential;
        if (c == null || string.IsNullOrWhiteSpace(c.UserName) || !endpoint.IsRemote) return null;

        var result = new StoredCredential { UserName = c.UserName.Trim() };
        var kept = stored?.Credential is { } s && string.Equals(s.UserName, result.UserName, StringComparison.OrdinalIgnoreCase) ? s : null;

        if (endpoint.IsSftp && (!string.IsNullOrEmpty(c.PrivateKey) || c.HasPrivateKey))
        {
            result.ProtectedPrivateKey = !string.IsNullOrEmpty(c.PrivateKey) ? CredentialProtector.Protect(c.PrivateKey)
                : kept?.ProtectedPrivateKey ?? throw new ServiceException(Loc.Fl(lang, "err.sftpkey.missing", result.UserName));
            result.ProtectedPassword = !string.IsNullOrEmpty(c.Password) ? CredentialProtector.Protect(c.Password)
                : c.HasPassword ? kept?.ProtectedPassword : null;
            return result;
        }

        if (!string.IsNullOrEmpty(c.Password)) result.ProtectedPassword = CredentialProtector.Protect(c.Password);
        else if (kept?.ProtectedPassword is { } p) result.ProtectedPassword = p;
        else throw new ServiceException(Loc.Fl(lang, "err.password", result.UserName));
        return result;
    }

    private List<CopyJob> Load()
    {
        try
        {
            if (File.Exists(AppPaths.JobsFile))
                return JsonSerializer.Deserialize<List<CopyJob>>(File.ReadAllText(AppPaths.JobsFile), Json.Options) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            _log.LogError(e, "Cannot read {File}", AppPaths.JobsFile);
            // Keep the unreadable file for inspection instead of overwriting it with nothing.
            File.Copy(AppPaths.JobsFile, AppPaths.JobsFile + $".broken-{DateTime.Now:yyyyMMddHHmmss}", true);
        }
        return [];
    }

    private void Persist() => DataDirectory.WriteAtomic(AppPaths.JobsFile, JsonSerializer.Serialize(_jobs, Json.Indented));
}
