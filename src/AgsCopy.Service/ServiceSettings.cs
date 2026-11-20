using System.Text.Json;
using AgsCopy.Core;

namespace AgsCopy.Service;

public sealed class ServiceSettings
{
    public bool SchedulerPaused { get; set; }

    /// <summary>Language of reports and run notes: the last one an administrator used in the GUI.</summary>
    public string Language { get; set; } = "it";

    public static ServiceSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.ServiceSettingsFile))
                return JsonSerializer.Deserialize<ServiceSettings>(File.ReadAllText(AppPaths.ServiceSettingsFile), Json.Options) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
        }
        return new ServiceSettings();
    }

    public void Save() => DataDirectory.WriteAtomic(AppPaths.ServiceSettingsFile, JsonSerializer.Serialize(this, Json.Indented));
}
