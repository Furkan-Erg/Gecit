using System.Text.Json;

namespace Gecit;

sealed class Settings
{
    public string PresetId { get; set; } = Dpi.DpiPresets.Default.Id;

    // What the user last chose with the main toggle; restored when the app starts.
    public bool DpiWanted { get; set; }

    public bool BrowserAdBlock { get; set; } = true;
    public string BrowserSearch { get; set; } = "duckduckgo";

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
