using System.Text.Json;
using Gecit.Dns;
using Gecit.Dpi;

namespace Gecit;

// Elevated-only helpers around "the copy of Geçit in Program Files" that the services run from.
static class AppInstall
{
    // Installed by the setup: nothing to do. Development build: copy it into Program Files, because a
    // service running as SYSTEM/LocalService must not execute files a normal user can overwrite.
    public static void EnsureInstalledCopy()
    {
        if (AppPaths.RunningFromInstallRoot)
            return;
        try
        {
            ServiceTool.CopyFiles(AppPaths.AppDir, AppPaths.InstallRoot, "*");
        }
        catch (IOException)
        {
            // The DNS service runs Gecit.exe from there; let go of the files, copy, carry on.
            bool dnsWasRunning = ServiceTool.Stop(DnsSetup.ServiceName);
            ServiceTool.CopyFiles(AppPaths.AppDir, AppPaths.InstallRoot, "*");
            if (dnsWasRunning)
                ServiceTool.Start(DnsSetup.ServiceName);
        }
    }

    // Written by the setup (installer\gecit.iss, PrepareToInstall) when it stops our services to
    // replace their files: a JSON list of the ones that were running.
    static string PausedFile => Path.Combine(AppPaths.InstallRoot, "paused-services.json");

    // After the setup copied the new files: start again whatever was running before.
    public static void ResumeServices()
    {
        if (!File.Exists(PausedFile))
            return;
        var running = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(PausedFile)) ?? [];
        File.Delete(PausedFile);
        foreach (string name in running.Where(ServiceTool.Exists))
        {
            try { ServiceTool.Start(name); }
            catch (Exception e) when (e is InvalidOperationException or System.ServiceProcess.TimeoutException) { }
        }
    }
}
