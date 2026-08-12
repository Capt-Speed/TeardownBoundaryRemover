using System.Text.Json;

namespace TeardownBoundaryRemover.Services;

internal sealed class AppSettings
{
    public List<string> ExtraLocations { get; set; } = [];
    public bool ScanBuiltInMaps { get; set; } = true;
    public bool ScanWorkshopMaps { get; set; } = true;
    public bool ScanLocalMaps { get; set; } = true;
}

internal static class SettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TeardownBoundaryRemover");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var temporaryPath = SettingsPath + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(settings, SerializerOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // A stale temporary settings file is harmless and may be cleaned up next time.
            }
        }
    }
}
