using System.Text.Json;

namespace PCMon.Services;

public sealed class AppSettings
{
    public double Width { get; set; } = 1100;
    public double Height { get; set; } = 720;
    public bool Maximized { get; set; }

    /// <summary>"System", "Dark" or "Light".</summary>
    public string Theme { get; set; } = "System";

    public int UnusedDays { get; set; } = 90;
    public bool IncludeUnknownUsage { get; set; } = true;
}

/// <summary>The only file PCMon writes besides its log (SRS NFR-2).</summary>
static class SettingsStore
{
    static string FilePath => Path.Combine(Log.Folder, "settings.json");
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error("settings load", ex);
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Log.Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception ex)
        {
            Log.Error("settings save", ex);
        }
    }
}
