using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Gecit.Game;

sealed record GameEntry(string Exe, string Name, string Source);   // Exe: lower-case process name, no ".exe"

sealed record AppCandidate(string Process, string Name, int MemoryMb, int Count);

static partial class GameCatalog
{
    // Process name -> title, for popular games that don't need a launcher scan.
    static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs2"] = "Counter-Strike 2",
        ["valorant-win64-shipping"] = "VALORANT",
        ["league of legends"] = "League of Legends",
        ["fortniteclient-win64-shipping"] = "Fortnite",
        ["robloxplayerbeta"] = "Roblox",
        ["gta5"] = "GTA V",
        ["gta5_enhanced"] = "GTA V Enhanced",
        ["r5apex"] = "Apex Legends",
        ["r5apex_dx12"] = "Apex Legends",
        ["dota2"] = "Dota 2",
        ["rocketleague"] = "Rocket League",
        ["eldenring"] = "Elden Ring",
        ["rdr2"] = "Red Dead Redemption 2",
        ["overwatch"] = "Overwatch 2",
        ["cod"] = "Call of Duty",
        ["tslgame"] = "PUBG",
        ["minecraft.windows"] = "Minecraft",
        ["eurotrucks2"] = "Euro Truck Simulator 2",
        ["forzahorizon5"] = "Forza Horizon 5",
        ["witcher3"] = "The Witcher 3",
        ["cyberpunk2077"] = "Cyberpunk 2077",
        ["rainbowsix"] = "Rainbow Six Siege",
        ["destiny2"] = "Destiny 2",
        ["genshinimpact"] = "Genshin Impact",
        ["marvel-win64-shipping"] = "Marvel Rivals",
        ["metin2client"] = "Metin2",
        ["knightonline"] = "Knight Online",
        ["zula"] = "Zula",
        ["fc25"] = "EA SPORTS FC 25",
        ["fc26"] = "EA SPORTS FC 26",
    };

    // Never offered for freezing: Windows itself, Geçit, and things the user is likely typing into right now.
    static readonly HashSet<string> NeverTouch = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "csrss", "wininit", "winlogon", "services", "lsass", "smss", "svchost", "dwm", "explorer",
        "fontdrvhost", "sihost", "ctfmon", "taskhostw", "RuntimeBroker", "ShellExperienceHost", "StartMenuExperienceHost",
        "SearchApp", "SearchHost", "TextInputHost", "ApplicationFrameHost", "SystemSettings", "LockApp", "audiodg",
        "conhost", "dllhost", "smartscreen", "SecurityHealthSystray", "Gecit", "Gecit.Dns", "msedgewebview2",
        "WindowsTerminal", "OpenConsole", "cmd", "pwsh", "powershell", "claude", "node", "dotnet", "VBCSCompiler",
        "Taskmgr", "mmc", "WmiPrvSE", "spoolsv", "NVDisplay.Container",
    };

    // Background apps worth suggesting even without a window.
    static readonly HashSet<string> BackgroundSuggestions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Rainmeter", "Lively", "Lively.UI", "OneDrive", "Teams", "ms-teams", "Spotify", "steamwebhelper",
        "EpicGamesLauncher", "EpicWebHelper", "Flow.Launcher", "Everything", "NVIDIA Share", "copilotapp",
        "PhoneExperienceHost", "Widgets", "TranslucentTB", "TaskbarX", "python",
    };

    public static bool IsProtected(string process) => NeverTouch.Contains(process);

    public static List<GameEntry> All(IEnumerable<string> custom)
    {
        var map = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (exe, name) in Known)
            map[exe] = new(exe.ToLowerInvariant(), name, "known");
        foreach (var g in ScanSteam().Concat(ScanEpic()))
            map[g.Exe] = g;
        foreach (string exe in custom)
            map[exe] = new(exe.ToLowerInvariant(), map.TryGetValue(exe, out var e) ? e.Name : exe, "custom");
        return map.Values.ToList();
    }

    // Installed (not built-in) games, for display.
    public static List<GameEntry> Installed() => ScanSteam().Concat(ScanEpic()).ToList();

    static IEnumerable<GameEntry> ScanSteam()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        if (key?.GetValue("SteamPath") is not string steam)
            yield break;
        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        var libraries = new List<string> { steam };
        if (File.Exists(vdf))
            libraries.AddRange(VdfValues(File.ReadAllText(vdf), "path").Select(p => p.Replace(@"\\", @"\")));

        foreach (string lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps))
                continue;
            foreach (string acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                string text = File.ReadAllText(acf);
                string? name = VdfValues(text, "name").FirstOrDefault();
                string? dir = VdfValues(text, "installdir").FirstOrDefault();
                if (name is null || dir is null)
                    continue;
                foreach (string exe in GameExes(Path.Combine(apps, "common", dir)))
                    yield return new(exe, name, "steam");
            }
        }
    }

    static IEnumerable<GameEntry> ScanEpic()
    {
        string dir = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
        if (!Directory.Exists(dir))
            yield break;
        foreach (string item in Directory.EnumerateFiles(dir, "*.item"))
        {
            JsonElement root;
            try { root = JsonDocument.Parse(File.ReadAllText(item)).RootElement; }
            catch (JsonException) { continue; }
            if (root.TryGetProperty("LaunchExecutable", out var exe) && root.TryGetProperty("DisplayName", out var name))
                yield return new(Path.GetFileNameWithoutExtension(exe.GetString() ?? "").ToLowerInvariant(), name.GetString() ?? "", "epic");
        }
    }

    // Executables in a game folder, minus installers, crash reporters and anti-cheat helpers.
    static IEnumerable<string> GameExes(string folder)
    {
        if (!Directory.Exists(folder))
            return [];
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
        return Directory.EnumerateFiles(folder, "*.exe", options)
            .Select(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant())
            .Where(n => !NotAGame().IsMatch(n))
            .Distinct();
    }

    static IEnumerable<string> VdfValues(string vdf, string key) =>
        Regex.Matches(vdf, $"\"{Regex.Escape(key)}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value);

    [GeneratedRegex("unins|crash|redist|setup|install|launcher|helper|report|vc_?redist|dxsetup|easyanticheat|battleye|be_service|prereq|update|bugsplat|cefprocess|webhelper")]
    private static partial Regex NotAGame();

    // Running apps the user could freeze/close during a game, biggest first.
    public static List<AppCandidate> RunningApps(IReadOnlySet<string> gameExes)
    {
        int session = Process.GetCurrentProcess().SessionId;
        var procs = Process.GetProcesses();
        try
        {
            return procs
                .Where(p => p.SessionId == session && !NeverTouch.Contains(p.ProcessName) && !gameExes.Contains(p.ProcessName))
                .GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
                .Where(g => BackgroundSuggestions.Contains(g.Key) || g.Any(HasWindow))
                .Select(g => new AppCandidate(
                    g.Key,
                    FriendlyName(g),
                    (int)(g.Sum(p => SafeMemory(p)) / (1024 * 1024)),
                    g.Count()))
                .OrderByDescending(a => a.MemoryMb)
                .ToList();
        }
        finally
        {
            foreach (var p in procs)
                p.Dispose();
        }
    }

    static bool HasWindow(Process p)
    {
        try { return p.MainWindowHandle != IntPtr.Zero; }
        catch (InvalidOperationException) { return false; }
    }

    static long SafeMemory(Process p)
    {
        try { return p.PrivateMemorySize64; }
        catch (InvalidOperationException) { return 0; }
    }

    static string FriendlyName(IEnumerable<Process> group)
    {
        foreach (var p in group)
        {
            try
            {
                string? desc = p.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(desc))
                    return desc.Trim();
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        return group.First().ProcessName;
    }
}
