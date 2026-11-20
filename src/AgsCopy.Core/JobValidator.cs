using AgsCopy.Core.Models;

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

        var src = job.Source.Path;
        var dst = job.Destination.Path;
        if (!PathUtil.IsAbsolute(src)) errors.Add(T("val.source"));
        if (!PathUtil.IsAbsolute(dst)) errors.Add(T("val.destination"));
        if (PathUtil.IsAbsolute(src) && PathUtil.IsAbsolute(dst) && PathUtil.Overlaps(src, dst))
            errors.Add(T("val.overlap"));

        CheckCredential(job.Source, T("val.cred.source"));
        CheckCredential(job.Destination, T("val.cred.destination"));

        var o = job.Options;
        if (o.Threads is < 1 or > CopyOptions.MaxThreads) errors.Add(Loc.Fl(lang, "val.threads", CopyOptions.MaxThreads));
        if (o.Processes is < 1 or > CopyOptions.MaxProcesses) errors.Add(Loc.Fl(lang, "val.processes", CopyOptions.MaxProcesses));
        if (o.Retries < 0 || o.RetryWaitSeconds < 0) errors.Add(T("val.retries"));

        var s = job.Schedule;
        if (s.Kind == ScheduleKind.Weekly && s.DaysOfWeek.Count == 0) errors.Add(T("val.weekdays"));
        if (s.Kind == ScheduleKind.Monthly && s.DayOfMonth is < 1 or > 31) errors.Add(T("val.dayofmonth"));
        if (s.Kind == ScheduleKind.Interval && s.IntervalMinutes < 1) errors.Add(T("val.interval"));

        if (job.DeletesFiles && !job.DeletionConfirmed) errors.Add(T("val.confirm"));
        return errors;

        void CheckCredential(JobEndpoint e, string message)
        {
            if (e.Credential is not { } c) return;
            if (!e.IsNetwork && c.UserName.Length > 0) errors.Add(message);
            if (c.UserName.Length > 0 && string.IsNullOrEmpty(c.Password) && !c.HasPassword && string.IsNullOrEmpty(c.ProtectedPassword))
                errors.Add(message);
        }
    }
}
