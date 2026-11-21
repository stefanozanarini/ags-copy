using System.Net;
using System.Text;
using AgsCopy.Core.Models;
using AgsCopy.Core.Robocopy;

namespace AgsCopy.Core.Reports;

/// <summary>Everything a run report shows.</summary>
public sealed class RunReport
{
    public const int MaxListedItems = 1000;

    public string Computer { get; set; } = Environment.MachineName;
    public string JobName { get; set; } = "";
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public CopyMode Mode { get; set; }
    public RunRecord Record { get; set; } = new();
    public List<string> Notes { get; set; } = [];

    public List<CopyError> Errors { get; set; } = [];
    public int ErrorCount { get; set; }

    public bool KeepVersions { get; set; }
    public int RetentionDays { get; set; }
    public string? VersionsFolder { get; set; }
    public List<VersionMove> Archived { get; set; } = [];
    public long ArchivedCount { get; set; }
    public int VersionFoldersRemoved { get; set; }

    /// <summary>Items a mirror deleted from the destination (when versions are off).</summary>
    public List<string> Deleted { get; set; } = [];
    public long DeletedCount { get; set; }

    public string? LogFile { get; set; }
}

/// <summary>One job in the index page.</summary>
public sealed class IndexEntry
{
    public string JobName { get; set; } = "";
    public CopyMode Mode { get; set; }
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Schedule { get; set; } = "";
    public bool Enabled { get; set; }
    /// <summary>Folder of the job's run reports, relative to the index page.</summary>
    public string Folder { get; set; } = "";
    /// <summary>Newest first.</summary>
    public List<RunRecord> Runs { get; set; } = [];
}

/// <summary>Self-contained HTML pages (inline CSS, no script), readable offline or attached.</summary>
public static class ReportBuilder
{
    public const string IndexFile = "index.html";
    public const int IndexDots = 30;
    public const int IndexRows = 10;

    public static string Run(RunReport rep, string? lang)
    {
        string T(string k) => Loc.Tl(lang, k);
        string F(string k, params object?[] a) => Loc.Fl(lang, k, a);
        var c = Loc.CultureOf(lang);
        var r = rep.Record;
        var sb = new StringBuilder();
        Head(sb, $"{rep.JobName} — {Fmt.DateTime(r.StartUtc.ToLocalTime(), c)}");

        sb.Append("<header><div class=\"brand\">AGS-Copy</div><h1>").Append(H(rep.JobName)).Append("</h1>")
          .Append(Badge(r.Outcome, lang))
          .Append("<p class=\"muted\">").Append(H(F("rep.run.sub", Fmt.DateTime(r.StartUtc.ToLocalTime(), c), rep.Computer))).Append("</p></header>");

        var seconds = Math.Max(1, r.Duration.TotalSeconds);
        sb.Append("<section class=\"grid\">");
        Fact(sb, T("rep.copied"), Fmt.Count(r.FilesCopied, c) + " " + T("rep.files"), Fmt.Bytes(r.BytesCopied, c));
        Fact(sb, T("rep.skipped"), Fmt.Count(r.FilesSkipped, c) + " " + T("rep.files"), T("rep.skipped.sub"));
        Fact(sb, T("rep.errors"), Fmt.Count(rep.ErrorCount, c), r.FilesFailed > 0 ? F("rep.failedfiles", Fmt.Count(r.FilesFailed, c)) : "", rep.ErrorCount > 0 ? "bad" : null);
        Fact(sb, T("rep.duration"), Fmt.Duration(r.Duration), Fmt.Bytes((long)(r.BytesCopied / seconds), c) + "/s");
        if (rep.KeepVersions) Fact(sb, T("rep.archived"), Fmt.Count(rep.ArchivedCount, c), F("rep.retention", rep.RetentionDays));
        if (rep.Mode == CopyMode.Mirror && !rep.KeepVersions) Fact(sb, T("rep.deleted"), Fmt.Count(rep.DeletedCount, c), T("rep.deleted.sub"), rep.DeletedCount > 0 ? "warn" : null);
        sb.Append("</section>");

        sb.Append("<section><table class=\"kv\">");
        Row(sb, T("col.source"), rep.Source, mono: true);
        Row(sb, T("col.destination"), rep.Destination, mono: true);
        Row(sb, T("col.mode"), T("mode." + rep.Mode));
        Row(sb, T("history.trigger"), T("trigger." + r.Trigger));
        Row(sb, T("rep.start"), Fmt.DateTime(r.StartUtc.ToLocalTime(), c) + (r.EndUtc is { } end ? " → " + end.ToLocalTime().ToString("T", c) : ""));
        Row(sb, T("history.code"), r.ExitCode.ToString(c));
        if (rep.KeepVersions && rep.VersionsFolder != null) Row(sb, T("rep.versionsfolder"), rep.VersionsFolder, mono: true);
        if (rep.LogFile != null) Row(sb, T("rep.log"), rep.LogFile, mono: true);
        sb.Append("</table>");
        if (rep.Notes.Count > 0)
        {
            sb.Append("<ul class=\"notes\">");
            foreach (var n in rep.Notes) sb.Append("<li>").Append(H(n)).Append("</li>");
            sb.Append("</ul>");
        }
        sb.Append("</section>");

        // Errors
        sb.Append("<section><h2>").Append(H(T("rep.errors"))).Append(" (").Append(Fmt.Count(rep.ErrorCount, c)).Append(")</h2>");
        if (rep.Errors.Count == 0)
        {
            sb.Append("<p class=\"ok\">").Append(H(T("rep.noerrors"))).Append("</p>");
        }
        else
        {
            sb.Append("<table class=\"list\"><thead><tr><th>").Append(H(T("preview.path"))).Append("</th><th>").Append(H(T("history.code")))
              .Append("</th><th>").Append(H(T("rep.message"))).Append("</th></tr></thead><tbody>");
            foreach (var e in rep.Errors.Take(RunReport.MaxListedItems))
            {
                sb.Append("<tr><td class=\"path\">").Append(H(e.Path)).Append("<div class=\"muted\">").Append(H(e.Action)).Append("</div></td><td>")
                  .Append(e.Code.ToString(c)).Append("</td><td>").Append(H(ErrorHint(e.Code, lang) ?? e.Message));
                if (ErrorHint(e.Code, lang) != null && e.Message.Length > 0) sb.Append("<div class=\"muted\">").Append(H(e.Message)).Append("</div>");
                sb.Append("</td></tr>");
            }
            sb.Append("</tbody></table>");
            More(sb, rep.ErrorCount, Math.Min(rep.Errors.Count, RunReport.MaxListedItems), lang);
        }
        sb.Append("</section>");

        if (rep.KeepVersions && rep.ArchivedCount > 0)
        {
            sb.Append("<section><h2>").Append(H(T("rep.archived"))).Append(" (").Append(Fmt.Count(rep.ArchivedCount, c)).Append(")</h2>")
              .Append("<p class=\"muted\">").Append(H(F("rep.archived.sub", rep.VersionsFolder ?? "", rep.RetentionDays))).Append("</p>")
              .Append("<table class=\"list\"><thead><tr><th>").Append(H(T("preview.path"))).Append("</th><th>").Append(H(T("rep.reason"))).Append("</th></tr></thead><tbody>");
            foreach (var a in rep.Archived.Take(RunReport.MaxListedItems))
                sb.Append("<tr><td class=\"path\">").Append(H(a.RelativePath + (a.IsDirectory ? "\\" : ""))).Append("</td><td>")
                  .Append(H(T("rep.reason." + a.Reason))).Append("</td></tr>");
            sb.Append("</tbody></table>");
            More(sb, rep.ArchivedCount, Math.Min(rep.Archived.Count, RunReport.MaxListedItems), lang);
            sb.Append("</section>");
        }

        if (rep.DeletedCount > 0)
        {
            sb.Append("<section><h2>").Append(H(T("rep.deleted"))).Append(" (").Append(Fmt.Count(rep.DeletedCount, c)).Append(")</h2><table class=\"list\"><tbody>");
            foreach (var d in rep.Deleted.Take(RunReport.MaxListedItems)) sb.Append("<tr><td class=\"path\">").Append(H(d)).Append("</td></tr>");
            sb.Append("</tbody></table>");
            More(sb, rep.DeletedCount, Math.Min(rep.Deleted.Count, RunReport.MaxListedItems), lang);
            sb.Append("</section>");
        }

        Foot(sb, lang);
        return sb.ToString();
    }

    /// <param name="runLink">Link to a run's report, relative to the index, or null when it has none.</param>
    public static string Index(IReadOnlyList<IndexEntry> jobs, Func<IndexEntry, RunRecord, string?> runLink, string? lang)
    {
        string T(string k) => Loc.Tl(lang, k);
        var c = Loc.CultureOf(lang);
        var sb = new StringBuilder();
        Head(sb, "AGS-Copy — " + T("rep.index.title"));
        sb.Append("<header><div class=\"brand\">AGS-Copy</div><h1>").Append(H(T("rep.index.title"))).Append("</h1><p class=\"muted\">")
          .Append(H(Loc.Fl(lang, "rep.index.sub", Environment.MachineName, Fmt.DateTime(DateTime.Now, c), jobs.Count))).Append("</p></header>");

        if (jobs.Count == 0) sb.Append("<p>").Append(H(T("main.empty.title"))).Append("</p>");

        foreach (var j in jobs)
        {
            var last = j.Runs.FirstOrDefault(r => r.Outcome != RunOutcome.Running);
            var lastOk = j.Runs.FirstOrDefault(r => r.Outcome == RunOutcome.Success);
            sb.Append("<section class=\"job\"><div class=\"jobhead\"><h2>").Append(H(j.JobName)).Append("</h2>");
            if (last != null) sb.Append(Badge(last.Outcome, lang));
            if (!j.Enabled) sb.Append("<span class=\"badge off\">").Append(H(T("main.disabled"))).Append("</span>");
            sb.Append("</div><p class=\"muted path\">").Append(H(j.Source)).Append(" → ").Append(H(j.Destination)).Append(" · ")
              .Append(H(T("mode." + j.Mode))).Append(" · ").Append(H(j.Schedule)).Append("</p>");

            sb.Append("<p>").Append(H(T("rep.lastok"))).Append(": <b>")
              .Append(H(lastOk != null ? Fmt.DateTime(lastOk.StartUtc.ToLocalTime(), c) : T("rep.never"))).Append("</b></p>");

            // Oldest to newest, left to right.
            sb.Append("<div class=\"dots\">");
            foreach (var r in j.Runs.Take(IndexDots).Reverse())
            {
                var title = H(Fmt.DateTime(r.StartUtc.ToLocalTime(), c) + " — " + T("outcome." + r.Outcome));
                var link = runLink(j, r);
                sb.Append(link != null ? $"<a href=\"{H(link)}\" " : "<span ").Append($"class=\"dot {r.Outcome.ToString().ToLowerInvariant()}\" title=\"{title}\">")
                  .Append(link != null ? "</a>" : "</span>");
            }
            sb.Append("</div>");

            if (j.Runs.Count > 0)
            {
                sb.Append("<table class=\"list\"><thead><tr>");
                foreach (var k in new[] { "history.start", "history.outcome", "history.duration", "history.files", "history.bytes", "rep.errors", "rep.archived" })
                    sb.Append("<th>").Append(H(T(k))).Append("</th>");
                sb.Append("</tr></thead><tbody>");
                foreach (var r in j.Runs.Take(IndexRows))
                {
                    var link = runLink(j, r);
                    var when = H(Fmt.DateTime(r.StartUtc.ToLocalTime(), c));
                    sb.Append("<tr><td>").Append(link != null ? $"<a href=\"{H(link)}\">{when}</a>" : when).Append("</td><td>")
                      .Append(Badge(r.Outcome, lang)).Append("</td><td>").Append(H(Fmt.Duration(r.Duration))).Append("</td><td>")
                      .Append(Fmt.Count(r.FilesCopied, c)).Append("</td><td>").Append(H(Fmt.Bytes(r.BytesCopied, c))).Append("</td><td>")
                      .Append(r.Errors > 0 ? "<b class=\"badtext\">" + Fmt.Count(r.Errors, c) + "</b>" : "0").Append("</td><td>")
                      .Append(Fmt.Count(r.VersionsArchived, c)).Append("</td></tr>");
                }
                sb.Append("</tbody></table>");
            }
            sb.Append("</section>");
        }

        Foot(sb, lang);
        return sb.ToString();
    }

    /// <summary>Plain words for the Windows errors robocopy meets most.</summary>
    public static string? ErrorHint(int code, string? lang) =>
        code is 2 or 3 or 5 or 32 or 33 or 53 or 64 or 67 or 112 or 121 or 1219 or 1326
            ? Loc.Tl(lang, "rep.err." + code)
            : null;

    private static void More(StringBuilder sb, long total, int shown, string? lang)
    {
        if (total > shown) sb.Append("<p class=\"muted\">").Append(H(Loc.Fl(lang, "rep.more", total - shown))).Append("</p>");
    }

    private static string Badge(RunOutcome o, string? lang) =>
        $"<span class=\"badge {o.ToString().ToLowerInvariant()}\">{H(Loc.Tl(lang, "outcome." + o))}</span>";

    private static void Fact(StringBuilder sb, string label, string value, string sub, string? tone = null) =>
        sb.Append("<div class=\"fact").Append(tone != null ? " " + tone : "").Append("\"><div class=\"label\">").Append(H(label))
          .Append("</div><div class=\"value\">").Append(H(value)).Append("</div><div class=\"muted\">").Append(H(sub)).Append("</div></div>");

    private static void Row(StringBuilder sb, string k, string v, bool mono = false) =>
        sb.Append("<tr><th>").Append(H(k)).Append(mono ? "</th><td class=\"path\">" : "</th><td>").Append(H(v)).Append("</td></tr>");

    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static void Head(StringBuilder sb, string title) => sb.Append("<!doctype html><html><head><meta charset=\"utf-8\">")
        .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>").Append(H(title)).Append("</title><style>").Append(Css).Append("</style></head><body><main>");

    private static void Foot(StringBuilder sb, string? lang) =>
        sb.Append("<footer>").Append(H(Loc.Fl(lang, "rep.footer", Fmt.DateTime(DateTime.Now, Loc.CultureOf(lang))))).Append("</footer></main></body></html>");

    private const string Css = """
        :root{--fg:#1f2328;--muted:#6b7280;--line:#e5e7eb;--bg:#fff;--surface:#f6f8fa;--accent:#2a2e35;--ok:#2e7d32;--warn:#b26a00;--bad:#c62828}
        *{box-sizing:border-box}body{margin:0;background:var(--surface);color:var(--fg);font:14px/1.45 "Segoe UI",system-ui,sans-serif}
        main{max-width:1100px;margin:0 auto;padding:24px 16px}header{margin-bottom:16px}
        .brand{color:var(--accent);font-weight:600;font-size:12px;letter-spacing:.08em;text-transform:uppercase}
        h1{font-size:24px;margin:2px 12px 4px 0;display:inline-block}h2{font-size:17px;margin:0 8px 8px 0;display:inline-block}
        section{background:var(--bg);border:1px solid var(--line);border-radius:8px;padding:16px;margin-bottom:14px}
        .grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:12px}
        .fact{border-left:4px solid var(--accent);padding:4px 10px}.fact.bad{border-color:var(--bad)}.fact.warn{border-color:var(--warn)}
        .label{color:var(--muted);font-size:12px;text-transform:uppercase;letter-spacing:.04em}.value{font-size:22px;font-weight:600}
        .muted{color:var(--muted);font-size:12.5px}.path{word-break:break-all;font-family:Consolas,"Cascadia Mono",monospace;font-size:12.5px}
        table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:6px 8px;border-bottom:1px solid var(--line);vertical-align:top}
        thead th{font-size:12px;color:var(--muted);text-transform:uppercase}.kv th{width:190px;color:var(--muted);font-weight:500}
        .badge{display:inline-block;padding:2px 9px;border-radius:999px;font-size:12px;font-weight:600;color:#fff;background:var(--muted);vertical-align:middle}
        .badge.success{background:var(--ok)}.badge.warning,.badge.cancelled{background:var(--warn)}.badge.failed{background:var(--bad)}.badge.running{background:var(--accent)}
        .badge.off{background:#9ca3af;margin-left:6px}.ok{color:var(--ok);font-weight:600}.badtext{color:var(--bad)}
        .dots{display:flex;flex-wrap:wrap;gap:4px;margin:8px 0 12px}.dot{width:14px;height:14px;border-radius:3px;background:#d1d5db;display:inline-block}
        .dot.success{background:var(--ok)}.dot.warning,.dot.cancelled{background:var(--warn)}.dot.failed{background:var(--bad)}.dot.running{background:var(--accent)}
        .notes{margin:10px 0 0;padding-left:18px;color:var(--muted)}a{color:var(--accent)}footer{color:var(--muted);font-size:12px;text-align:center;margin-top:20px}
        @media print{body{background:#fff}section{break-inside:avoid}}
        """;
}
