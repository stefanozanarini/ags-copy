using AgsCopy.Core;
using AgsCopy.Core.Models;
using AgsCopy.Core.Reports;
using AgsCopy.Core.Robocopy;

namespace AgsCopy.Core.Tests;

public class VersionsTests
{
    private static readonly string[] ListLines =
    [
        "\t                   3\tC:\\Data\\",
        "\t    Newer     \t\t    2000\tC:\\Data\\report.xlsx",
        "\t    New File  \t\t    1000\tC:\\Data\\sub\\new.docx",
        "\t  *EXTRA File \t\t     300\tE:\\Copy\\old.txt",
        "\t*EXTRA Dir        -1\tE:\\Copy\\Archive\\",
    ];

    [Fact]
    public void Mirror_moves_overwrite_candidates_and_extras()
    {
        var plan = Versions.Plan(ListLines, @"C:\Data", @"E:\Copy", CopyMode.Mirror).ToList();
        Assert.Equal(4, plan.Count);
        Assert.Contains(plan, m => m is { DestinationPath: @"E:\Copy\report.xlsx", RelativePath: "report.xlsx", Reason: VersionReason.Overwritten });
        Assert.Contains(plan, m => m is { DestinationPath: @"E:\Copy\sub\new.docx", Reason: VersionReason.Overwritten });
        Assert.Contains(plan, m => m is { DestinationPath: @"E:\Copy\old.txt", Reason: VersionReason.Deleted, IsDirectory: false });
        Assert.Contains(plan, m => m is { DestinationPath: @"E:\Copy\Archive", RelativePath: "Archive", Reason: VersionReason.Deleted, IsDirectory: true });
    }

    [Fact]
    public void Copy_mode_never_touches_extras()
    {
        var plan = Versions.Plan(ListLines, @"C:\Data", @"E:\Copy", CopyMode.Copy).ToList();
        Assert.All(plan, m => Assert.Equal(VersionReason.Overwritten, m.Reason));
        Assert.Equal(2, plan.Count);
    }

    [Fact]
    public void The_versions_folder_itself_is_never_archived()
    {
        var plan = Versions.Plan(["\t*EXTRA Dir        -1\tE:\\Copy\\_AGS-Copy-Versions\\"], @"C:\Data", @"E:\Copy", CopyMode.Mirror);
        Assert.Empty(plan);
    }

    [Fact]
    public void Expired_folders_by_name()
    {
        var now = new DateTime(2026, 11, 20, 12, 0, 0);
        var names = new[] { "2026-10-01_220000", "2026-11-19_220000", "not-a-date", "2026-10-21_115959" };
        Assert.Equal(["2026-10-01_220000", "2026-10-21_115959"], Versions.Expired(names, now, 30));
        Assert.Empty(Versions.Expired(names, now, 0));
    }

    [Fact]
    public void Run_folder_and_exclusion()
    {
        Assert.Equal(@"\\nas\b\_AGS-Copy-Versions\2026-11-20_220000", Versions.RunFolder(@"\\nas\b\", new DateTime(2026, 11, 20, 22, 0, 0)));
        foreach (var keep in new[] { true, false })
        {
            var job = new CopyJob { Mode = CopyMode.Mirror, Options = new CopyOptions { KeepVersions = keep } };
            var a = RobocopyArgs.Build(job, @"C:\a", @"D:\b", RobocopyPass.Full, false, null, false);
            Assert.Contains(Versions.FolderName, a.Skip(a.IndexOf("/XD")));
        }
    }

    [Fact]
    public void Retention_is_validated()
    {
        var job = new CopyJob { Name = "x", Source = new JobEndpoint { Path = @"C:\a" }, Destination = new JobEndpoint { Path = @"D:\b" } };
        job.Options.KeepVersions = true;
        job.Options.VersionRetentionDays = -1;
        Assert.Single(JobValidator.Validate(job, "en"));
        job.Options.VersionRetentionDays = 0;
        Assert.Empty(JobValidator.Validate(job, "en"));
    }
}

public class ErrorCollectorTests
{
    [Fact]
    public void Collects_errors_once_per_path_with_their_message()
    {
        var c = new ErrorCollector();
        foreach (var l in new[]
                 {
                     "\t    New File  \t\t    1000\tC:\\Data\\a.pst",
                     "2026/11/20 10:15:32 ERROR 32 (0x00000020) Copying File C:\\Data\\a.pst",
                     "The process cannot access the file because it is being used by another process.",
                     "Waiting 5 seconds... Retrying...",
                     "2026/11/20 10:15:37 ERROR 32 (0x00000020) Copying File C:\\Data\\a.pst",
                     "The process cannot access the file because it is being used by another process.",
                     "ERROR: RETRY LIMIT EXCEEDED.",
                     "2026/11/20 10:15:40 ERRORE 5 (0x00000005) Copia del file \\\\nas\\s\\b c.doc",
                     "Accesso negato.",
                     "2026/11/20 10:15:41 ERROR 53 (0x00000035) Accessing Source Directory \\\\srv\\x\\",
                 })
            c.Add(l);
        c.Complete();

        Assert.Equal(3, c.Count);
        Assert.Equal(new CopyError(@"C:\Data\a.pst", 32, "Copying File", "The process cannot access the file because it is being used by another process."), c.Errors[0]);
        Assert.Equal(@"\\nas\s\b c.doc", c.Errors[1].Path);
        Assert.Equal("Accesso negato.", c.Errors[1].Message);
        Assert.Equal(53, c.Errors[2].Code);
        Assert.Equal("", c.Errors[2].Message);
    }
}

public class ReportBuilderTests
{
    private static RunReport Sample() => new()
    {
        Computer = "PC01",
        JobName = "Docs <NAS>",
        Source = @"C:\Docs",
        Destination = @"\\nas\backup",
        Mode = CopyMode.Mirror,
        Record = new RunRecord
        {
            Outcome = RunOutcome.Warning,
            StartUtc = new DateTime(2026, 11, 20, 21, 0, 0, DateTimeKind.Utc),
            EndUtc = new DateTime(2026, 11, 20, 21, 5, 0, DateTimeKind.Utc),
            FilesCopied = 1234,
            BytesCopied = 5L * 1024 * 1024 * 1024,
        },
        Errors = [new CopyError(@"C:\Docs\a.pst", 32, "Copying File", "in use")],
        ErrorCount = 1,
        KeepVersions = true,
        RetentionDays = 30,
        Archived = [new VersionMove(@"\\nas\backup\x.doc", "x.doc", false, VersionReason.Overwritten)],
        ArchivedCount = 1,
        Notes = ["note"],
    };

    [Theory]
    [InlineData("it")]
    [InlineData("en")]
    public void Run_report_shows_numbers_and_errors_and_escapes_html(string lang)
    {
        var html = ReportBuilder.Run(Sample(), lang);
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("Docs &lt;NAS&gt;", html);
        Assert.DoesNotContain("Docs <NAS>", html);
        Assert.Contains(@"C:\Docs\a.pst", html);
        Assert.Contains(Loc.Tl(lang, "rep.err.32"), html);
        Assert.Contains("x.doc", html);
        Assert.DoesNotContain("[rep.", html);
        Assert.DoesNotContain("[outcome.", html);
    }

    [Fact]
    public void Index_links_runs_that_have_a_report()
    {
        var withReport = new RunRecord { Outcome = RunOutcome.Success, StartUtc = DateTime.UtcNow, EndUtc = DateTime.UtcNow, ReportFile = "20261120-220000.html" };
        var without = new RunRecord { Outcome = RunOutcome.Failed, StartUtc = DateTime.UtcNow.AddDays(-1), EndUtc = DateTime.UtcNow.AddDays(-1) };
        var html = ReportBuilder.Index(
            [new IndexEntry { JobName = "Docs", Folder = "abc", Enabled = true, Runs = [withReport, without] }],
            (e, r) => r.ReportFile == null ? null : e.Folder + "/" + r.ReportFile,
            "it");
        Assert.Contains("href=\"abc/20261120-220000.html\"", html);
        Assert.Contains("dot failed", html);
        Assert.DoesNotContain("[rep.", html);
    }
}

public class UpdateCheckTests
{
    private const string Latest = """
        {"tag_name":"v0.3.0","html_url":"https://github.com/stefanozanarini/ags-copy/releases/tag/v0.3.0","draft":false,"prerelease":false,
         "assets":[{"name":"AGS-Copy-0.3.0.zip","browser_download_url":"https://x/zip"},
                   {"name":"AGS-Copy-0.3.0.msi","browser_download_url":"https://x/msi"}]}
        """;

    [Fact]
    public void Newer_release_with_its_msi()
    {
        var u = Updates.UpdateCheck.Parse(Latest, new Version(0, 2, 9, 0));
        Assert.NotNull(u);
        Assert.Equal(new Version(0, 3, 0), u.Version);
        Assert.Equal("https://x/msi", u.MsiUrl);
        Assert.Equal("AGS-Copy-0.3.0.msi", u.MsiName);
    }

    [Fact]
    public void Same_or_older_release_is_no_update()
    {
        Assert.Null(Updates.UpdateCheck.Parse(Latest, new Version(0, 3, 0, 0)));
        Assert.Null(Updates.UpdateCheck.Parse(Latest, new Version(1, 0, 0)));
        Assert.Null(Updates.UpdateCheck.Parse("""{"tag_name":"v9.0.0","prerelease":true}""", new Version(0, 1, 0)));
        Assert.Null(Updates.UpdateCheck.Parse("""{"tag_name":"nightly"}""", new Version(0, 1, 0)));
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("V10.0.1", "10.0.1")]
    public void Tags(string tag, string expected) => Assert.Equal(Version.Parse(expected), Updates.UpdateCheck.ParseTag(tag));
}
