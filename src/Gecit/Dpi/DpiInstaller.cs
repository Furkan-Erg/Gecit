namespace Gecit.Dpi;

// Runs only inside the elevated helper process (Gecit.exe --elevated ...).
static class DpiInstaller
{
    public const string ServiceName = "GecitDPI";
    public const string DriverServiceName = "WinDivert";

    // Default service DACL, except interactive users (IU) also get start (RP) and stop (WP),
    // so the unelevated app can toggle the service. Config changes stay admin-only.
    const string ServiceSddl =
        "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPWPLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";

    public static void Install(DpiPreset preset, bool dnsRedirect)
    {
        bool wasRunning = ServiceTool.Stop(ServiceName);
        ServiceTool.CopyFiles(AppPaths.BundledDpiDir, AppPaths.InstalledDpiDir, "*");

        string exe = Path.Combine(AppPaths.InstalledDpiDir, "goodbyedpi.exe");
        ServiceTool.CreateOrUpdate(ServiceName, $"\"{exe}\" {preset.Args(dnsRedirect)}", "demand", "Geçit DPI",
            "Geçit: DPI engellerini aşar (GoodbyeDPI).", ServiceSddl);

        if (wasRunning)
            ServiceTool.Start(ServiceName);
    }

    // DNS mode changes flip the DNS redirect of an already installed service.
    public static void ReconfigureIfInstalled(DpiPreset preset, bool dnsRedirect)
    {
        if (ServiceTool.Exists(ServiceName))
            Install(preset, dnsRedirect);
    }

    public static void Uninstall()
    {
        ServiceTool.Remove(ServiceName);
        // WinDivert's driver stays loaded until stopped; GoodbyeDPI's own remover does the same.
        ServiceTool.Remove(DriverServiceName);

        try { Directory.Delete(AppPaths.InstalledDpiDir, recursive: true); }
        catch (IOException) { }      // driver file still locked until reboot; harmless leftover
        catch (UnauthorizedAccessException) { }
    }
}
