using System.Diagnostics;
using System.Drawing.Imaging;
using Microsoft.Win32;

namespace Gecit.Boot;

enum StartupSource { UserRun, MachineRun, MachineRun32, UserFolder, CommonFolder }

sealed record StartupItem(
    string Id, StartupSource Source, string Key, string Name, string? Publisher, string Command, string? ExePath,
    bool Enabled, bool NeedsAdmin, bool Missing, bool IsWindows, string? Hint, bool Suggested);

// The same list as Task Manager's "Başlangıç" tab: the Run keys and the Startup folders. Turning an
// item off writes the same StartupApproved value Task Manager writes, so both stay in sync and
// nothing is deleted.
static class StartupItems
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Run32Key = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    const string Launcher = "Oyun başlatıcısı. Açılışta gerekmez: oyunu açınca kendisi de açılır.";
    const string Updater = "Arka planda güncelleme denetler. Kapatılabilir; program yine çalışır.";

    // exe name (or a word in the command) -> why it's safe to turn off.
    static readonly (string Match, string Hint, bool Suggested)[] Hints =
    [
        ("onedrive.exe", "OneDrive kullanmıyorsan kapatabilirsin. Eşitleme durur, dosyaların silinmez.", false),
        ("browser_assistant.exe", "Opera'nın arka plan yardımcısı. Kapatmak Opera'yı etkilemez.", true),
        ("msedge.exe", "Edge'i açılışta arka planda hazırlar. Edge'i az kullanıyorsan kapat.", true),
        ("steam.exe", Launcher, true), ("epicgameslauncher.exe", Launcher, true), ("battle.net.exe", Launcher, true),
        ("battle.net launcher.exe", Launcher, true), ("riotclientservices.exe", Launcher, true), ("eadesktop.exe", Launcher, true),
        ("upc.exe", Launcher, true), ("galaxyclient.exe", Launcher, true), ("origin.exe", Launcher, true),
        ("overwolf.exe", "Oyun içi eklenti platformu; arka planda RAM kullanır. Kullanmıyorsan kapat.", true),
        ("discord", "Açılışta gerekmez; Discord'u açınca bağlanır. Arkadaşların seni çevrimiçi görmesi için açık bırakabilirsin.", false),
        ("spotify.exe", "Açılışta gerekmez; Spotify'ı açınca kendisi gelir.", true),
        ("teams", "Toplantın yoksa kapatılabilir.", true), ("skype", "Kullanmıyorsan kapatılabilir.", true),
        ("jusched.exe", Updater, true), ("adobegcclient.exe", Updater, true), ("acrotray.exe", Updater, true),
        ("adobearm", Updater, true), ("googleupdate", Updater, true), ("solidworks", Updater, true),
        ("ccleaner", "Arka plan izleyicisi; kapatılabilir.", true),
        ("dropbox.exe", "Dropbox kullanmıyorsan kapatabilirsin. Eşitleme durur.", false),
        ("googledrivefs.exe", "Google Drive kullanmıyorsan kapatabilirsin. Eşitleme durur.", false),
        ("securityhealthsystray.exe", "Windows Güvenliği'nin simgesi. Korumayı kapatmaz ama açık kalması önerilir.", false),
    ];

    public static List<StartupItem> List()
    {
        var list = new List<StartupItem>();
        AddRun(list, Registry.CurrentUser, RunKey, StartupSource.UserRun);
        AddRun(list, Registry.LocalMachine, RunKey, StartupSource.MachineRun);
        AddRun(list, Registry.LocalMachine, Run32Key, StartupSource.MachineRun32);
        AddFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupSource.UserFolder);
        AddFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupSource.CommonFolder);
        return list;
    }

    static void AddRun(List<StartupItem> list, RegistryKey hive, string path, StartupSource source)
    {
        using var key = hive.OpenSubKey(path);
        if (key is null)
            return;
        foreach (string name in key.GetValueNames())
        {
            // Geçit's own entry is the "Windows açılınca başlat" switch in Ayarlar.
            if (source == StartupSource.UserRun && name == "Gecit")
                continue;
            if (key.GetValue(name) is not string cmd || string.IsNullOrWhiteSpace(cmd))
                continue;
            list.Add(Make(source, name, cmd, ExeFromCommand(cmd)));
        }
    }

    static void AddFolder(List<StartupItem> list, string dir, StartupSource source)
    {
        if (!Directory.Exists(dir))
            return;
        foreach (string file in Directory.GetFiles(dir))
        {
            string fileName = Path.GetFileName(file);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                continue;
            string? exe = file, cmd = file;
            if (file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                (exe, string args) = ResolveShortcut(file);
                cmd = exe is null ? file : $"\"{exe}\" {args}".Trim();
            }
            list.Add(Make(source, fileName, cmd, exe, Path.GetFileNameWithoutExtension(file)));
        }
    }

    static StartupItem Make(StartupSource source, string key, string cmd, string? exe, string? fallbackName = null)
    {
        bool missing = exe is null || !File.Exists(exe);
        FileVersionInfo? info = null;
        if (!missing)
        {
            try { info = FileVersionInfo.GetVersionInfo(exe!); }
            catch (FileNotFoundException) { }
        }
        string name = FirstNonEmpty(info?.FileDescription, info?.ProductName, fallbackName, key)!;
        string? publisher = FirstNonEmpty(info?.CompanyName);
        bool windows = publisher == "Microsoft Corporation" && exe is not null
                       && exe.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase);
        string haystack = $"{Path.GetFileName(exe ?? "")} {cmd} {key}".ToLowerInvariant();
        var hint = Hints.FirstOrDefault(h => haystack.Contains(h.Match, StringComparison.Ordinal));
        return new StartupItem($"{source}|{key}", source, key, name, publisher, cmd, exe,
            IsEnabled(source, key), source is not (StartupSource.UserRun or StartupSource.UserFolder), missing, windows,
            hint.Hint, hint.Suggested);
    }

    static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    // "C:\Program Files\x\a.exe" -flag  |  C:\Program Files\x\a.exe -flag  |  rundll32.exe x.dll,Entry
    public static string? ExeFromCommand(string cmd)
    {
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            return end > 1 ? cmd[1..end] : null;
        }
        // Unquoted paths with spaces: the first ".exe" that exists.
        for (int i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); i >= 0;
             i = cmd.IndexOf(".exe", i + 4, StringComparison.OrdinalIgnoreCase))
        {
            string candidate = cmd[..(i + 4)];
            if (File.Exists(candidate))
                return candidate;
            if (!Path.IsPathRooted(candidate) && Path.Combine(Environment.SystemDirectory, candidate) is var sys && File.Exists(sys))
                return sys;
        }
        return cmd.Split(' ')[0];
    }

    static (string? Exe, string Args) ResolveShortcut(string lnk)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
                return (null, "");
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(lnk);
            string target = link.TargetPath;
            string args = link.Arguments;
            return (string.IsNullOrEmpty(target) ? null : target, args ?? "");
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            return (null, "");
        }
    }

    // ---- Enabled state (StartupApproved) ----

    static (RegistryKey Hive, string Path) Approved(StartupSource source) => source switch
    {
        StartupSource.UserRun => (Registry.CurrentUser, ApprovedKey + "Run"),
        StartupSource.MachineRun => (Registry.LocalMachine, ApprovedKey + "Run"),
        StartupSource.MachineRun32 => (Registry.LocalMachine, ApprovedKey + "Run32"),
        StartupSource.UserFolder => (Registry.CurrentUser, ApprovedKey + "StartupFolder"),
        _ => (Registry.LocalMachine, ApprovedKey + "StartupFolder"),
    };

    // 12 bytes; an odd first byte means "disabled" (Task Manager writes 03 + the time it was turned off).
    static bool IsEnabled(StartupSource source, string key)
    {
        var (hive, path) = Approved(source);
        using var k = hive.OpenSubKey(path);
        return k?.GetValue(key) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;
    }

    // Machine-wide items need admin: called from the elevated helper ("startup ..."), after checking
    // that the entry really exists so the helper can't be used to write arbitrary values.
    public static void SetEnabled(StartupSource source, string key, bool enabled)
    {
        if (!List().Any(i => i.Source == source && i.Key == key))
            throw new ArgumentException($"Başlangıç öğesi bulunamadı: {key}");
        var data = new byte[12];
        data[0] = (byte)(enabled ? 2 : 3);
        if (!enabled)
            BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        var (hive, path) = Approved(source);
        using var k = hive.CreateSubKey(path);
        k.SetValue(key, data, RegistryValueKind.Binary);
    }

    // ---- Icons ----

    static readonly Dictionary<string, string?> iconCache = new(StringComparer.OrdinalIgnoreCase);

    public static string? IconDataUrl(string? exe)
    {
        if (exe is null || !File.Exists(exe))
            return null;
        lock (iconCache)
        {
            if (iconCache.TryGetValue(exe, out string? cached))
                return cached;
        }
        string? url = null;
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(exe);
            if (icon is not null)
            {
                using var bmp = icon.ToBitmap();
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                url = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
            }
        }
        catch (Exception e) when (e is ArgumentException or IOException or System.ComponentModel.Win32Exception) { }
        lock (iconCache)
            iconCache[exe] = url;
        return url;
    }
}
