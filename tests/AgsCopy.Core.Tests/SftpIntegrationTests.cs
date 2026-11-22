using AgsCopy.Core.Ipc;
using AgsCopy.Core.Models;
using AgsCopy.Core.Robocopy;
using AgsCopy.Core.Sync;

namespace AgsCopy.Core.Tests;

/// <summary>Runs only when AGSCOPY_SFTP=host:port:user:password:/writable/dir points at a test server (CI starts one).</summary>
public sealed class SftpFactAttribute : FactAttribute
{
    public SftpFactAttribute()
    {
        if (SftpServer.Settings == null) Skip = "AGSCOPY_SFTP not set: no SFTP test server";
    }
}

public static class SftpServer
{
    public static readonly string[]? Settings = Environment.GetEnvironmentVariable("AGSCOPY_SFTP")?.Split(':', 5);

    public static JobEndpoint Endpoint(string folder, string? hostKey = null)
    {
        var s = Settings!;
        return new JobEndpoint
        {
            Path = $"sftp://{s[0]}:{s[1]}{s[4].TrimEnd('/')}/{folder}",
            Credential = new StoredCredential { UserName = s[2], Password = s[3] },
            HostKey = hostKey,
        };
    }
}

public sealed class SftpIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agscopy-sftp-" + Guid.NewGuid().ToString("N"));
    private readonly string _folder = "t" + Guid.NewGuid().ToString("N")[..8];
    private static readonly DateTime T0 = new(2026, 1, 10, 8, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private void Write(string rel, string text, DateTime time)
    {
        var p = Path.Combine(_root, "src", rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        File.SetLastWriteTimeUtc(p, time);
    }

    private string HostKey()
    {
        using var c = Sftp.Connect(SftpServer.Endpoint(_folder), null, null, "en", out var key);
        Assert.NotNull(key);
        Assert.StartsWith("SHA256:", key);
        return key;
    }

    [SftpFact]
    public void A_different_host_key_is_refused()
    {
        HostKey();
        var e = Assert.Throws<ServiceException>(() => Sftp.Connect(SftpServer.Endpoint(_folder), null, "SHA256:not-this-one", "en", out _));
        Assert.Contains("SHA256:not-this-one", e.Message);
    }

    [SftpFact]
    public async Task Backs_up_to_sftp_mirrors_keeps_versions_and_restores()
    {
        var key = HostKey();
        var job = new CopyJob
        {
            Source = new JobEndpoint { Path = Path.Combine(_root, "src") },
            Destination = SftpServer.Endpoint(_folder, key),
            Mode = CopyMode.Mirror,
            Options = new CopyOptions { Compress = true, KeepVersions = true },
        };
        Write("a.txt", new string('a', 4000), T0);
        Write("docs/b.txt", "b1", T0);
        Write("photo.jpg", "jpeg", T0);

        async Task<(SyncPlan, SyncResult)> Run(DateTime start)
        {
            ITargetStore Target() => Sftp.Open(job.Destination, null, "en");
            ISourceStore Source() => new LocalSource(job.Source.Path);
            SyncPlan plan;
            using (var s = Source())
            using (var t = Target())
                plan = SyncPlan.Build(SyncEngine.Scan(s, new Exclusions(job), false, default),
                    t.Exists() ? SyncEngine.Scan(t, new Exclusions(job), true, default) : null, true);
            var result = await SyncEngine.RunAsync(plan, Source, Target, new SyncSettings
            {
                Mode = job.Mode, Compress = true, KeepVersions = true, StartLocal = start, Threads = 3, Retries = 0,
            }, new ProgressCounter("x"), _ => { }, default);
            Assert.Empty(result.Errors);
            return (plan, result);
        }

        var (_, first) = await Run(new DateTime(2026, 2, 1, 22, 0, 0));
        Assert.Equal(3, first.FilesCopied);
        Assert.Equal(2, first.FilesCompressed);

        var (again, _) = await Run(new DateTime(2026, 2, 1, 23, 0, 0));
        Assert.Empty(again.Copies);

        Write("a.txt", "changed", T0.AddDays(1));
        File.Delete(Path.Combine(_root, "src", "docs", "b.txt"));
        var (_, second) = await Run(new DateTime(2026, 2, 2, 22, 0, 0));
        Assert.Equal(2, second.ArchivedCount);

        using var backup = Sftp.Open(job.Destination, null, "en");
        var browser = new RestoreBrowser(backup);
        var points = browser.Points();
        Assert.Equal([null, "2026-02-02_220000"], points.Select(p => p.Stamp));

        var target = Path.Combine(_root, "restore");
        var restored = browser.Restore(points[1], ["a.txt", "docs"], target, false, new ProgressCounter("x"), _ => { }, default);
        Assert.Empty(restored.Errors);
        Assert.Equal(new string('a', 4000), File.ReadAllText(Path.Combine(target, "a.txt")));
        Assert.Equal("b1", File.ReadAllText(Path.Combine(target, "docs", "b.txt")));
        Assert.Equal(T0, File.GetLastWriteTimeUtc(Path.Combine(target, "a.txt")));

        backup.Delete("", true);
    }

    [SftpFact]
    public async Task Copies_from_sftp_to_a_local_folder()
    {
        var key = HostKey();
        var remote = SftpServer.Endpoint(_folder, key);
        using (var t = Sftp.Open(remote, null, "en"))
        {
            t.CreateDirectory("sub");
            using (var w = t.Create("sub/r.txt")) w.Write("remote"u8);
            t.SetModified("sub/r.txt", T0);
        }
        var dst = Path.Combine(_root, "dst");
        ISourceStore Source() => Sftp.Open(remote, null, "en");
        SyncPlan plan;
        using (var s = Source()) plan = SyncPlan.Build(SyncEngine.Scan(s, new Exclusions(new CopyJob()), false, default), null, false);
        var r = await SyncEngine.RunAsync(plan, Source, () => new LocalTarget(dst), new SyncSettings { Threads = 2, Retries = 0 }, new ProgressCounter("x"), _ => { }, default);
        Assert.Empty(r.Errors);
        Assert.Equal("remote", File.ReadAllText(Path.Combine(dst, "sub", "r.txt")));
        Assert.Equal(T0, File.GetLastWriteTimeUtc(Path.Combine(dst, "sub", "r.txt")));
        using (var t = Sftp.Open(remote, null, "en"))
        {
            Assert.Equal("remote", new StreamReader(t.OpenRead("sub/r.txt")).ReadToEnd());
            t.Delete("", true);
        }
    }
}
