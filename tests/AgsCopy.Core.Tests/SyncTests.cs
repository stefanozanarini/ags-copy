using System.IO.Compression;
using System.Security.Cryptography;
using AgsCopy.Core.Models;
using AgsCopy.Core.Robocopy;
using AgsCopy.Core.Sync;

namespace AgsCopy.Core.Tests;

public sealed class SyncEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agscopy-sync-" + Guid.NewGuid().ToString("N"));
    private string Src => Path.Combine(_root, "src");
    private string Dst => Path.Combine(_root, "dst");
    private static readonly DateTime T0 = new(2026, 1, 10, 8, 0, 0, DateTimeKind.Utc);

    public SyncEngineTests() => Directory.CreateDirectory(Src);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private void Write(string root, string rel, string text, DateTime? time = null)
    {
        var p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        File.SetLastWriteTimeUtc(p, time ?? T0);
    }

    private CopyJob Job(CopyMode mode = CopyMode.Copy, bool compress = false, bool versions = false) => new()
    {
        Source = new JobEndpoint { Path = Src },
        Destination = new JobEndpoint { Path = Dst },
        Mode = mode,
        Options = new CopyOptions { Compress = compress, KeepVersions = versions, Retries = 0 },
    };

    private async Task<(SyncPlan Plan, SyncResult Result)> Run(CopyJob job, DateTime? start = null)
    {
        var ex = new Exclusions(job);
        using var src = new LocalSource(Src);
        using var dst = new LocalTarget(Dst);
        var plan = SyncPlan.Build(SyncEngine.Scan(src, ex, false, default), dst.Exists() ? SyncEngine.Scan(dst, ex, true, default) : null, job.Options.Compress);
        var settings = new SyncSettings
        {
            Mode = job.Mode, Compress = job.Options.Compress, KeepVersions = job.Options.KeepVersions, Threads = 3, Retries = 0,
            StartLocal = start ?? new DateTime(2026, 2, 1, 22, 0, 0),
        };
        var result = await SyncEngine.RunAsync(plan, () => new LocalSource(Src), () => new LocalTarget(Dst), settings, new ProgressCounter("x"), _ => { }, default);
        return (plan, result);
    }

    private string Hash(string root) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("|",
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order()
            .Select(f => f + ":" + File.ReadAllText(f) + ":" + File.GetLastWriteTimeUtc(f).Ticks)))));

    [Fact]
    public async Task Copies_compresses_what_is_worth_it_and_then_has_nothing_to_do()
    {
        Write(Src, "docs/report.txt", new string('a', 5000));
        Write(Src, "photo.jpg", "not really a jpeg");
        var before = Hash(Src);

        var (_, r) = await Run(Job(compress: true));
        Assert.Equal(2, r.FilesCopied);
        Assert.Equal(1, r.FilesCompressed);
        var gz = Path.Combine(Dst, "docs", "report.txt" + Compression.Suffix);
        Assert.True(File.Exists(gz));
        Assert.True(new FileInfo(gz).Length < 5000);
        using (var z = new GZipStream(File.OpenRead(gz), CompressionMode.Decompress))
        using (var reader = new StreamReader(z))
            Assert.Equal(new string('a', 5000), reader.ReadToEnd());
        Assert.Equal(T0, File.GetLastWriteTimeUtc(gz));
        Assert.True(File.Exists(Path.Combine(Dst, "photo.jpg")));
        Assert.Empty(Directory.EnumerateFiles(Dst, "*" + SyncEngine.TempSuffix, SearchOption.AllDirectories));

        var (plan, again) = await Run(Job(compress: true));
        Assert.Empty(plan.Copies);
        Assert.Equal(2, again.FilesSkipped);
        Assert.Equal(before, Hash(Src));
    }

    [Fact]
    public async Task Mirror_deletes_extras_and_copy_keeps_them()
    {
        Write(Src, "a.txt", "a");
        Write(Dst, "old.txt", "old");
        Write(Dst, "gone/x.txt", "x");

        var (_, copy) = await Run(Job(CopyMode.Copy));
        Assert.Equal(2, copy.FilesExtra);
        Assert.True(File.Exists(Path.Combine(Dst, "old.txt")));

        var (_, mirror) = await Run(Job(CopyMode.Mirror));
        Assert.Equal(2, mirror.DeletedCount);
        Assert.False(File.Exists(Path.Combine(Dst, "old.txt")));
        Assert.False(Directory.Exists(Path.Combine(Dst, "gone")));
        Assert.True(File.Exists(Path.Combine(Dst, "a.txt")));
    }

    [Fact]
    public async Task Versions_keep_overwritten_and_deleted_files()
    {
        Write(Src, "a.txt", "new", T0.AddHours(1));
        Write(Dst, "a.txt", "old");
        Write(Dst, "b.txt", "extra");
        var before = Hash(Src);

        var (_, r) = await Run(Job(CopyMode.Mirror, versions: true));
        Assert.Equal(2, r.ArchivedCount);
        var folder = Path.Combine(Dst, Versions.FolderName, "2026-02-01_220000");
        Assert.Equal("old", File.ReadAllText(Path.Combine(folder, "a.txt")));
        Assert.Equal("extra", File.ReadAllText(Path.Combine(folder, "b.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(Dst, "a.txt")));
        Assert.Equal(before, Hash(Src));

        // The versions folder is never an extra, even for a mirror.
        var (plan, _) = await Run(Job(CopyMode.Mirror, versions: true));
        Assert.Empty(plan.Extras);
    }

    [Fact]
    public async Task Exclusions_are_neither_copied_nor_deleted()
    {
        Write(Src, "keep.txt", "k");
        Write(Src, "cache/big.bin", "c");
        Write(Src, "x.tmp", "t");
        Write(Dst, "cache/stale.bin", "s");
        var job = Job(CopyMode.Mirror);
        job.Options.ExcludeDirectories.Add("cache");
        job.Options.ExcludeFiles.Add("*.tmp");

        var (_, r) = await Run(job);
        Assert.Equal(1, r.FilesCopied);
        Assert.True(File.Exists(Path.Combine(Dst, "cache", "stale.bin")));
        Assert.False(File.Exists(Path.Combine(Dst, "x.tmp")));
    }

    private string Restored(string folder, string rel) => File.ReadAllText(Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public async Task Restores_files_as_they_were_before_each_run()
    {
        var job = Job(CopyMode.Mirror, compress: true, versions: true);
        Write(Src, "a.txt", "v1", T0);
        Write(Src, "docs/b.txt", "b1", T0);
        Write(Src, "dump.sql.gz", "not ours");
        await Run(job, new DateTime(2026, 2, 1, 22, 0, 0));

        Write(Src, "a.txt", "v2", T0.AddDays(1));
        File.Delete(Path.Combine(Src, "docs", "b.txt"));
        Write(Src, "c.txt", "c", T0.AddDays(1));
        await Run(job, new DateTime(2026, 2, 2, 22, 0, 0));

        Write(Src, "a.txt", "v3", T0.AddDays(2));
        await Run(job, new DateTime(2026, 2, 3, 22, 0, 0));
        var source = Hash(Src);

        using var backup = new LocalSource(Dst);
        var browser = new RestoreBrowser(backup);
        var points = browser.Points();
        Assert.Equal([null, "2026-02-03_220000", "2026-02-02_220000"], points.Select(p => p.Stamp));

        // The names show without the .gz the engine added; the source's own .gz keeps it.
        var top = browser.List(RestorePoint.Current, "").Select(i => i.Name).ToList();
        Assert.Contains("a.txt", top);
        Assert.Contains("dump.sql.gz", top);
        Assert.DoesNotContain(Versions.FolderName, top);

        string RestoreAt(RestorePoint p)
        {
            var target = Path.Combine(_root, "restore-" + (p.Stamp ?? "now"));
            var r = browser.Restore(p, ["a.txt", "docs", "dump.sql.gz"], target, false, new ProgressCounter("x"), _ => { }, default);
            Assert.Empty(r.Errors);
            return target;
        }

        var now = RestoreAt(points[0]);
        Assert.Equal("v3", Restored(now, "a.txt"));
        Assert.False(Directory.Exists(Path.Combine(now, "docs", "b.txt")) || File.Exists(Path.Combine(now, "docs", "b.txt")));
        Assert.Equal("not ours", Restored(now, "dump.sql.gz"));
        Assert.Equal(T0.AddDays(2), File.GetLastWriteTimeUtc(Path.Combine(now, "a.txt")));

        var beforeThird = RestoreAt(points[1]);
        Assert.Equal("v2", Restored(beforeThird, "a.txt"));

        var beforeSecond = RestoreAt(points[2]);
        Assert.Equal("v1", Restored(beforeSecond, "a.txt"));
        Assert.Equal("b1", Restored(beforeSecond, "docs/b.txt"));

        Assert.Equal(source, Hash(Src));
    }

    [Fact]
    public async Task Restore_keeps_existing_files_unless_asked()
    {
        Write(Src, "a.txt", "new");
        await Run(Job());
        var target = Path.Combine(_root, "restore");
        Write(target, "a.txt", "mine");
        using var backup = new LocalSource(Dst);
        var browser = new RestoreBrowser(backup);

        var kept = browser.Restore(RestorePoint.Current, ["a.txt"], target, false, new ProgressCounter("x"), _ => { }, default);
        Assert.Equal(1, kept.Skipped);
        Assert.Equal("mine", Restored(target, "a.txt"));

        browser.Restore(RestorePoint.Current, ["a.txt"], target, true, new ProgressCounter("x"), _ => { }, default);
        Assert.Equal("new", Restored(target, "a.txt"));
    }

    [Fact]
    public void Only_our_gzip_files_carry_the_mark()
    {
        using var ours = new MemoryStream();
        using (var w = AgsGzip.Create(ours)) w.Write("hello"u8);
        var bytes = ours.ToArray();
        Assert.True(AgsGzip.IsMarked(new MemoryStream(bytes)));
        using (var z = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress))
        using (var r = new StreamReader(z))
            Assert.Equal("hello", r.ReadToEnd());

        using var plain = new MemoryStream();
        using (var w = new GZipStream(plain, CompressionLevel.Fastest, leaveOpen: true)) w.Write("hello"u8);
        Assert.False(AgsGzip.IsMarked(new MemoryStream(plain.ToArray())));
        Assert.False(AgsGzip.IsMarked(new MemoryStream("not gzip at all"u8.ToArray())));
    }

    [Fact]
    public void A_name_already_taken_by_a_gz_in_the_source_stays_plain()
    {
        var src = new[] { new SyncEntry("a.txt", false, 10, T0), new SyncEntry("a.txt.gz", false, 5, T0) };
        var plan = SyncPlan.Build(src, null, compress: true);
        Assert.Contains(plan.Copies, c => c.Target == "a.txt" && !c.Compressed);
        Assert.Contains(plan.Copies, c => c.Target == "a.txt.gz" && !c.Compressed);
    }

    [Fact]
    public void A_name_with_no_room_for_the_suffix_stays_plain()
    {
        var fits = new string('a', 248) + ".txt";        // 252 + ".gz" = 255
        var full = new string('b', 249) + ".txt";        // 253 + ".gz" = 256
        var accented = new string('è', 126) + ".txt";    // 130 characters, 259 UTF-8 bytes with ".gz"
        var plan = SyncPlan.Build([new SyncEntry(fits, false, 1, T0), new SyncEntry(full, false, 1, T0), new SyncEntry("d/" + accented, false, 1, T0)], null, compress: true);
        Assert.Contains(plan.Copies, c => c.Target == fits + Compression.Suffix && c.Compressed);
        Assert.Contains(plan.Copies, c => c.Target == full && !c.Compressed);
        Assert.Contains(plan.Copies, c => c.Target == "d/" + accented && !c.Compressed);
    }

    [Theory]
    [InlineData(@"C:\data\file.txt", @"\\?\C:\data\file.txt")]
    [InlineData(@"\\server\share\dir\", @"\\?\UNC\server\share\dir")]
    [InlineData(@"\\?\C:\x", @"\\?\C:\x")]
    public void Long_path_prefix(string path, string expected) => Assert.Equal(expected, PathUtil.LongPath(path));

    [Fact]
    public void Times_within_two_seconds_are_equal()
    {
        var src = new[] { new SyncEntry("a", false, 10, T0) };
        Assert.Empty(SyncPlan.Build(src, [new SyncEntry("a", false, 10, T0.AddSeconds(1.5))], false).Copies);
        Assert.Single(SyncPlan.Build(src, [new SyncEntry("a", false, 10, T0.AddSeconds(3))], false).Copies);
        Assert.Single(SyncPlan.Build(src, [new SyncEntry("a", false, 11, T0)], false).Copies);
    }

    [Fact]
    public void Compressible_types()
    {
        Assert.True(Compression.IsCompressible("db.mdb"));
        Assert.True(Compression.IsCompressible("notes.txt"));
        Assert.True(Compression.IsCompressible("noextension"));
        Assert.False(Compression.IsCompressible("photo.JPG"));
        Assert.False(Compression.IsCompressible("offer.docx"));
        Assert.False(Compression.IsCompressible("backup.zip"));
    }
}

public class SftpPathTests
{
    [Theory]
    [InlineData("sftp://nas.local/backup/pc1", "nas.local", 22, "/backup/pc1")]
    [InlineData("sftp://nas.local:2222/backup/", "nas.local", 2222, "/backup")]
    [InlineData("SFTP://10.0.0.5", "10.0.0.5", 22, "/")]
    [InlineData("sftp://[fe80::1]:22/data", "fe80::1", 22, "/data")]
    public void Parses(string text, string host, int port, string path)
    {
        var l = SftpLocation.TryParse(text);
        Assert.NotNull(l);
        Assert.Equal((host, port, path), (l.Host, l.Port, l.RemotePath));
    }

    [Theory]
    [InlineData("sftp://user@nas/x")]
    [InlineData("sftp://nas:99999/x")]
    [InlineData("sftp:///x")]
    [InlineData("sftp://nas/a/../b")]
    public void Rejects(string text) => Assert.Null(SftpLocation.TryParse(text));

    [Fact]
    public void Path_helpers_keep_forward_slashes()
    {
        Assert.True(PathUtil.IsAbsolute("sftp://nas/backup"));
        Assert.Equal("sftp://nas/backup", PathUtil.NormalizeDir("sftp://nas/backup/"));
        Assert.Equal("sftp://nas/backup/pc1", PathUtil.Combine("sftp://nas/backup", "pc1"));
        Assert.Equal("pc1", PathUtil.LastName("sftp://nas/backup/pc1"));
        Assert.True(PathUtil.Overlaps("sftp://nas/backup", "sftp://nas/backup/pc1"));
        Assert.False(PathUtil.Overlaps("sftp://nas/backup", @"D:\backup"));
        Assert.Equal("/backup/a/b.txt", SftpLocation.TryParse("sftp://nas/backup")!.Resolve("a/b.txt"));
        Assert.Equal("/a", SftpLocation.TryParse("sftp://nas")!.Resolve("a"));
    }
}

public class SftpValidationTests
{
    private static CopyJob Job(StoredCredential? c, string? hostKey) => new()
    {
        Name = "x",
        Source = new JobEndpoint { Path = @"D:\Data" },
        Destination = new JobEndpoint { Path = "sftp://nas/backup", Credential = c, HostKey = hostKey },
    };

    [Fact]
    public void Needs_a_user_and_a_recorded_fingerprint()
    {
        Assert.Contains(Loc.Tl("en", "val.sftp.user.destination"), JobValidator.Validate(Job(null, null), "en"));
        Assert.Contains(Loc.Tl("en", "val.hostkey.destination"), JobValidator.Validate(Job(new StoredCredential { UserName = "u", Password = "p" }, null), "en"));
        Assert.Empty(JobValidator.Validate(Job(new StoredCredential { UserName = "u", Password = "p" }, "SHA256:abc"), "en"));
    }

    [Fact]
    public void A_private_key_needs_no_password()
    {
        Assert.Empty(JobValidator.Validate(Job(new StoredCredential { UserName = "u", PrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----" }, "SHA256:abc"), "en"));
        Assert.NotEmpty(JobValidator.Validate(Job(new StoredCredential { UserName = "u" }, "SHA256:abc"), "en"));
    }

    [Fact]
    public void Robocopy_arguments_do_not_apply()
    {
        var job = Job(new StoredCredential { UserName = "u", Password = "p" }, "SHA256:abc");
        job.Options.ExtraArguments = "/XA:H";
        Assert.Contains(Loc.Tl("en", "val.sftpargs"), JobValidator.Validate(job, "en"));
    }

    [Fact]
    public void The_summary_names_the_server_and_its_fingerprint()
    {
        var job = Job(new StoredCredential { UserName = "backup", HasPrivateKey = true }, "SHA256:abc");
        job.Options.Compress = true;
        var lang = Loc.Language;
        Loc.Language = "en";
        try
        {
            var text = string.Join("\n", JobDescriber.Describe(job).Select(l => l.Text));
            Assert.Contains("SHA256:abc", text);
            Assert.Contains("backup", text);
            Assert.Contains(".gz", text);
            Assert.DoesNotContain("[sum.", text);
        }
        finally
        {
            Loc.Language = lang;
        }
    }
}
