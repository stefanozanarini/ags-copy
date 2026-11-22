using AgsCopy.Core.Models;
using AgsCopy.Core.Robocopy;

namespace AgsCopy.Core;

public static class JobValidator
{
    /// <summary>Problems that prevent saving the job, in the given language.</summary>
    public static List<string> Validate(CopyJob job, string? lang = null)
    {
        lang ??= Loc.Language;
        var errors = new List<string>();
        string T(string key) => Loc.Tl(lang, key);

        if (string.IsNullOrWhiteSpace(job.Name)) errors.Add(T("val.name"));

        var dst = job.Destination.Path;
        if (!PathUtil.IsAbsolute(dst)) errors.Add(T("val.destination"));
        var sources = job.Sources;
        for (var i = 0; i < sources.Count; i++)
        {
            var src = sources[i].Path;
            if (!PathUtil.IsAbsolute(src))
            {
                errors.Add(job.MultiSource ? Loc.Fl(lang, "val.source.n", i + 1) : T("val.source"));
                continue;
            }
            if (PathUtil.IsAbsolute(dst) && PathUtil.Overlaps(src, dst))
                errors.Add(job.MultiSource ? Loc.Fl(lang, "val.overlap.n", src) : T("val.overlap"));
            // One source inside another would be copied twice, and two mirrors would fight over it.
            for (var j = 0; j < i; j++)
                if (PathUtil.IsAbsolute(sources[j].Path) && PathUtil.Overlaps(src, sources[j].Path))
                    errors.Add(Loc.Fl(lang, "val.sources.overlap", sources[j].Path, src));
            CheckCredential(sources[i], T("val.cred.source"));
            CheckSftp(sources[i], "source");
        }
        if (job.MultiSource) CheckFolders();

        CheckCredential(job.Destination, T("val.cred.destination"));
        CheckSftp(job.Destination, "destination");

        var o = job.Options;
        if (o.Threads is < 1 or > CopyOptions.MaxThreads) errors.Add(Loc.Fl(lang, "val.threads", CopyOptions.MaxThreads));
        if (o.Processes is < 1 or > CopyOptions.MaxProcesses) errors.Add(Loc.Fl(lang, "val.processes", CopyOptions.MaxProcesses));
        if (o.Retries < 0 || o.RetryWaitSeconds < 0) errors.Add(T("val.retries"));
        if ((sources.Any(x => x.IsSftp) || job.Destination.IsSftp) && !string.IsNullOrWhiteSpace(o.ExtraArguments)) errors.Add(T("val.sftpargs"));
        if (RobocopyArgs.SourceWritingSwitches(o.ExtraArguments) is { Count: > 0 } forbidden)
            errors.Add(Loc.Fl(lang, "val.sourcewrite", string.Join(' ', forbidden)));

        if (o.KeepVersions && o.VersionRetentionDays is < 0 or > Versions.MaxRetentionDays)
            errors.Add(Loc.Fl(lang, "val.retention", Versions.MaxRetentionDays));

        var s = job.Schedule;
        if (s.Kind == ScheduleKind.Weekly && s.DaysOfWeek.Count == 0) errors.Add(T("val.weekdays"));
        if (s.Kind == ScheduleKind.Monthly && s.DayOfMonth is < 1 or > 31) errors.Add(T("val.dayofmonth"));
        if (s.Kind == ScheduleKind.Interval && s.IntervalMinutes < 1) errors.Add(T("val.interval"));

        if (job.DeletesFiles && !job.DeletionConfirmed) errors.Add(T("val.confirm"));
        return errors;

        void CheckCredential(JobEndpoint e, string message)
        {
            if (e.Credential is not { } c) return;
            if (!e.IsRemote && c.UserName.Length > 0) errors.Add(message);
            // An SFTP private key needs no password (it may have a passphrase).
            if (c.UserName.Length > 0 && !(e.IsSftp && c.UsesKey)
                && string.IsNullOrEmpty(c.Password) && !c.HasPassword && string.IsNullOrEmpty(c.ProtectedPassword))
                errors.Add(message);
        }

        // Each source in a folder of its own: a plain name, not the versions folder, not used twice.
        void CheckFolders()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in sources)
            {
                var f = e.Folder?.Trim() ?? "";
                if (!IsFolderName(f)) errors.Add(Loc.Fl(lang, "val.folder", e.Path, f));
                else if (f.Equals(Versions.FolderName, StringComparison.OrdinalIgnoreCase)) errors.Add(Loc.Fl(lang, "val.folder.versions", Versions.FolderName));
                else if (!seen.Add(f)) errors.Add(Loc.Fl(lang, "val.folder.twice", f));
            }
        }

        void CheckSftp(JobEndpoint e, string side)
        {
            if (!e.IsSftp) return;
            if (!e.HasCredential) errors.Add(T("val.sftp.user." + side));
            else if (string.IsNullOrEmpty(e.HostKey)) errors.Add(T("val.hostkey." + side));
        }
    }

    private static readonly char[] BadNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>A single folder name valid on Windows and on SFTP servers.</summary>
    public static bool IsFolderName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name == name.Trim()
        && name.IndexOfAny(BadNameChars) < 0
        && !name.Any(char.IsControl)
        && name is not ("." or "..")
        && !name.EndsWith('.');
}
