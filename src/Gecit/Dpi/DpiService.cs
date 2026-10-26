using System.ServiceProcess;
using Gecit.Dns;
using Microsoft.Win32;

namespace Gecit.Dpi;

enum DpiState { NotInstalled, Stopped, Starting, Running, Stopping }

sealed record DpiStatus(DpiState State, string? InstalledPresetId, bool InstalledDnsRedirect, IReadOnlyList<string> Conflicts);

// The unelevated side: reads service state and toggles it. Anything that changes the
// service configuration goes through the elevated helper.
sealed class DpiService
{
    // Other DPI tools that also use WinDivert; running two at once breaks both.
    static readonly string[] KnownConflicts = ["GoodbyeDPI", "zapret", "winws1", "SplitWire"];

    public DpiStatus GetStatus()
    {
        var conflicts = KnownConflicts.Where(IsRunning).ToList();
        using var sc = Open(DpiInstaller.ServiceName);
        if (sc is null)
            return new(DpiState.NotInstalled, null, false, conflicts);

        var state = sc.Status switch
        {
            ServiceControllerStatus.Running => DriverLoaded() ? DpiState.Running : DpiState.Starting,
            ServiceControllerStatus.StartPending => DpiState.Starting,
            ServiceControllerStatus.StopPending => DpiState.Stopping,
            _ => DpiState.Stopped,
        };
        var (presetId, redirect) = InstalledConfig();
        return new(state, presetId, redirect, conflicts);
    }

    public async Task StartAsync()
    {
        using var sc = Open(DpiInstaller.ServiceName) ?? throw new InvalidOperationException("Hizmet kurulu değil.");
        if (sc.Status != ServiceControllerStatus.Running)
        {
            sc.Start();
            await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15)));
        }

        // goodbyedpi reports "running" before it opens WinDivert; the driver service coming up
        // is the real sign that it works. On failure it sleeps 20 s and exits.
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!DriverLoaded())
        {
            if (DateTime.UtcNow > deadline)
                throw new InvalidOperationException(
                    "GoodbyeDPI başladı ama WinDivert sürücüsü yüklenmedi. Antivirüs engelliyor olabilir.");
            await Task.Delay(300);
        }
        // Blocked sites may still be cached with the ISP's fake answers.
        DnsService.FlushCache();
    }

    public async Task StopAsync()
    {
        using var sc = Open(DpiInstaller.ServiceName);
        if (sc is null || sc.Status == ServiceControllerStatus.Stopped)
            return;
        if (sc.Status != ServiceControllerStatus.StopPending)
            sc.Stop();
        await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15)));
        DnsService.FlushCache();
    }

    public Task InstallAsync(DpiPreset preset, bool dnsRedirect) =>
        Elevated.RunAsync("install", preset.Id, dnsRedirect ? "1" : "0");

    public Task UninstallAllAsync() => Elevated.RunAsync("uninstall");

    static (string? PresetId, bool DnsRedirect) InstalledConfig()
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{DpiInstaller.ServiceName}");
        if (key?.GetValue("ImagePath") is not string imagePath)
            return (null, false);
        // ImagePath is "<quoted exe>" <args>
        int end = imagePath.IndexOf('"', 1);
        var (preset, redirect) = DpiPresets.Parse(end > 0 ? imagePath[(end + 1)..] : "");
        return (preset?.Id ?? "custom", redirect);
    }

    static bool DriverLoaded() => IsRunning(DpiInstaller.DriverServiceName);

    static bool IsRunning(string name)
    {
        using var sc = Open(name);
        try { return sc?.Status == ServiceControllerStatus.Running; }
        catch (InvalidOperationException) { return false; }
    }

    static ServiceController? Open(string name)
    {
        var sc = new ServiceController(name);
        try
        {
            _ = sc.Status;   // throws if the service doesn't exist
            return sc;
        }
        catch (InvalidOperationException)
        {
            sc.Dispose();
            return null;
        }
    }
}
