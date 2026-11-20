using AgsCopy.Core;
using AgsCopy.Core.Models;
using AgsCopy.Core.Robocopy;

namespace AgsCopy.Core.Tests;

public class RobocopyOutputTests
{
    [Theory]
    [InlineData("\t    New File  \t\t    1234\tC:\\src\\a.txt", "C:\\src\\a.txt", 1234L, false)]
    [InlineData("\t    Nuovo file\t\t  987654\tC:\\src\\b c.txt", "C:\\src\\b c.txt", 987654L, false)]
    [InlineData("\t  *EXTRA File \t\t      10\tD:\\dst\\old.txt", "D:\\dst\\old.txt", 10L, false)]
    [InlineData("\t*EXTRA Dir        -1\tD:\\dst\\gone\\", "D:\\dst\\gone\\", -1L, true)]
    [InlineData("\t  New Dir          3\t\\\\nas\\share\\x\\", "\\\\nas\\share\\x\\", 3L, true)]
    [InlineData("\t                   5\tC:\\src\\sub\\", "C:\\src\\sub\\", 5L, true)]
    public void Parses_file_and_folder_lines_in_any_language(string line, string path, long size, bool dir)
    {
        var l = RobocopyOutput.ParseLine(line);
        Assert.NotNull(l);
        Assert.Equal(path, l.Value.Path);
        Assert.Equal(size, l.Value.Size);
        Assert.Equal(dir, l.Value.IsDirectory);
    }

    [Theory]
    [InlineData("   Source : C:\\src\\")]
    [InlineData("2026/11/20 10:00:00 ERROR 5 (0x00000005) Copying File C:\\src\\a.txt")]
    [InlineData("  Options : *.* /S /E /DCOPY:DA /COPY:DAT /R:2 /W:5")]
    [InlineData("")]
    [InlineData("\t  New File\t\tabc\tC:\\x.txt")]
    [InlineData("\t  New File\t\t12\trelative\\x.txt")]
    public void Ignores_other_lines(string line) => Assert.Null(RobocopyOutput.ParseLine(line));

    [Fact]
    public void Reads_the_summary_table_in_any_language()
    {
        var c = new SummaryCollector();
        foreach (var line in new[]
                 {
                     "               Total    Copied   Skipped  Mismatch    FAILED    Extras",
                     "    Dirs :        10         2         8         0         0         1",
                     "   Files :       100        20        79         0         1         3",
                     "   Bytes :   1234567     12345   1222222         0         0       500",
                     "   Times :   0:00:01   0:00:00                       0:00:00   0:00:00",
                 })
            c.Add(line);
        var s = c.Summary!;
        Assert.Equal(10, s.Dirs.Total);
        Assert.Equal(20, s.Files.Copied);
        Assert.Equal(1, s.Files.Failed);
        Assert.Equal(12345, s.Bytes.Copied);
        Assert.Equal(500, s.Bytes.Extras);

        var it = new SummaryCollector();
        it.Add("Directory :         4         1         3         0         0         0");
        it.Add("     File :         7         2         5         0         0         2");
        it.Add("     Byte :      7000      2000      5000         0         0       100");
        Assert.Equal(2, it.Summary!.Files.Copied);
    }

    [Theory]
    [InlineData(0, RunOutcome.Success)]
    [InlineData(1, RunOutcome.Success)]
    [InlineData(3, RunOutcome.Success)]
    [InlineData(4, RunOutcome.Warning)]
    [InlineData(7, RunOutcome.Warning)]
    [InlineData(8, RunOutcome.Failed)]
    [InlineData(16, RunOutcome.Failed)]
    public void Exit_codes(int code, RunOutcome outcome) => Assert.Equal(outcome, RobocopyExitCode.Outcome(code));
}

public class PreviewTests
{
    [Fact]
    public void Mirror_preview_tells_copies_from_deletions_by_path()
    {
        var agg = new PreviewAggregator(@"C:\Data\", @"\\nas\backup\Data", CopyMode.Mirror);
        foreach (var line in new[]
                 {
                     "\t                   2\tC:\\Data\\",
                     "\t    New File  \t\t    1000\tC:\\Data\\a.docx",
                     "\t    Newer     \t\t    2000\tC:\\Data\\b.xlsx",
                     "\t  *EXTRA File \t\t     300\t\\\\nas\\backup\\Data\\old.txt",
                     "\t*EXTRA Dir        -1\t\\\\nas\\backup\\Data\\Archive\\",
                 })
            agg.Add(line);
        var r = agg.Complete(3);

        Assert.Equal(2, r.FilesToCopy);
        Assert.Equal(3000, r.BytesToCopy);
        Assert.Equal(1, r.ExtraFiles);
        Assert.Equal(300, r.ExtraBytes);
        Assert.Equal(1, r.ExtraDirs);
        Assert.Equal(2, r.ExtraItems.Count);
        Assert.True(r.ExtrasDeleted);
    }

    [Fact]
    public void Destination_inside_a_similarly_named_folder_is_not_mistaken_for_the_source()
    {
        var agg = new PreviewAggregator(@"D:\Data", @"D:\Data2", CopyMode.Mirror);
        agg.Add("\t  *EXTRA File \t\t     300\tD:\\Data2\\x.txt");
        agg.Add("\t    New File  \t\t     100\tD:\\Data\\y.txt");
        var r = agg.Complete(3);
        Assert.Equal(1, r.ExtraFiles);
        Assert.Equal(1, r.FilesToCopy);
    }

    [Fact]
    public void Progress_counts_only_copied_source_files()
    {
        var p = new ProgressCounter(@"C:\Data");
        p.Add("\t    New File  \t\t    1000\tC:\\Data\\a.docx");
        p.Add("\t  *EXTRA File \t\t     300\tE:\\Copy\\old.txt");
        p.Add("\t                   2\tC:\\Data\\sub\\");
        Assert.Equal(1, p.Files);
        Assert.Equal(1000, p.Bytes);
    }
}

public class RobocopyArgsTests
{
    private static CopyJob Job(CopyMode mode) => new()
    {
        Name = "t",
        Source = new JobEndpoint { Path = @"C:\My Data\" },
        Destination = new JobEndpoint { Path = @"\\nas\backup\" },
        Mode = mode,
    };

    [Fact]
    public void Copy_mode_never_purges()
    {
        var a = RobocopyArgs.Build(Job(CopyMode.Copy), @"C:\My Data\", @"\\nas\backup\", RobocopyPass.Full, false, null, false);
        Assert.Equal(@"C:\My Data", a[0]);
        Assert.Equal(@"\\nas\backup", a[1]);
        Assert.Contains("/E", a);
        Assert.DoesNotContain("/MIR", a);
        Assert.DoesNotContain("/PURGE", a);
        Assert.DoesNotContain("/MOV", a);
        Assert.Contains("/MT:16", a);
        Assert.Contains("/FP", a);
        Assert.Contains("/BYTES", a);
    }

    [Fact]
    public void No_mode_ever_writes_to_the_source()
    {
        foreach (var mode in Enum.GetValues<CopyMode>())
        foreach (var pass in Enum.GetValues<RobocopyPass>())
        {
            var a = RobocopyArgs.Build(Job(mode), @"C:\a", @"D:\b", pass, false, null, false, ["x"]);
            Assert.DoesNotContain(a, x => x.Equals("/MOV", StringComparison.OrdinalIgnoreCase)
                                          || x.Equals("/MOVE", StringComparison.OrdinalIgnoreCase)
                                          || x.Equals("/M", StringComparison.OrdinalIgnoreCase));
        }
        Assert.Contains("/MIR", RobocopyArgs.Build(Job(CopyMode.Mirror), @"C:\a", @"D:\b", RobocopyPass.Full, false, null, false));
    }

    [Theory]
    [InlineData("/MOV")]
    [InlineData("/move")]
    [InlineData("/XO /M")]
    public void Source_writing_extra_switches_are_refused(string extra)
    {
        var job = Job(CopyMode.Copy);
        job.Options.ExtraArguments = extra;
        Assert.NotEmpty(RobocopyArgs.SourceWritingSwitches(extra));
        Assert.Throws<InvalidOperationException>(() => RobocopyArgs.Build(job, @"C:\a", @"D:\b", RobocopyPass.Full, false, null, false));
        Assert.Contains(JobValidator.Validate(job, "en"), e => e.Contains("source"));
    }

    [Fact]
    public void Harmless_extra_switches_are_accepted() =>
        Assert.Empty(RobocopyArgs.SourceWritingSwitches("/MAXAGE:30 /IPG:10 /MIN:1 /A"));

    [Fact]
    public void Root_pass_copies_root_files_and_excludes_the_parallel_folders()
    {
        var job = Job(CopyMode.Copy);
        job.Options.ExcludeDirectories.Add("Temp");
        var a = RobocopyArgs.Build(job, @"C:\a", @"D:\b", RobocopyPass.Root, false, null, false, ["One", "Two"]);
        Assert.DoesNotContain("/E", a);
        var xd = a.IndexOf("/XD");
        Assert.Equal(["Temp", "One", "Two"], a.Skip(xd + 1).Take(3));
    }

    [Fact]
    public void Drive_roots_keep_their_backslash() =>
        Assert.Equal(@"D:\", RobocopyArgs.Build(Job(CopyMode.Copy), @"C:\a", "D:", RobocopyPass.Full, false, null, false)[1]);

    [Fact]
    public void Preview_and_logging_switches()
    {
        var a = RobocopyArgs.Build(Job(CopyMode.Copy), @"C:\a", @"D:\b", RobocopyPass.Full, true, @"C:\l.log", true);
        Assert.Contains("/L", a);
        Assert.Contains(@"/UNILOG:C:\l.log", a);
        Assert.Contains("/TEE", a);
    }

    [Fact]
    public void Extra_arguments_are_split_like_a_command_line()
    {
        var job = Job(CopyMode.Copy);
        job.Options.ExtraArguments = "/MAXAGE:30  /XF \"a b.txt\"";
        var a = RobocopyArgs.Build(job, @"C:\a", @"D:\b", RobocopyPass.Full, false, null, false);
        Assert.Equal(["/MAXAGE:30", "/XF", "a b.txt"], a.TakeLast(3));
    }
}

public class ParallelPlanTests
{
    private static CopyJob Job(int processes) => new()
    {
        Source = new JobEndpoint { Path = @"C:\Data" },
        Destination = new JobEndpoint { Path = @"E:\Copy" },
        Options = new CopyOptions { Processes = processes, ExcludeDirectories = ["Temp", @"C:\Data\Skip"] },
    };

    [Fact]
    public void One_process_means_one_full_pass() =>
        Assert.Single(ParallelPlan.Build(Job(1), ["A", "B", "C"]), t => t.Pass == RobocopyPass.Full);

    [Fact]
    public void Splits_by_top_level_folder_and_skips_excluded_ones()
    {
        var tasks = ParallelPlan.Build(Job(4), ["B", "A", "Temp", "Skip", "C"]);
        Assert.Equal(RobocopyPass.Root, tasks[0].Pass);
        Assert.Equal(["A", "B", "C"], tasks[0].RootExcludedDirs!);
        Assert.Equal([@"C:\Data\A", @"C:\Data\B", @"C:\Data\C"], tasks.Skip(1).Select(t => t.Source));
        Assert.Equal(@"E:\Copy\B", tasks[2].Destination);
    }

    [Fact]
    public void Too_many_folders_fall_back_to_one_process()
    {
        var names = Enumerable.Range(0, 5000).Select(i => "Folder number " + i).ToList();
        Assert.Single(ParallelPlan.Build(Job(8), names));
    }

    [Theory]
    [InlineData("tmp*", "tmpfiles", true)]
    [InlineData("?emp", "Temp", true)]
    [InlineData("Temp", "Temporary", false)]
    public void Wildcards(string pattern, string name, bool excluded) =>
        Assert.Equal(excluded, ParallelPlan.IsExcluded([pattern], @"C:\x", name));
}

public class CommandLineTests
{
    [Fact]
    public void Split_and_join_round_trip()
    {
        var parts = CommandLine.Split("a \"b c\" d");
        Assert.Equal(["a", "b c", "d"], parts);
        Assert.Equal("a \"b c\" d", CommandLine.Join(parts));
    }
}
