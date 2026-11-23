using System.Text.Json;
using AgsCopy.Core.Models;
using AgsCopy.Core.Robocopy;
using AgsCopy.Core.Sync;

namespace AgsCopy.Core.Tests;

public class MultiSourceJobTests
{
    private static CopyJob Job(params (string Path, string? Folder)[] sources) => new()
    {
        Name = "multi",
        Source = new JobEndpoint { Path = sources[0].Path, Folder = sources[0].Folder },
        ExtraSources = sources.Skip(1).Select(s => new JobEndpoint { Path = s.Path, Folder = s.Folder }).ToList(),
        Destination = new JobEndpoint { Path = @"E:\Backup" },
    };

    [Fact]
    public void Each_source_goes_into_its_own_folder()
    {
        var job = Job((@"D:\Data", "Data"), (@"\\srv\share\Docs", "Documents"));
        Assert.Empty(JobValidator.Validate(job, "en"));
        Assert.Equal(@"E:\Backup\Data", job.DestinationFor(0));
        Assert.Equal(@"E:\Backup\Documents", job.DestinationFor(1));

        var part = job.ForSource(1);
        Assert.Equal(@"\\srv\share\Docs", part.Source.Path);
        Assert.Equal(@"E:\Backup\Documents", part.Destination.Path);
        Assert.False(part.MultiSource);
        Assert.Equal(@"E:\Backup", job.Destination.Path); // the job itself is unchanged
    }

    [Fact]
    public void A_single_source_fills_the_destination_as_before()
    {
        var job = Job((@"D:\Data", "Ignored"));
        Assert.Empty(JobValidator.Validate(job, "en"));
        Assert.Equal(@"E:\Backup", job.DestinationFor(0));
        Assert.Equal(@"E:\Backup", job.ForSource(0).Destination.Path);
    }

    [Fact]
    public void Sources_must_not_overlap()
    {
        var errors = JobValidator.Validate(Job((@"D:\Data", "A"), (@"D:\Data\Sub", "B")), "en");
        Assert.Contains(errors, e => e.Contains("overlap") && e.Contains(@"D:\Data\Sub"));

        errors = JobValidator.Validate(Job((@"D:\Data", "A"), (@"d:\data\", "B")), "en");
        Assert.Contains(errors, e => e.Contains("overlap"));

        // Same folder name in different places is fine: they are different sources.
        Assert.Empty(JobValidator.Validate(Job((@"D:\Data", "A"), (@"D:\Data2", "B")), "en"));
    }

    [Fact]
    public void Each_source_needs_a_folder_of_its_own()
    {
        Assert.Contains(JobValidator.Validate(Job((@"D:\A\Docs", "Docs"), (@"F:\B\Docs", "docs")), "en"), e => e.Contains("same folder"));
        Assert.Contains(JobValidator.Validate(Job((@"D:\A", "A"), (@"F:\B", "")), "en"), e => e.Contains("Invalid destination folder"));
        Assert.Contains(JobValidator.Validate(Job((@"D:\A", "A"), (@"F:\B", @"x\y")), "en"), e => e.Contains("Invalid destination folder"));
        Assert.Contains(JobValidator.Validate(Job((@"D:\A", "A"), (@"F:\B", "..")), "en"), e => e.Contains("Invalid destination folder"));
        Assert.Contains(JobValidator.Validate(Job((@"D:\A", "A"), (@"F:\B", Versions.FolderName)), "en"), e => e.Contains("reserved"));
    }

    private static JobEndpoint Share(string path, string user, string folder) =>
        new() { Path = path, Folder = folder, Credential = new StoredCredential { UserName = user, Password = "x" } };

    [Fact]
    public void Each_source_has_its_own_account_but_one_per_server()
    {
        var job = Job((@"D:\Data", "Data"));
        job.Source = Share(@"\\fs1\accounts", @"CORP\anna", "Accounts");
        job.ExtraSources = [Share(@"\\fs2\tech", @"CORP\marco", "Tech")];
        Assert.Empty(JobValidator.Validate(job, "en"));

        // Same server, same user (spelled differently): fine.
        job.ExtraSources = [Share(@"\\FS1\tech", @"corp\ANNA", "Tech")];
        Assert.Empty(JobValidator.Validate(job, "en"));

        job.ExtraSources = [Share(@"\\fs1\tech", @"CORP\marco", "Tech")];
        Assert.Contains(JobValidator.Validate(job, "en"), e => e.Contains("fs1") && e.Contains("1219"));

        // The destination counts too; by IP address the server is another one for Windows.
        job.ExtraSources = [];
        job.Destination = Share(@"\\fs1\backup", @"CORP\backup", "");
        Assert.Contains(JobValidator.Validate(job, "en"), e => e.Contains("1219"));
        job.Destination = Share(@"\\10.0.0.5\backup", @"CORP\backup", "");
        Assert.Empty(JobValidator.Validate(job, "en"));
    }

    [Fact]
    public void Every_source_goes_to_every_destination()
    {
        var job = Job((@"D:\Data", "Data"), (@"F:\Mail", "Mail"));
        job.ExtraDestinations = [new JobEndpoint { Path = @"\\nas\backup" }];
        Assert.Empty(JobValidator.Validate(job, "en"));
        Assert.Equal([(0, 0), (1, 0), (0, 1), (1, 1)], job.Pairs);
        Assert.Equal(@"\\nas\backup\Mail", job.DestinationFor(1, 1));

        var part = job.ForSource(1, 1);
        Assert.Equal(@"F:\Mail", part.Source.Path);
        Assert.Equal(@"\\nas\backup\Mail", part.Destination.Path);
        Assert.False(part.MultiDestination);
        Assert.Equal(@"[→ \\nas\backup · Mail] ", job.PairLabel(1, 1));
        Assert.Equal(@"E:\Backup (+1)", job.DestinationSummary);

        var lines = JobDescriber.Describe(job).Select(l => l.Text).ToList();
        Assert.Contains(lines, l => l.Contains(@"E:\Backup") && l.Contains(@"\\nas\backup") && l.Contains('2'));
        Assert.Contains(lines, l => l.Contains(@"F:\Mail") && l.Contains(@"\\nas\backup\Mail"));
    }

    [Fact]
    public void Destinations_must_not_overlap_each_other_or_a_source()
    {
        var job = Job((@"D:\Data", "Data"));
        job.ExtraDestinations = [new JobEndpoint { Path = @"E:\Backup\Second" }];
        Assert.Contains(JobValidator.Validate(job, "en"), e => e.Contains("destinations") && e.Contains("overlap"));

        job.ExtraDestinations = [new JobEndpoint { Path = @"D:\Data\Copy" }];
        Assert.Contains(JobValidator.Validate(job, "en"), e => e.Contains(@"D:\Data\Copy") && e.Contains("inside each other"));

        job.ExtraDestinations = [new JobEndpoint { Path = "backup" }];
        Assert.Contains(JobValidator.Validate(job, "en"), e => e.StartsWith("Destination 2"));
    }

    [Fact]
    public void Jobs_saved_before_keep_one_destination()
    {
        const string old = """{"name":"old","source":{"path":"D:\\Data"},"destination":{"path":"E:\\B"},"mode":"Copy"}""";
        var job = JsonSerializer.Deserialize<CopyJob>(old, Json.Options)!;
        Assert.Single(job.Destinations);
        Assert.Equal(@"E:\B", job.DestinationSummary);
        Assert.False(job.Options.DeleteExtrasFirst);
    }

    [Fact]
    public void No_source_may_contain_the_destination()
    {
        var job = Job((@"D:\Data", "A"), (@"E:\", "E"));
        Assert.Contains(JobValidator.Validate(job, "en"), e => e.Contains(@"E:\") && e.Contains("inside each other"));
    }

    [Theory]
    [InlineData(@"D:\Data\Docs", "Docs")]
    [InlineData(@"\\srv\share", "srv-share")]
    [InlineData(@"D:\", "Drive-D")]
    [InlineData("sftp://nas.local/home/backup", "backup")]
    [InlineData("sftp://nas.local/", "nas.local")]
    public void Default_folder_names(string path, string expected) => Assert.Equal(expected, CopyJob.DefaultFolder(path));

    [Fact]
    public void Jobs_saved_before_keep_loading_with_one_source()
    {
        const string old = """{"name":"old","source":{"path":"D:\\Data"},"destination":{"path":"E:\\B"},"mode":"Copy"}""";
        var job = JsonSerializer.Deserialize<CopyJob>(old, Json.Options)!;
        Assert.Single(job.Sources);
        Assert.False(job.MultiSource);
        Assert.Equal(@"D:\Data", job.SourceSummary);
    }

    [Fact]
    public void The_summary_lists_every_source_and_its_folder()
    {
        var lines = JobDescriber.Describe(Job((@"D:\Data", "Data"), (@"F:\Mail", "Mail"))).Select(l => l.Text).ToList();
        Assert.Contains(lines, l => l.Contains(@"D:\Data") && l.Contains(@"E:\Backup\Data"));
        Assert.Contains(lines, l => l.Contains(@"F:\Mail") && l.Contains(@"E:\Backup\Mail"));
    }

    [Fact]
    public void Previews_of_the_sources_add_up()
    {
        var a = new PreviewResult { Source = "A", FilesToCopy = 2, BytesToCopy = 10, DestinationExists = true, CopyItems = [new PreviewItem { Path = "a" }] };
        var b = new PreviewResult { Source = "B", FilesToCopy = 3, BytesToCopy = 5, DestinationExists = false, Error = "no B", CopyItems = [new PreviewItem { Path = "b" }] };
        a.Merge(b);
        Assert.Equal(5, a.FilesToCopy);
        Assert.Equal(15, a.BytesToCopy);
        Assert.False(a.DestinationExists);
        Assert.Equal("no B", a.Error);
        Assert.Equal(["a", "b"], a.CopyItems.Select(i => i.Path));
    }
}

public class LinkedFolderTests
{
    private static readonly Dictionary<string, FolderEntry[]> Namespace = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"\\corp\dfs"] = [new("Sales", true, false, true), new("Depts", false, false, false), new("Junction", false, true, true), new("Old", true, false, true)],
        [@"\\corp\dfs\Depts"] = [new("IT", true, false, true), new("Plain", false, false, false)],
        [@"\\corp\dfs\Depts\Plain"] = [],
    };

    private static IEnumerable<FolderEntry> ListNamespace(string path) =>
        Namespace.TryGetValue(path, out var f) ? f : throw new InvalidOperationException("Entered " + path);

    private static CopyJob Job(string source, params string[] excludeDirs) => new()
    {
        Source = new JobEndpoint { Path = source },
        Destination = new JobEndpoint { Path = @"E:\Backup" },
        Options = new CopyOptions { ExcludeDirectories = excludeDirs.ToList() },
    };

    [Fact]
    public void Finds_dfs_links_without_entering_them_or_junctions()
    {
        var scan = LinkedFolders.Find(@"\\corp\dfs", ListNamespace, null, new Exclusions(Job(@"\\corp\dfs")), false, default);
        Assert.Equal([@"Sales", @"Old", @"Depts\IT"], scan.Links.Select(l => l.Relative));
        Assert.All(scan.Links, l => Assert.Equal(LinkKind.Dfs, l.Kind));
        Assert.Equal(@"\\corp\dfs\Depts\IT", scan.Links[2].Path);
    }

    [Fact]
    public void Excluded_folders_are_not_followed()
    {
        var scan = LinkedFolders.Find(@"\\corp\dfs", ListNamespace, null, new Exclusions(Job(@"\\corp\dfs", "Old", @"\\corp\dfs\Depts")), false, default);
        Assert.Equal(["Sales"], scan.Links.Select(l => l.Relative));
    }

    // \\srv\share is D:\Share on the server. Data\Proj → D:\Proj, which holds Deep → E:\X;
    // Data\Back → D:\Share (itself); Root → D:\, which holds Share again; Sym is a symbolic link.
    private static readonly Dictionary<string, FolderEntry[]> Share = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"\\srv\share"] = [new("Data", false, false, false), new("Root", false, true, true)],
        [@"\\srv\share\Data"] = [new("Proj", false, true, true), new("Back", false, true, true), new("Sym", false, false, true)],
        [@"\\srv\share\Data\Proj"] = [new("Deep", false, true, true), new("Plain", false, false, false)],
        [@"\\srv\share\Data\Proj\Deep"] = [],
        [@"\\srv\share\Data\Proj\Plain"] = [],
        [@"\\srv\share\Root"] = [new("Share", false, false, false), new("Other", false, false, false)],
        [@"\\srv\share\Root\Other"] = [],
    };

    private static readonly Dictionary<string, string> Ids = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"\\srv\share"] = "S", [@"\\srv\share\Data"] = "D", [@"\\srv\share\Data\Proj"] = "P", [@"\\srv\share\Data\Back"] = "S",
        [@"\\srv\share\Data\Proj\Deep"] = "X", [@"\\srv\share\Data\Proj\Plain"] = "PP",
        [@"\\srv\share\Root"] = "ROOT", [@"\\srv\share\Root\Share"] = "S", [@"\\srv\share\Root\Other"] = "O",
    };

    private static IEnumerable<FolderEntry> ListShare(string path) =>
        Share.TryGetValue(path, out var f) ? f : throw new InvalidOperationException("Entered " + path);

    [Fact]
    public void Junctions_on_a_share_are_followed_but_never_round_in_a_loop()
    {
        var scan = LinkedFolders.Find(@"\\srv\share", ListShare, p => Ids.GetValueOrDefault(p), new Exclusions(Job(@"\\srv\share")), true, default);
        Assert.Equal([@"Root", @"Data\Proj", @"Data\Proj\Deep"], scan.Links.Select(l => l.Relative));
        Assert.All(scan.Links, l => Assert.Equal(LinkKind.Junction, l.Kind));
        // Back points at the share itself; behind Root (the drive) the share shows up again.
        Assert.Equal([@"Data\Back", @"Root\Share"], scan.Loops);
        Assert.False(scan.Truncated);
    }

    [Fact]
    public void A_depth_limit_stops_the_walk_but_keeps_the_links_up_to_it()
    {
        // Two levels: Data\Proj is found but not entered, so Deep (level 3) is not.
        var two = LinkedFolders.Find(@"\\srv\share", ListShare, p => Ids.GetValueOrDefault(p), new Exclusions(Job(@"\\srv\share")), true, default, maxDepth: 2);
        Assert.Equal([@"Root", @"Data\Proj"], two.Links.Select(l => l.Relative));
        Assert.Equal([@"Data\Back", @"Root\Share"], two.Loops);
        Assert.False(two.Truncated);

        // One level: neither Data nor Root is listed.
        var one = LinkedFolders.Find(@"\\srv\share", ListShare, p => Ids.GetValueOrDefault(p), new Exclusions(Job(@"\\srv\share")), true, default, maxDepth: 1);
        Assert.Equal([@"Root"], one.Links.Select(l => l.Relative));
        Assert.Empty(one.Loops);
    }

    [Fact]
    public void Many_threads_find_the_same_links_in_the_same_order()
    {
        var one = LinkedFolders.Find(@"\\srv\share", ListShare, p => Ids.GetValueOrDefault(p), new Exclusions(Job(@"\\srv\share")), true, default);
        var many = LinkedFolders.Find(@"\\srv\share", ListShare, p => Ids.GetValueOrDefault(p), new Exclusions(Job(@"\\srv\share")), true, default, threads: 8);
        Assert.Equal(one.Links, many.Links);
        Assert.Equal(one.Loops, many.Loops);
    }

    [Fact]
    public void The_namespace_lists_its_links_whatever_server_name_it_gives()
    {
        string[] entries =
        [
            @"\\CORP\dfs", @"\\CORP\dfs\Sales", @"\\CORP\dfs\Depts\IT", @"\\CORP\dfs\Old",
            @"\\CORP\dfs\_AGS-Copy-Versions\X", @"\\CORP\other\Sales",
        ];
        var scan = LinkedFolders.FromNamespace(@"\\corp.lan\dfs", entries, new Exclusions(Job(@"\\corp.lan\dfs", "Old")));
        Assert.Equal([@"Depts\IT", "Sales"], scan.Links.Select(l => l.Relative));
        Assert.Equal(@"\\corp.lan\dfs\Depts\IT", scan.Links[0].Path);
        Assert.All(scan.Links, l => Assert.Equal(LinkKind.Dfs, l.Kind));

        // A source below a namespace folder sees only the links under it.
        var depts = LinkedFolders.FromNamespace(@"\\corp.lan\dfs\Depts", entries, new Exclusions(Job(@"\\corp.lan\dfs\Depts")));
        Assert.Equal(["IT"], depts.Links.Select(l => l.Relative));
    }

    [Fact]
    public void Links_of_the_last_walk_are_kept_only_while_they_still_are_links()
    {
        LinkedFolder[] last =
        [
            new("Root", @"\\srv\share\Root", LinkKind.Junction),          // still a junction
            new(@"Data\Proj", @"\\srv\share\Data\Proj", LinkKind.Junction), // now a plain folder
            new(@"Data\Back", @"\\srv\share\Data\Back", LinkKind.Junction), // now points back at the share
            new("Gone", @"\\srv\share\Gone", LinkKind.Junction),          // removed
            new("Sales", @"\\srv\share\Sales", LinkKind.Dfs),             // still a DFS link
            new("Skip", @"\\srv\share\Skip", LinkKind.Junction),          // excluded since
        ];
        var entries = new Dictionary<string, FolderEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [@"\\srv\share\Root"] = new("Root", false, true, true),
            [@"\\srv\share\Data\Proj"] = new("Proj", false, false, false),
            [@"\\srv\share\Data\Back"] = new("Back", false, true, true),
            [@"\\srv\share\Sales"] = new("Sales", true, false, true),
            [@"\\srv\share\Skip"] = new("Skip", false, true, true),
        };
        var kept = LinkedFolders.Recheck(@"\\srv\share", last, p => entries.GetValueOrDefault(p), p => Ids.GetValueOrDefault(p),
            new Exclusions(Job(@"\\srv\share", "Skip")), true, default);
        Assert.Equal(["Root", "Sales"], kept.Select(l => l.Relative));

        // Junctions no longer followed: only the DFS link stays.
        var dfsOnly = LinkedFolders.Recheck(@"\\srv\share", last, p => entries.GetValueOrDefault(p), p => Ids.GetValueOrDefault(p),
            new Exclusions(Job(@"\\srv\share")), false, default);
        Assert.Equal(["Sales"], dfsOnly.Select(l => l.Relative));
    }

    [Fact]
    public void The_links_of_a_walk_are_saved_per_source()
    {
        var scan = LinkedFolders.Find(@"\\srv\share", ListShare, p => Ids.GetValueOrDefault(p), new Exclusions(Job(@"\\srv\share")), true, default);
        var cache = new LinkCache();
        cache.Put(@"\\srv\share\", scan, DateTime.UtcNow);
        cache.Put(@"\\srv\other", new LinkScan(), DateTime.UtcNow);
        cache.Put(@"\\SRV\share", scan, DateTime.UtcNow);
        var back = System.Text.Json.JsonSerializer.Deserialize<LinkCache>(System.Text.Json.JsonSerializer.Serialize(cache, Json.Options), Json.Options)!;
        Assert.Equal(2, back.Sources.Count);
        Assert.Equal(scan.Links, back.Get(@"\\srv\share")!.Links);
        Assert.Equal(scan.Loops, back.Get(@"\\srv\share")!.Loops);
        Assert.Empty(back.Get(@"\\srv\other")!.Links);
        Assert.Null(back.Get(@"\\srv\none"));
    }

    [Fact]
    public void Without_the_option_junctions_stay_skipped()
    {
        var scan = LinkedFolders.Find(@"\\srv\share", ListShare, null, new Exclusions(Job(@"\\srv\share")), false, default);
        Assert.Empty(scan.Links);
        Assert.Empty(scan.Loops);
    }

    [Fact]
    public void The_option_applies_to_shares_and_needs_junctions_skipped_by_robocopy()
    {
        var o = new CopyOptions();
        Assert.True(o.FollowsJunctionsOn(new JobEndpoint { Path = @"\\srv\share" }));
        Assert.False(o.FollowsJunctionsOn(new JobEndpoint { Path = @"D:\Data" }));
        o.SkipJunctions = false; // robocopy follows everything itself then
        Assert.False(o.FollowsJunctionsOn(new JobEndpoint { Path = @"\\srv\share" }));
    }

    [Fact]
    public void Each_link_is_copied_into_its_place_and_kept_out_of_the_rest()
    {
        var job = Job(@"\\srv\share");
        var proj = new LinkedFolder(@"Data\Proj", @"\\srv\share\Data\Proj", LinkKind.Junction);
        var deep = new LinkedFolder(@"Data\Proj\Deep", @"\\srv\share\Data\Proj\Deep", LinkKind.Junction);
        LinkedFolder[] links = [proj, deep];

        var rest = LinkedFolders.WithoutLinks(job, links);
        Assert.Contains(@"\\srv\share\Data\Proj", rest.Options.ExcludeDirectories);
        // Excluded in the destination too, so a mirror of the rest never deletes the link's copy.
        Assert.Contains(@"E:\Backup\Data\Proj", rest.Options.ExcludeDirectories);
        Assert.DoesNotContain(@"E:\Backup\Data\Proj\Deep", rest.Options.ExcludeDirectories); // behind Proj, skipped with it
        var args = RobocopyArgs.Build(rest, @"\\srv\share", @"E:\Backup", RobocopyPass.Full, false, null, false);
        Assert.Contains(@"E:\Backup\Data\Proj", args);
        Assert.Contains("/XJ", args);

        var part = LinkedFolders.ForLink(job, proj, links);
        Assert.Equal(@"\\srv\share\Data\Proj", part.Source.Path);
        Assert.Equal(@"E:\Backup\Data\Proj", part.Destination.Path);
        Assert.Contains(@"E:\Backup\Data\Proj\Deep", part.Options.ExcludeDirectories);
        Assert.Empty(job.Options.ExcludeDirectories); // the job itself is unchanged
    }
}

public sealed class MultiSourceEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agscopy-multi-" + Guid.NewGuid().ToString("N"));
    private string Dst => Path.Combine(_root, "dst");
    private static readonly DateTime T0 = new(2026, 1, 10, 8, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string P(string rel) => Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));

    private void Write(string rel, string text, DateTime? time = null)
    {
        var p = P(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        File.SetLastWriteTimeUtc(p, time ?? T0);
    }

    private static async Task Copy(Func<ISourceStore> source, string target, CopyMode mode, DateTime start)
    {
        var ex = new Exclusions(new CopyJob());
        SyncPlan plan;
        using (var s = source())
        using (var t = new LocalTarget(target))
            plan = SyncPlan.Build(SyncEngine.Scan(s, ex, false, default), t.Exists() ? SyncEngine.Scan(t, ex, true, default) : null, false);
        var settings = new SyncSettings { Mode = mode, KeepVersions = true, Threads = 2, Retries = 0, StartLocal = start };
        await SyncEngine.RunAsync(plan, source, () => new LocalTarget(target), settings, new ProgressCounter("x"), _ => { }, default);
    }

    [Fact]
    public async Task Links_mounted_in_a_source_are_copied_in_place()
    {
        Write("ns/readme.txt", "root");
        Write("ns/Depts/plain.txt", "plain");
        Write("target/it.txt", "it");
        Write("target/sub/deep.txt", "deep");
        // On the share the link is a reparse point, which the source itself leaves out.
        Directory.CreateSymbolicLink(P("ns/Depts/IT"), P("target"));

        ISourceStore Open() => new MountedSource(new LocalSource(P("ns")), new Dictionary<string, ISourceStore> { [@"Depts\IT"] = new LocalSource(P("target")) });
        await Copy(Open, Dst, CopyMode.Mirror, new DateTime(2026, 2, 1, 22, 0, 0));

        Assert.Equal("root", File.ReadAllText(Path.Combine(Dst, "readme.txt")));
        Assert.Equal("it", File.ReadAllText(Path.Combine(Dst, "Depts", "IT", "it.txt")));
        Assert.Equal("deep", File.ReadAllText(Path.Combine(Dst, "Depts", "IT", "sub", "deep.txt")));
        using var s = Open();
        Assert.Single(s.List("Depts"), e => e.Name == "IT");
    }

    [Fact]
    public async Task Links_behind_links_are_mounted_too()
    {
        Write("share/top.txt", "top");
        Write("proj/p.txt", "p");
        Write("deep/d.txt", "d");
        Directory.CreateSymbolicLink(P("share/Proj"), P("proj"));
        Directory.CreateSymbolicLink(P("proj/Deep"), P("deep"));

        ISourceStore Open() => new MountedSource(new LocalSource(P("share")), new Dictionary<string, ISourceStore>
        {
            ["Proj"] = new LocalSource(P("proj")),
            [@"Proj\Deep"] = new LocalSource(P("deep")),
        });
        await Copy(Open, Dst, CopyMode.Mirror, new DateTime(2026, 2, 1, 22, 0, 0));

        Assert.Equal("p", File.ReadAllText(Path.Combine(Dst, "Proj", "p.txt")));
        Assert.Equal("d", File.ReadAllText(Path.Combine(Dst, "Proj", "Deep", "d.txt")));
    }

    [Fact]
    public async Task A_full_destination_gets_room_before_the_copy()
    {
        Write("src/new.bin", new string('n', 3000));
        Write("dst/old.bin", new string('o', 3000));
        var log = new List<string>();
        var ex = new Exclusions(new CopyJob());
        using (var s = new LocalSource(P("src")))
        using (var t = new LocalTarget(P("dst")))
        {
            var plan = SyncPlan.Build(SyncEngine.Scan(s, ex, false, default), SyncEngine.Scan(t, ex, true, default), false);
            var settings = new SyncSettings { Mode = CopyMode.Mirror, Threads = 1, Retries = 0, ExtrasFirst = true };
            var result = await SyncEngine.RunAsync(plan, () => new LocalSource(P("src")), () => new LocalTarget(P("dst")), settings, new ProgressCounter("x"),
                line => { lock (log) log.Add(line); }, default);
            Assert.Equal(1, result.DeletedCount);
        }
        // The extra went before the new file came.
        Assert.True(log.FindIndex(l => l.StartsWith("*EXTRA")) < log.FindIndex(l => l.StartsWith("New File")));
        Assert.False(File.Exists(P("dst/old.bin")));
        Assert.True(File.Exists(P("dst/new.bin")));
    }

    [Fact]
    public async Task One_restore_point_covers_every_source()
    {
        Write("a/x.txt", "x1");
        Write("b/y.txt", "y1");
        await Copy(() => new LocalSource(P("a")), Path.Combine(Dst, "A"), CopyMode.Mirror, new DateTime(2026, 2, 1, 22, 0, 0));
        await Copy(() => new LocalSource(P("b")), Path.Combine(Dst, "B"), CopyMode.Mirror, new DateTime(2026, 2, 1, 22, 0, 0));

        Write("a/x.txt", "x2", T0.AddDays(1));
        File.Delete(P("b/y.txt"));
        await Copy(() => new LocalSource(P("a")), Path.Combine(Dst, "A"), CopyMode.Mirror, new DateTime(2026, 2, 2, 22, 0, 0));
        await Copy(() => new LocalSource(P("b")), Path.Combine(Dst, "B"), CopyMode.Mirror, new DateTime(2026, 2, 2, 22, 0, 0));

        using var backup = new SourceFoldersBackup(new LocalSource(Dst), ["A", "B"]);
        var browser = new RestoreBrowser(backup);
        Assert.Equal([null, "2026-02-02_220000"], browser.Points().Select(p => p.Stamp));
        Assert.Equal(["A", "B"], browser.List(RestorePoint.Current, "").Select(i => i.Name));

        var target = P("restore");
        var before = browser.Points()[1];
        var r = browser.Restore(before, ["A", "B"], target, false, new ProgressCounter("x"), _ => { }, default);
        Assert.Empty(r.Errors);
        Assert.Equal("x1", File.ReadAllText(Path.Combine(target, "A", "x.txt")));
        Assert.Equal("y1", File.ReadAllText(Path.Combine(target, "B", "y.txt")));

        var now = P("now");
        browser.Restore(RestorePoint.Current, ["A/x.txt"], now, false, new ProgressCounter("x"), _ => { }, default);
        Assert.Equal("x2", File.ReadAllText(Path.Combine(now, "A", "x.txt")));
    }
}

public class InterruptionTests
{
    [Fact]
    public void A_crashed_run_starts_again_but_not_forever()
    {
        var job = Guid.NewGuid();
        var state = new Interruptions();
        Assert.True(state.Interrupted(job));
        Assert.True(state.Interrupted(job)); // the resumed run crashed too
        Assert.False(state.Interrupted(job)); // a third time in a row: it waits for its schedule

        // A run that ends by itself, whatever the outcome, starts the count again.
        Assert.True(state.Finished(job));
        Assert.True(state.Interrupted(job));
        Assert.False(state.Finished(Guid.NewGuid()));
    }

    [Fact]
    public void The_count_survives_a_restart()
    {
        var state = new Interruptions();
        var job = Guid.NewGuid();
        state.Interrupted(job);
        var saved = JsonSerializer.Deserialize<Interruptions>(JsonSerializer.Serialize(state, Json.Options), Json.Options)!;
        Assert.Equal(1, saved.Counts[job]);
    }
}
