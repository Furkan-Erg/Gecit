namespace Gecit;

static class AppPaths
{
    public static string AppDir => AppContext.BaseDirectory;
    public static string WebRoot => Path.Combine(AppDir, "wwwroot");
    public static string BundledDpiDir => Path.Combine(AppDir, "dpi");
    public static string BundledDnsDir => Path.Combine(AppDir, "dnssvc");

    // Services run their binaries from Program Files, where normal users can't replace them.
    public static string InstallRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Gecit");
    public static string InstalledDpiDir => Path.Combine(InstallRoot, "dpi");
    public static string InstalledDnsDir => Path.Combine(InstallRoot, "dns");

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
