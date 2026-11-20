namespace AgsCopy.Core;

public static class AppPaths
{
    public const string ServiceName = "AGS-Copy";

    /// <summary>%ProgramData%\AGS-Copy — jobs, history and logs, owned by the service.</summary>
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AGS-Copy");

    public static string JobsFile => Path.Combine(DataDir, "jobs.json");
    public static string ServiceSettingsFile => Path.Combine(DataDir, "service.json");
    public static string HistoryDir => Path.Combine(DataDir, "history");
    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string TempDir => Path.Combine(DataDir, "temp");

    public static string JobLogDir(Guid jobId) => Path.Combine(LogsDir, jobId.ToString("N"));

    public static string ReportsDir => Path.Combine(DataDir, "reports");

    public static string JobReportDir(Guid jobId) => Path.Combine(ReportsDir, jobId.ToString("N"));

    /// <summary>%AppData%\AGS-Copy — per-user GUI settings.</summary>
    public static string UserDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AGS-Copy");
}
