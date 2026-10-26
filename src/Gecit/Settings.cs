using System.Text.Json;

namespace Gecit;

sealed class Settings
{
    public string PresetId { get; set; } = Dpi.DpiPresets.Default.Id;

    // What the user last chose with the main toggle; restored when the app starts.
    public bool DpiWanted { get; set; }

    public bool BrowserAdBlock { get; set; } = true;
    public string BrowserSearch { get; set; } = "duckduckgo";

    public GameModeSettings Game { get; set; } = new();

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new();
        }
        catch (JsonException) { }
        return new();
    }

    public void Save() => File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Json));
}

sealed class GameModeSettings
{
    public bool AutoDetect { get; set; } = true;          // start when a known/installed/added game runs
    public bool DetectFullscreen { get; set; } = true;    // ...or any Direct3D exclusive-fullscreen app
    public bool PowerPlan { get; set; } = true;
    public bool HighPriority { get; set; } = true;
    public bool RelaunchClosed { get; set; } = true;
    public List<string> CustomGames { get; set; } = [];
    // Process name -> "suspend" | "close"
    public Dictionary<string, string> Targets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
