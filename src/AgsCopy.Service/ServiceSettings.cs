using System.Text.Json;
using AgsCopy.Core;

namespace AgsCopy.Service;

public sealed class ServiceSettings
{
    public bool SchedulerPaused { get; set; }

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
