using System.Text.RegularExpressions;
using AgsCopy.Core;
using AgsCopy.Core.Models;
using AgsCopy.Core.Scheduling;

// Loc.Language is global state.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace AgsCopy.Core.Tests;

public class PathUtilTests
{
    [Theory]
    [InlineData(@"\\srv\share\a\b", @"\\srv\share")]
    [InlineData(@"\\srv\share", @"\\srv\share")]
    [InlineData(@"\\srv", null)]
    [InlineData(@"C:\x", null)]
    public void Share_root(string path, string? root) => Assert.Equal(root, PathUtil.ShareRoot(path));

    [Theory]
    [InlineData(@"C:\a\", @"C:\a")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData("C:", @"C:\")]
    [InlineData(@" \\srv\share\ ", @"\\srv\share")]
    [InlineData("C:/a/b/", @"C:\a\b")]
    public void Normalize(string path, string expected) => Assert.Equal(expected, PathUtil.NormalizeDir(path));

    [Theory]
    [InlineData(@"C:\Data", @"C:\Data\Sub", true)]
    [InlineData(@"C:\Data", @"c:\data", true)]
    [InlineData(@"C:\Data", @"C:\Data2", false)]
    [InlineData(@"\\a\s", @"\\b\s", false)]
    public void Overlaps(string a, string b, bool overlap) => Assert.Equal(overlap, PathUtil.Overlaps(a, b));

    [Theory]
    [InlineData(@"C:\x", true)]
    [InlineData(@"\\srv\share", true)]
    [InlineData(@"\\srv", false)]
    [InlineData(@"x\y", false)]
    [InlineData("", false)]
    public void Absolute(string p, bool ok) => Assert.Equal(ok, PathUtil.IsAbsolute(p));
}

public class ScheduleTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 15, 30, 0); // a Friday

    [Fact]
    public void Daily_runs_today_if_still_ahead_else_tomorrow()
    {
        var s = new Schedule { Kind = ScheduleKind.Daily, Start = new DateTime(2026, 1, 1, 22, 0, 0) };
        Assert.Equal(new DateTime(2026, 11, 20, 22, 0, 0), ScheduleCalculator.Next(s, Now));
        s.Start = new DateTime(2026, 1, 1, 8, 0, 0);
        Assert.Equal(new DateTime(2026, 11, 21, 8, 0, 0), ScheduleCalculator.Next(s, Now));
    }

    [Fact]
    public void Daily_does_not_start_before_its_start_date()
    {
        var s = new Schedule { Kind = ScheduleKind.Daily, Start = new DateTime(2026, 12, 1, 22, 0, 0) };
        Assert.Equal(new DateTime(2026, 12, 1, 22, 0, 0), ScheduleCalculator.Next(s, Now));
    }

    [Fact]
    public void Weekly_picks_the_next_chosen_day()
    {
        var s = new Schedule { Kind = ScheduleKind.Weekly, Start = new DateTime(2026, 1, 1, 9, 0, 0), DaysOfWeek = [DayOfWeek.Monday, DayOfWeek.Wednesday] };
        Assert.Equal(new DateTime(2026, 11, 23, 9, 0, 0), ScheduleCalculator.Next(s, Now));
        s.DaysOfWeek = [DayOfWeek.Friday];
        Assert.Equal(new DateTime(2026, 11, 27, 9, 0, 0), ScheduleCalculator.Next(s, Now));
        s.DaysOfWeek = [];
        Assert.Null(ScheduleCalculator.Next(s, Now));
    }

    [Fact]
    public void Monthly_clamps_to_the_last_day()
    {
        var s = new Schedule { Kind = ScheduleKind.Monthly, Start = new DateTime(2026, 1, 1, 23, 0, 0), DayOfMonth = 31 };
        Assert.Equal(new DateTime(2026, 11, 30, 23, 0, 0), ScheduleCalculator.Next(s, Now));
        Assert.Equal(new DateTime(2027, 2, 28, 23, 0, 0), ScheduleCalculator.Next(s, new DateTime(2027, 2, 1)));
    }

    [Fact]
    public void Interval_steps_from_the_start()
    {
        var s = new Schedule { Kind = ScheduleKind.Interval, Start = new DateTime(2026, 11, 20, 15, 0, 0), IntervalMinutes = 20 };
        Assert.Equal(new DateTime(2026, 11, 20, 15, 40, 0), ScheduleCalculator.Next(s, Now));
        Assert.Equal(new DateTime(2026, 11, 20, 15, 0, 0), ScheduleCalculator.Next(s, Now.AddHours(-2)));
    }

    [Fact]
    public void Once_and_manual()
    {
        Assert.Null(ScheduleCalculator.Next(new Schedule { Kind = ScheduleKind.Manual }, Now));
        var once = new Schedule { Kind = ScheduleKind.Once, Start = Now.AddMinutes(5) };
        Assert.Equal(Now.AddMinutes(5), ScheduleCalculator.Next(once, Now));
        Assert.Null(ScheduleCalculator.Next(once, Now.AddMinutes(10)));
    }

    [Fact]
    public void Missed_runs_happen_at_startup_only_when_asked()
    {
        var s = new Schedule { Kind = ScheduleKind.Daily, Start = new DateTime(2026, 1, 1, 22, 0, 0), RunMissed = true };
        var lastRun = new DateTime(2026, 11, 18, 22, 0, 0); // the 19th was missed
        Assert.Equal(new DateTime(2026, 11, 19, 22, 0, 0), ScheduleCalculator.OnStartup(s, lastRun, lastRun, Now));
        s.RunMissed = false;
        Assert.Equal(new DateTime(2026, 11, 20, 22, 0, 0), ScheduleCalculator.OnStartup(s, lastRun, lastRun, Now));
    }
}

public class ValidationAndTextTests
{
    private static CopyJob Valid() => new()
    {
        Name = "Docs",
        Source = new JobEndpoint { Path = @"C:\Docs" },
        Destination = new JobEndpoint { Path = @"\\nas\backup\Docs", Credential = new StoredCredential { UserName = @"NAS\backup", Password = "x" } },
    };

    [Fact]
    public void A_valid_job_has_no_errors() => Assert.Empty(JobValidator.Validate(Valid(), "en"));

    [Fact]
    public void Mirror_needs_confirmation()
    {
        var j = Valid();
        j.Mode = CopyMode.Mirror;
        Assert.Contains(Loc.Tl("en", "val.confirm"), JobValidator.Validate(j, "en"));
        j.DeletionConfirmed = true;
        Assert.Empty(JobValidator.Validate(j, "en"));
    }

    [Fact]
    public void Nested_paths_and_missing_passwords_are_rejected()
    {
        var j = Valid();
        j.Destination = new JobEndpoint { Path = @"C:\Docs\Backup" };
        Assert.Contains(Loc.Tl("en", "val.overlap"), JobValidator.Validate(j, "en"));

        j = Valid();
        j.Destination.Credential!.Password = null;
        Assert.Contains(Loc.Tl("en", "val.cred.destination"), JobValidator.Validate(j, "en"));
        j.Destination.Credential.HasPassword = true; // stored on the service
        Assert.Empty(JobValidator.Validate(j, "en"));
    }

    [Theory]
    [InlineData("it")]
    [InlineData("en")]
    public void Summary_mentions_deletion_for_destructive_modes(string lang)
    {
        Loc.Language = lang;
        try
        {
            foreach (var mode in Enum.GetValues<CopyMode>())
            {
                var j = Valid();
                j.Mode = mode;
                j.Options.Processes = 4;
                j.Schedule = new Schedule { Kind = ScheduleKind.Weekly, DaysOfWeek = [DayOfWeek.Monday] };
                var lines = JobDescriber.Describe(j);
                Assert.DoesNotContain(lines, l => l.Text.Contains('['));
                Assert.Equal(mode == CopyMode.Mirror, lines.Any(l => l.Level == SummaryLevel.Danger));
                // Every summary states that the source is left untouched.
                Assert.Contains(lines, l => l.Text == Loc.F("sum.readonly", @"C:\Docs"));
            }
        }
        finally
        {
            Loc.Language = "it";
        }
    }
}

public class LocalizationTests
{
    private static readonly Regex Placeholder = new(@"\{(\d+)\}");

    [Fact]
    public void Both_languages_have_the_same_keys_and_placeholders()
    {
        var it = Loc.Table("it");
        var en = Loc.Table("en");
        Assert.Empty(it.Keys.Except(en.Keys));
        Assert.Empty(en.Keys.Except(it.Keys));
        foreach (var key in it.Keys)
        {
            var a = Placeholder.Matches(it[key]).Select(m => m.Value).ToHashSet();
            var b = Placeholder.Matches(en[key]).Select(m => m.Value).ToHashSet();
            Assert.True(a.SetEquals(b), $"Placeholders differ for {key}");
        }
    }

    [Fact]
    public void Every_key_used_in_the_sources_exists()
    {
        var root = RepoRoot();
        var used = new HashSet<string>();
        var literal = new Regex("\"((?:val|sum|sched|prev|preview|err|test|log|tray|main|editor|endpoint|general|source|destination|mode|options|props|kind|schedule|summary|step|history|col|outcome|trigger|run)\\.[A-Za-z0-9_.]*[A-Za-z0-9_])\"");
        var markup = new Regex(@"\{l:T ([A-Za-z0-9_.]+)\}");
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            if (file.EndsWith("Loc.Strings.cs") || file.EndsWith("Protocol.cs")) continue;
            var text = File.ReadAllText(file);
            if (file.EndsWith(".cs")) used.UnionWith(literal.Matches(text).Select(m => m.Groups[1].Value));
            if (file.EndsWith(".xaml")) used.UnionWith(markup.Matches(text).Select(m => m.Groups[1].Value));
        }

        // Keys built from enum values.
        used.UnionWith(Enum.GetNames<CopyMode>().Select(n => "mode." + n));
        used.UnionWith(Enum.GetNames<RunOutcome>().Select(n => "outcome." + n));
        used.UnionWith(Enum.GetNames<RunTrigger>().Select(n => "trigger." + n));
        used.UnionWith(Enum.GetNames<FileProperties>().Select(n => "sum.props." + n));

        var it = Loc.Table("it");
        Assert.True(used.Count > 100, "the scan found too few keys: " + used.Count);
        Assert.Empty(used.Where(k => !it.ContainsKey(k)).OrderBy(k => k));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AgsCopy.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("AgsCopy.sln not found");
    }
}

public class FmtTests
{
    [Fact]
    public void Bytes()
    {
        var c = System.Globalization.CultureInfo.InvariantCulture;
        Assert.Equal("512 B", Fmt.Bytes(512, c));
        Assert.Equal("1.5 KB", Fmt.Bytes(1536, c));
        Assert.Equal("10 GB", Fmt.Bytes(10L * 1024 * 1024 * 1024, c));
    }
}
