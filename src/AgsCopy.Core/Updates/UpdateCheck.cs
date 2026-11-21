using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace AgsCopy.Core.Updates;

/// <summary>A release newer than the running program.</summary>
public sealed record UpdateInfo(Version Version, string PageUrl, string? MsiUrl, string? MsiName);

/// <summary>
/// Asks GitHub for the latest published release. Failures (offline, proxy, rate limit) are
/// silent: a missing update check must never get in the way of a backup.
/// </summary>
public static class UpdateCheck
{
    public const string Repository = "stefanozanarini/ags-copy";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";
    private const string LatestApi = "https://api.github.com/repos/" + Repository + "/releases/latest";

    public static Version Current { get; } = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    public static async Task<UpdateInfo?> FindAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AGS-Copy/" + Current);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var json = await http.GetStringAsync(LatestApi, ct);
            return Parse(json, Current);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>Reads a GitHub "latest release" response; null unless it is newer than <paramref name="current"/>.</summary>
    public static UpdateInfo? Parse(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        if (root.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
        if (!root.TryGetProperty("tag_name", out var tag) || ParseTag(tag.GetString()) is not { } version) return null;
        if (version <= Normalize(current)) return null;

        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage;
        string? msiUrl = null, msiName = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name == null || !name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) continue;
                msiName = name;
                msiUrl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                break;
            }
        }
        return new UpdateInfo(version, page, msiUrl, msiName);
    }

    /// <summary>"v1.2.3" or "1.2.3" → 1.2.3; anything else → null.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(s, out var v) ? Normalize(v) : null;
    }

    /// <summary>Compares on major.minor.build: assembly versions carry a revision of 0 or -1.</summary>
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>Downloads the setup package into the user's temp folder and returns its path.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, CancellationToken ct = default)
    {
        if (update.MsiUrl == null || update.MsiName == null) throw new InvalidOperationException("The release has no .msi package.");
        var dir = Path.Combine(Path.GetTempPath(), "AGS-Copy-Update");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, Path.GetFileName(update.MsiName));
        var partial = file + ".part";

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AGS-Copy/" + Current);
        using (var response = await http.GetAsync(update.MsiUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(partial);
            await src.CopyToAsync(dst, ct);
        }
        File.Move(partial, file, overwrite: true);
        return file;
    }
}
