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
                Assert.Contains(lines, l => l.Text == Loc.F("sum.vss", "C:"));
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
        var literal = new Regex("\"((?:rep|val|sum|sched|prev|preview|err|test|log|tray|main|editor|endpoint|general|source|destination|mode|options|props|kind|schedule|summary|step|history|col|outcome|trigger|run|update|layout)\\.[A-Za-z0-9_.]*[A-Za-z0-9_])\"");
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
        used.UnionWith(Enum.GetNames<VersionReason>().Select(n => "rep.reason." + n));
        used.UnionWith(Enum.GetNames<LayoutFate>().Select(n => "layout.fate." + n));
        used.UnionWith(new[] { 2, 3, 5, 32, 33, 53, 64, 67, 112, 121, 1219, 1326 }.Select(c => "rep.err." + c));

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

public class WatchdogPolicyTests
{
    [Fact]
    public void Starts_a_service_only_after_it_stays_stopped()
    {
        var p = new WatchdogPolicy();
        Assert.Equal(WatchdogAction.None, p.Next(ServiceHealth.Stopped));
        Assert.Equal(WatchdogAction.Start, p.Next(ServiceHealth.Stopped));
        Assert.Equal(WatchdogAction.None, p.Next(ServiceHealth.Stopped));
    }

    [Fact]
    public void Restarts_a_hung_service_after_three_checks_in_a_row()
    {
        var p = new WatchdogPolicy();
        Assert.Equal(WatchdogAction.None, p.Next(ServiceHealth.Unresponsive));
        Assert.Equal(WatchdogAction.None, p.Next(ServiceHealth.Unresponsive));
        Assert.Equal(WatchdogAction.None, p.Next(ServiceHealth.Healthy));
        Assert.Equal(WatchdogAction.None, p.Next(ServiceHealth.Unresponsive));
        Assert.Equal(WatchdogAction.None, p.Next(ServiceHealth.Unresponsive));
        Assert.Equal(WatchdogAction.Restart, p.Next(ServiceHealth.Unresponsive));
    }

    [Fact]
    public void Ignored_and_healthy_never_act()
    {
        var p = new WatchdogPolicy();
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(WatchdogAction.None, p.Next(ServiceHealth.Ignored));
            Assert.Equal(WatchdogAction.None, p.Next(ServiceHealth.Healthy));
        }
    }

    [Fact]
    public void A_pending_state_that_never_ends_counts_as_hung()
    {
        var p = new WatchdogPolicy();
        for (var i = 0; i < 4; i++) Assert.Equal(WatchdogAction.None, p.Next(ServiceHealth.Changing));
        Assert.Equal(WatchdogAction.Restart, p.Next(ServiceHealth.Changing));
    }
}

public class DestinationLayoutTests
{
    private static PathEntry D(string n) => new() { Name = n, IsDirectory = true };
    private static PathEntry F(string n, long size = 10) => new() { Name = n, Size = size };

    private static CopyJob Job(string src, string dst, CopyMode mode = CopyMode.Copy) => new()
    {
        Source = new JobEndpoint { Path = src },
        Destination = new JobEndpoint { Path = dst },
        Mode = mode,
    };

    [Fact]
    public void Fates_follow_the_mode()
    {
        var src = new[] { D("Docs"), F("a.txt") };
        var dst = new[] { D("Docs"), F("old.txt"), D(Versions.FolderName) };

        var copy = DestinationLayout.Build(Job(@"C:\Data", @"E:\Data"), "", src, dst);
        Assert.Equal(LayoutFate.Update, copy.Items.Single(i => i.Name == "Docs").Fate);
        Assert.Equal(LayoutFate.New, copy.Items.Single(i => i.Name == "a.txt").Fate);
        Assert.Equal(LayoutFate.Kept, copy.Items.Single(i => i.Name == "old.txt").Fate);
        Assert.Equal(LayoutFate.Excluded, copy.Items.Single(i => i.Name == Versions.FolderName).Fate);
        Assert.True(copy.Items[0].IsDirectory);

        var mirror = DestinationLayout.Build(Job(@"C:\Data", @"E:\Data", CopyMode.Mirror), "", src, dst);
        Assert.Equal(LayoutFate.Deleted, mirror.Items.Single(i => i.Name == "old.txt").Fate);
        var job = Job(@"C:\Data", @"E:\Data", CopyMode.Mirror);
        job.Options.KeepVersions = true;
        Assert.Equal(LayoutFate.Archived, DestinationLayout.Build(job, "", src, dst).Items.Single(i => i.Name == "old.txt").Fate);
    }

    [Fact]
    public void Warns_when_earlier_copies_went_into_a_subfolder_named_like_the_source()
    {
        Loc.Language = "en";
        var r = DestinationLayout.Build(Job(@"C:\Data", @"E:\Backup"), "", [F("a.txt")], [D("Data")]);
        Assert.Equal(@"E:\Backup\Data", r.Suggestion);
        Assert.True(r.SuggestionRecommended);
        Assert.Contains(r.Notes, n => n.Level == SummaryLevel.Warning && n.Text.Contains(@"E:\Backup\Data"));
        Loc.Language = "it";
    }

    [Fact]
    public void Warns_about_a_folder_one_level_too_deep()
    {
        var r = DestinationLayout.Build(Job(@"C:\Data", @"E:\Backup\Data"), "", [F("a.txt")], [D("Data")]);
        Assert.Null(r.Suggestion);
        Assert.Contains(r.Notes, n => n.Level == SummaryLevel.Warning);
    }

    [Fact]
    public void A_mirror_into_an_unrelated_folder_is_a_danger()
    {
        var r = DestinationLayout.Build(Job(@"C:\Data", @"E:\Other", CopyMode.Mirror), "", [F("a.txt")], [F("x.doc"), F("y.doc")]);
        Assert.Contains(r.Notes, n => n.Level == SummaryLevel.Danger);
        var ok = DestinationLayout.Build(Job(@"C:\Data", @"E:\Data", CopyMode.Mirror), "", [F("a.txt")], [F("a.txt"), F("y.doc")]);
        Assert.DoesNotContain(ok.Notes, n => n.Level != SummaryLevel.Info);
    }

    [Fact]
    public void Roots_have_no_folder_name_to_keep()
    {
        Assert.Null(DestinationLayout.FolderName(@"D:\"));
        Assert.Null(DestinationLayout.FolderName(@"\\nas\share"));
        Assert.Equal("Data", DestinationLayout.FolderName(@"\\nas\share\Data\"));
        Assert.Null(DestinationLayout.Build(Job(@"D:\", @"E:\Backup"), "", [F("a")], null).Suggestion);
    }

    [Fact]
    public void Excluded_names_and_new_destination()
    {
        var job = Job(@"C:\Data", @"E:\Data");
        job.Options.ExcludeFiles.Add("*.tmp");
        job.Options.ExcludeDirectories.Add("Cache");
        var r = DestinationLayout.Build(job, "", [F("x.tmp"), D("Cache"), F("y.doc")], null);
        Assert.Equal(LayoutFate.Excluded, r.Items.Single(i => i.Name == "x.tmp").Fate);
        Assert.Equal(LayoutFate.Excluded, r.Items.Single(i => i.Name == "Cache").Fate);
        Assert.Equal(LayoutFate.New, r.Items.Single(i => i.Name == "y.doc").Fate);
        Assert.Equal(2, r.Notes.Count(n => n.Level == SummaryLevel.Info));
    }
}
