namespace Gecit;

static class AppPaths
{
    public static string AppDir => AppContext.BaseDirectory;
    public static string WebRoot => Path.Combine(AppDir, "wwwroot");
    // Services run their binaries from Program Files, where normal users can't replace them.
    // The setup installs the app right there; a development build is copied there first (AppInstall).
    public static string InstallRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Gecit");
    public static string InstalledExe => Path.Combine(InstallRoot, "Gecit.exe");
    public static string InstalledDpiDir => Path.Combine(InstallRoot, "dpi");

    public static bool RunningFromInstallRoot => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppDir)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(InstallRoot)),
        StringComparison.OrdinalIgnoreCase);

    // DNS settings from before Geçit changed them; admin-only location so restore can't be tampered with.
    public static string DnsBackupFile => Path.Combine(InstallRoot, "dns-backup.json");

    public static string RoamingDir => Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Gecit"));

    public static string LocalDir => Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gecit"));

    public static string SettingsFile => Path.Combine(RoamingDir, "settings.json");
    public static string WebViewDataDir => Path.Combine(LocalDir, "WebView2");
    public static string ElevatedResultFile => Path.Combine(LocalDir, "elevated-result.txt");

    static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
