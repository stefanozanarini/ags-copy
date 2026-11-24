using AgsCopy.Core.Models;
using AgsCopy.Core.Robocopy;
using AgsCopy.Core.Sync;

namespace AgsCopy.Core.Tests;

/// <summary>The copy engine reading both trees while it copies, with the destination index.</summary>
public sealed class StreamingSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agscopy-stream-" + Guid.NewGuid().ToString("N"));
    private string Src => Path.Combine(_root, "src");
    private string Dst => Path.Combine(_root, "dst");
    private string IndexFile => Path.Combine(_root, "index.gz");
    private static readonly DateTime T0 = new(2026, 1, 10, 8, 0, 0, DateTimeKind.Utc);

    public StreamingSyncTests() => Directory.CreateDirectory(Src);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static void Write(string root, string rel, string text, DateTime? time = null)
    {
        var p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        File.SetLastWriteTimeUtc(p, time ?? T0);
    }

    private CopyJob Job(CopyMode mode = CopyMode.Copy, bool compress = false) => new()
    {
        Source = new JobEndpoint { Path = Src },
        Destination = new JobEndpoint { Path = Dst },
        Mode = mode,
        Options = new CopyOptions { Compress = compress, Retries = 0 },
    };

    /// <summary>A run as the service makes it: the index loaded, used (or checked in full) and saved.</summary>
    private async Task<SyncResult> Run(CopyJob job, bool extrasFirst = false, Func<ISourceStore>? source = null, DateTime? now = null, bool versions = false)
    {
        var index = DestinationIndex.Load(IndexFile) ?? new DestinationIndex();
        var walk = new SyncWalk { Exclusions = new Exclusions(job), Threads = 4, Index = index, Verify = index.NeedsVerify(now ?? DateTime.UtcNow) };
        var result = await SyncEngine.RunAsync(walk, source ?? (() => new LocalSource(Src)), () => new LocalTarget(Dst), new SyncSettings
        {
            Mode = job.Mode, Compress = job.Options.Compress, Threads = 3, Retries = 0, ExtrasFirst = extrasFirst, KeepVersions = versions,
            StartLocal = new DateTime(2026, 2, 1, 22, 0, 0),
        }, new ProgressCounter("x"), _ => { }, default);
        Assert.True(walk.Progress.Done);
        index.Save(IndexFile);
        return result;
    }

    private void Tree(int folders, int files)
    {
        for (var d = 0; d < folders; d++)
            for (var f = 0; f < files; f++)
                Write(Src, $"d{d % 5}/sub{d}/f{f}.txt", $"{d}-{f}");
    }

    [Fact]
    public async Task Copies_everything_then_reads_the_destination_from_the_index()
    {
        Tree(30, 4);
        Write(Src, "docs/report.txt", new string('a', 5000));

        var first = await Run(Job(compress: true));
        Assert.Equal(121, first.FilesCopied);
        Assert.True(first.DestinationVerified);
        Assert.True(File.Exists(Path.Combine(Dst, "docs", "report.txt" + Compression.Suffix)));

        var second = await Run(Job(compress: true));
        Assert.Equal(0, second.FilesCopied);
        Assert.Equal(121, second.FilesSkipped);
        Assert.False(second.DestinationVerified);
        Assert.Equal(0, second.DestinationFoldersRead);
        Assert.Equal(first.FoldersRead, second.FoldersRead);

        // A file changed in the source is copied again from the index alone.
        Write(Src, "d1/sub1/f0.txt", "changed", T0.AddHours(1));
        Write(Src, "new/one.txt", "1");
        var third = await Run(Job(compress: true));
        Assert.Equal(2, third.FilesCopied);
        Assert.Equal("changed", Gunzip(Path.Combine(Dst, "d1", "sub1", "f0.txt" + Compression.Suffix)));
    }

    private static string Gunzip(string path)
    {
        using var z = new System.IO.Compression.GZipStream(File.OpenRead(path), System.IO.Compression.CompressionMode.Decompress);
        return new StreamReader(z).ReadToEnd();
    }

    [Fact]
    public async Task A_file_deleted_from_the_destination_comes_back_at_the_full_check()
    {
        Write(Src, "a/x.txt", "x");
        Write(Src, "a/y.txt", "y");
        await Run(Job());
        File.Delete(Path.Combine(Dst, "a", "x.txt"));

        var trusted = await Run(Job());
        Assert.Equal(0, trusted.FilesCopied);

        var check = await Run(Job(), now: DateTime.UtcNow.AddDays(DestinationIndex.VerifyDays + 1));
        Assert.True(check.DestinationVerified);
        Assert.Equal(1, check.FilesCopied);
        Assert.True(File.Exists(Path.Combine(Dst, "a", "x.txt")));
    }

    [Fact]
    public async Task An_emptied_destination_is_read_in_full()
    {
        Write(Src, "a/x.txt", "x");
        Write(Src, "b.txt", "b");
        await Run(Job());
        Directory.Delete(Dst, true);
        Directory.CreateDirectory(Dst);

        var r = await Run(Job());
        Assert.True(r.DestinationVerified);
        Assert.Equal(2, r.FilesCopied);
    }

    [Fact]
    public async Task Mirror_deletes_extras_and_the_index_forgets_them()
    {
        Write(Src, "a.txt", "a");
        Write(Src, "keep/k.txt", "k");
        Write(Dst, "old.txt", "old");
        Write(Dst, "gone/x.txt", "x");
        Write(Dst, "gone/deep/y.txt", "y");

        var copy = await Run(Job(CopyMode.Copy));
        Assert.Equal(3, copy.FilesExtra);
        Assert.Equal(1, copy.DirsExtra);
        Assert.True(File.Exists(Path.Combine(Dst, "old.txt")));

        // From the index this time: the extras are still known.
        var mirror = await Run(Job(CopyMode.Mirror));
        Assert.False(mirror.DestinationVerified);
        Assert.Equal(2, mirror.DeletedCount);
        Assert.Equal(3, mirror.FilesExtra);
        Assert.False(File.Exists(Path.Combine(Dst, "old.txt")));
        Assert.False(Directory.Exists(Path.Combine(Dst, "gone")));

        var again = await Run(Job(CopyMode.Mirror));
        Assert.False(again.DestinationVerified);
        Assert.Equal(0, again.FilesExtra + again.DeletedCount + again.FilesCopied);
    }

    [Fact]
    public async Task A_file_the_index_does_not_know_is_kept_as_a_version()
    {
        Write(Src, "a/x.txt", "x");
        await Run(Job(), versions: true);
        Write(Dst, "a/y.txt", "put there by hand");
        Write(Src, "a/y.txt", "from the source", T0.AddHours(1));

        var r = await Run(Job(), versions: true);
        Assert.False(r.DestinationVerified);
        Assert.Equal(1, r.ArchivedCount);
        Assert.Equal("put there by hand", File.ReadAllText(Path.Combine(Dst, Versions.FolderName, "2026-02-01_220000", "a", "y.txt")));
        Assert.Equal("from the source", File.ReadAllText(Path.Combine(Dst, "a", "y.txt")));
    }

    [Fact]
    public async Task Extras_first_deletes_before_copying()
    {
        Tree(10, 2);
        Write(Dst, "old/big.bin", "old");
        var r = await Run(Job(CopyMode.Mirror), extrasFirst: true);
        Assert.Equal(20, r.FilesCopied);
        Assert.Equal(1, r.DeletedCount);
        Assert.False(Directory.Exists(Path.Combine(Dst, "old")));
    }

    [Fact]
    public async Task A_folder_that_cannot_be_read_is_reported_and_the_rest_copied()
    {
        Write(Src, "ok/a.txt", "a");
        Write(Src, "broken/b.txt", "b");
        Write(Dst, "broken/keep.txt", "keep");
        var r = await Run(Job(CopyMode.Mirror), source: () => new FailingSource(Src, "broken"));
        Assert.Equal(1, r.FilesCopied);
        Assert.Contains(r.Errors, e => e.Action == "Listing Directory");
        // Never taken for an extra: what the source could not show stays.
        Assert.True(File.Exists(Path.Combine(Dst, "broken", "keep.txt")));
        Assert.Equal(0, r.DeletedCount);
    }

    private sealed class FailingSource(string root, string broken) : LocalSource(root), ISourceStore
    {
        public new IReadOnlyList<SyncEntry> List(string relative) => relative == broken ? throw new IOException("Access denied") : base.List(relative);
    }

    [Fact]
    public void Index_survives_a_round_trip_and_rejects_garbage()
    {
        var index = new DestinationIndex { VerifiedUtc = T0 };
        index.SetFolder("", [new SyncEntry("a", true, 0, default), new SyncEntry("b.txt", false, 12, T0)]);
        index.SetFolder("a", [new SyncEntry("a/c.txt", false, 3, T0.AddSeconds(1))]);
        index.SetFolder("bad", [new SyncEntry("bad/x\ny", false, 1, T0)]);
        index.Put(new SyncEntry("a/d.txt", false, 4, T0));
        index.Save(IndexFile);

        var loaded = DestinationIndex.Load(IndexFile)!;
        Assert.Equal(T0, loaded.VerifiedUtc);
        Assert.Equal(2, loaded.FolderCount);
        Assert.Null(loaded.Folder("bad"));
        Assert.Equal(["a/c.txt", "a/d.txt"], loaded.Folder("a")!.Select(e => e.Relative).Order());
        Assert.Equal(12, loaded.Folder("")!.Single(e => e.Name == "b.txt").Size);

        loaded.Remove("a", true);
        Assert.Null(loaded.Folder("a"));
        Assert.Equal(["b.txt"], loaded.Folder("")!.Select(e => e.Relative));

        File.WriteAllText(IndexFile, "not an index");
        Assert.Null(DestinationIndex.Load(IndexFile));
    }

    [Fact]
    public void Parallel_walk_visits_every_folder_and_tells_the_first_error()
    {
        Tree(40, 1);
        var all = SyncEngine.Scan(() => new LocalSource(Src), 8, new Exclusions(Job()), false, default);
        using (var s = new LocalSource(Src))
            Assert.Equal(SyncEngine.Scan(s, new Exclusions(Job()), false, default).Select(e => e.Relative).Order(), all.Select(e => e.Relative).Order());

        var e = Assert.Throws<IOException>(() => ParallelWalk.Run(0, 4, () => new Nothing(), (_, n, add) =>
        {
            if (n == 50) throw new IOException("boom");
            if (n < 100) add(n + 1);
        }, default));
        Assert.Equal("boom", e.Message);
    }

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
