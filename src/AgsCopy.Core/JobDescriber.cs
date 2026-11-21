using System.Globalization;
using AgsCopy.Core.Models;
using AgsCopy.Core.Robocopy;

namespace AgsCopy.Core;

public enum SummaryLevel
{
    Info,
    Warning,
    Danger,
}

public sealed record SummaryLine(SummaryLevel Level, string Text);

/// <summary>Plain-language description of what a job does, shown before it is saved.</summary>
public static class JobDescriber
{
    public static List<SummaryLine> Describe(CopyJob job)
    {
        var lines = new List<SummaryLine>();
        void Add(SummaryLevel l, string key, params object?[] a) => lines.Add(new SummaryLine(l, Loc.F(key, a)));

        var src = PathUtil.NormalizeDir(job.Source.Path);
        var dst = PathUtil.NormalizeDir(job.Destination.Path);
        Add(SummaryLevel.Info, "sum.what", src, dst);

        switch (job.Mode)
        {
            case CopyMode.Copy:
                Add(SummaryLevel.Info, "sum.mode.copy");
                break;
            case CopyMode.Mirror when job.Options.KeepVersions:
                Add(SummaryLevel.Danger, "sum.mode.mirror.versions", dst, src);
                break;
            case CopyMode.Mirror:
                Add(SummaryLevel.Danger, "sum.mode.mirror", dst, src);
                break;
        }
        if (job.Options.KeepVersions)
        {
            var folder = Versions.Root(dst) + "\\";
            if (job.Options.VersionRetentionDays > 0) Add(SummaryLevel.Info, "sum.versions", folder, job.Options.VersionRetentionDays);
            else Add(SummaryLevel.Info, "sum.versions.forever", folder);
        }
        Add(SummaryLevel.Info, "sum.readonly", src);
        if (job.Source.IsSftp)
        {
            // Shadow copies are a Windows matter.
        }
        else if (job.Options.UseShadowCopy)
        {
            if (job.Source.IsNetwork) Add(SummaryLevel.Warning, "sum.vss.network");
            else Add(SummaryLevel.Info, "sum.vss", src.Length >= 2 ? src[..2] : src); // the drive, D:
        }
        else if (!job.Source.IsNetwork)
        {
            Add(SummaryLevel.Info, "sum.novss");
        }

        AddEndpoint(job.Source, "sum.net.source");
        AddEndpoint(job.Destination, "sum.net.destination");

        var o = job.Options;
        var sftp = job.Source.IsSftp || job.Destination.IsSftp;
        if (sftp) Add(SummaryLevel.Warning, "sum.engine.sftp");
        if (o.Compress) Add(SummaryLevel.Info, job.Destination.IsSftp ? "sum.compress.gz" : "sum.compress", Compression.Suffix);
        if (o.ExcludeFiles.Count > 0) Add(SummaryLevel.Info, "sum.xf", string.Join(", ", o.ExcludeFiles));
        if (o.ExcludeDirectories.Count > 0) Add(SummaryLevel.Info, "sum.xd", string.Join(", ", o.ExcludeDirectories));
        if (o.ExcludeOlder) Add(SummaryLevel.Info, "sum.xo");
        if (!sftp) Add(SummaryLevel.Info, "sum.props." + o.FileProperties);
        if (o.AutoTune) Add(SummaryLevel.Info, "sum.speed.auto");
        else Add(SummaryLevel.Info, o.Processes > 1 ? "sum.speed.parallel" : "sum.speed", o.Processes, o.Threads, o.Processes * o.Threads);
        Add(SummaryLevel.Info, "sum.retries", o.Retries, o.RetryWaitSeconds);
        if (!string.IsNullOrWhiteSpace(o.ExtraArguments)) Add(SummaryLevel.Warning, "sum.extra", o.ExtraArguments.Trim());

        lines.Add(new SummaryLine(SummaryLevel.Info, Loc.T("sum.schedule") + " " + DescribeSchedule(job.Schedule)));
        if (!job.Enabled) Add(SummaryLevel.Warning, "sum.disabled");
        return lines;

        void AddEndpoint(JobEndpoint e, string key)
        {
            if (e.IsSftp)
            {
                var server = SftpLocation.TryParse(e.Path);
                Add(SummaryLevel.Info, key.Replace(".net.", ".sftp."), server?.Host ?? e.Path, e.Credential?.UserName ?? "?",
                    Loc.T(e.Credential?.UsesKey == true ? "sum.auth.key" : "sum.auth.password"), e.HostKey ?? "?");
                return;
            }
            if (!e.IsNetwork) return;
            if (e.HasCredential) Add(SummaryLevel.Info, key, e.Credential!.UserName);
            else Add(SummaryLevel.Info, key + ".none");
        }
    }

    public static string DescribeSchedule(Schedule s)
    {
        var c = Loc.Culture;
        var time = s.Start.ToString("t", c);
        return s.Kind switch
        {
            ScheduleKind.Manual => Loc.T("sched.manual"),
            ScheduleKind.Once => Loc.F("sched.once", s.Start.ToString("d", c), time),
            ScheduleKind.Daily => Loc.F("sched.daily", time),
            ScheduleKind.Weekly => Loc.F("sched.weekly", DayList(s.DaysOfWeek, c), time),
            ScheduleKind.Monthly => Loc.F("sched.monthly", s.DayOfMonth, time),
            ScheduleKind.Interval => Loc.F("sched.interval", s.IntervalMinutes, Fmt.DateTime(s.Start, c)),
            _ => "",
        };
    }

    private static string DayList(IEnumerable<DayOfWeek> days, CultureInfo c)
    {
        // Monday first, as on European calendars.
        var ordered = days.Distinct().OrderBy(d => ((int)d + 6) % 7).ToList();
        if (ordered.Count == 7) return Loc.T("sched.everyday");
        return string.Join(", ", ordered.Select(d => c.DateTimeFormat.GetDayName(d)));
    }

    /// <summary>Plain-language outcome of a preview.</summary>
    public static List<SummaryLine> DescribePreview(PreviewResult p)
    {
        var lines = new List<SummaryLine>();
        void Add(SummaryLevel l, string key, params object?[] a) => lines.Add(new SummaryLine(l, Loc.F(key, a)));

        if (p.Error != null)
        {
            Add(SummaryLevel.Danger, "prev.error", p.Error);
            return lines;
        }

        if (p.FilesToCopy == 0) Add(SummaryLevel.Info, "prev.nothing");
        else Add(SummaryLevel.Info, "prev.copy", Fmt.Count(p.FilesToCopy), Fmt.Bytes(p.BytesToCopy), p.Destination);
        if (p.FilesUnchanged > 0) Add(SummaryLevel.Info, "prev.unchanged", Fmt.Count(p.FilesUnchanged), Fmt.Bytes(p.BytesUnchanged));
        if (!p.DestinationExists) Add(SummaryLevel.Info, "prev.newdest", p.Destination);

        var extras = p.ExtraFiles + p.ExtraDirs;
        if (extras > 0)
        {
            if (p.ExtrasDeleted && p.KeepVersions)
                Add(SummaryLevel.Warning, "prev.delete.versions", Fmt.Count(p.ExtraFiles), Fmt.Bytes(p.ExtraBytes), Fmt.Count(p.ExtraDirs), Versions.Root(p.Destination));
            else if (p.ExtrasDeleted)
                Add(SummaryLevel.Danger, "prev.delete", Fmt.Count(p.ExtraFiles), Fmt.Bytes(p.ExtraBytes), Fmt.Count(p.ExtraDirs), p.Destination);
            else
                Add(SummaryLevel.Info, "prev.extrakept", Fmt.Count(p.ExtraFiles), Fmt.Count(p.ExtraDirs));
        }
        else if (p.Mode == CopyMode.Mirror)
        {
            Add(SummaryLevel.Info, "prev.nodelete");
        }

        if (p.ExitCode >= RobocopyExitCode.CopyFailed)
            Add(SummaryLevel.Warning, "prev.exitcode", p.ExitCode);
        return lines;
    }
}
