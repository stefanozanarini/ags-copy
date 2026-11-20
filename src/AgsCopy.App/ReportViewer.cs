using System.Diagnostics;
using AgsCopy.Core.Ipc;

namespace AgsCopy.App;

/// <summary>
/// Reports live in %ProgramData%\AGS-Copy, readable by administrators only, while the browser
/// runs as the plain user. They are fetched from the service and written to the user's temp
/// folder, then opened there.
/// </summary>
public static class ReportViewer
{
    private static string Folder => Path.Combine(Path.GetTempPath(), "AGS-Copy-Reports");

    /// <summary>The index of all jobs with the run reports it links to.</summary>
    public static async Task OpenIndexAsync()
    {
        var bundle = await PipeClient.CallAsync<ReportBundleResponse>(Commands.ReportBundle, timeout: TimeSpan.FromMinutes(1));
        if (Directory.Exists(Folder)) Directory.Delete(Folder, true);
        foreach (var (relative, html) in bundle?.Files ?? [])
        {
            // Paths come from the service: keep them inside the folder all the same.
            var path = Path.GetFullPath(Path.Combine(Folder, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(Folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, html);
        }
        Open(Path.Combine(Folder, "index.html"));
    }

    /// <returns>False when the run has no report.</returns>
    public static async Task<bool> OpenRunAsync(Guid jobId, Guid runId)
    {
        var r = await PipeClient.CallAsync<ReportResponse>(Commands.RunReport, new LogRequest { JobId = jobId, RunId = runId });
        if (string.IsNullOrEmpty(r?.Html)) return false;
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, $"run-{runId:N}.html");
        await File.WriteAllTextAsync(path, r.Html);
        Open(path);
        return true;
    }

    private static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}
