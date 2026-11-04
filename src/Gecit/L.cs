using System.Globalization;
using System.Text.Json;

namespace Gecit;

// UI language. Turkish is the source language and the Turkish text itself is the key into
// wwwroot/i18n/en.json (shared with the web UI), so a missing translation simply stays Turkish.
// Placeholders are "{name}": L.T("Oyun modu açık · {game}", ("game", name)).
static class L
{
    static string? lang;
    static readonly Lazy<(Dictionary<string, string> Map, string Json)> En = new(LoadEnglish);

    // "tr" | "en". Processes without a main window (elevated helper, boot measurement) read the setting here.
    public static string Lang => lang ??= Resolve(SavedSetting());

    public static bool IsEnglish => Lang == "en";

    // Settings.Language: "auto" (Windows display language) | "tr" | "en".
    public static void Set(string? setting) => lang = Resolve(setting);

    public static string Resolve(string? setting) => setting switch
    {
        "tr" => "tr",
        "en" => "en",
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? "tr" : "en",
    };

    public static CultureInfo Culture => CultureInfo.GetCultureInfo(IsEnglish ? "en-US" : "tr-TR");

    // Language name the Steam store APIs take.
    public static string SteamLanguage => IsEnglish ? "english" : "turkish";

    public static string T(string tr) =>
        IsEnglish && En.Value.Map.TryGetValue(tr, out string? en) ? en : tr;

    public static string T(string tr, params (string Name, object? Value)[] vars)
    {
        string s = T(tr);
        foreach (var (name, value) in vars)
            s = s.Replace("{" + name + "}", Convert.ToString(value, Culture), StringComparison.Ordinal);
        return s;
    }

    // Run in the web UI before any of its scripts (AddScriptToExecuteOnDocumentCreatedAsync), so the
    // page is translated before it's first shown.
    public static string WebScript() =>
        $"window.__gecitI18n={{lang:{JsonSerializer.Serialize(Lang)},dict:{(IsEnglish ? En.Value.Json : "{}")}}};";

    static string? SavedSetting()
    {
        try { return Settings.Load().Language; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static (Dictionary<string, string>, string) LoadEnglish()
    {
        try
        {
            string json = File.ReadAllText(Path.Combine(AppPaths.WebRoot, "i18n", "en.json"));
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
            // Re-serialized: the file goes into a script, so it must be plain JSON whatever it contains.
            return (map, JsonSerializer.Serialize(map));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return ([], "{}");
        }
    }
}
