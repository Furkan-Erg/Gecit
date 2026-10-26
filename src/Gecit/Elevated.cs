using System.ComponentModel;
using System.Diagnostics;
using Gecit.Dns;
using Gecit.Dpi;

namespace Gecit;

// Everything that changes system configuration runs in a short-lived elevated copy of Gecit.exe.
static class Elevated
{
    public static async Task RunAsync(params string[] args)
    {
        File.Delete(AppPaths.ElevatedResultFile);
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        psi.ArgumentList.Add("--elevated");
        foreach (string a in args)
            psi.ArgumentList.Add(a);

        Process p;
        try { p = Process.Start(psi)!; }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Yönetici izni verilmedi.");
        }
        using (p)
            await p.WaitForExitAsync();

        if (p.ExitCode != 0)
        {
            string detail = File.Exists(AppPaths.ElevatedResultFile)
                ? await File.ReadAllTextAsync(AppPaths.ElevatedResultFile)
                : $"çıkış kodu {p.ExitCode}";
            throw new InvalidOperationException(detail);
        }
    }

    // For long-running elevated helpers that talk back over a pipe instead of an exit code.
    public static Process StartDetached(params string[] args)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        psi.ArgumentList.Add("--elevated");
        foreach (string a in args)
            psi.ArgumentList.Add(a);
        try { return Process.Start(psi)!; }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Yönetici izni verilmedi.");
        }
    }

    // Entry point of the elevated process.
    public static int Run(string[] args)
    {
        try
        {
            switch (args.FirstOrDefault())
            {
                case "install":    // install <dpiPreset> <dnsRedirect 0|1>
                    DpiInstaller.Install(DpiPresets.Get(args[1]), args[2] == "1");
                    break;
                case "dns":        // dns <auto|plain|doh> <provider> <dpiPreset>
                    var provider = DnsProviders.Find(args[2]) ?? throw new ArgumentException($"Bilinmeyen DNS: {args[2]}");
                    DnsSetup.Apply(Enum.Parse<DnsMode>(args[1], ignoreCase: true), provider, DpiPresets.Get(args[3]));
                    break;
                case "uninstall":  // uninstall [--full]   (--full: from the setup's uninstaller)
                    UninstallAll(full: args.Contains("--full"));
                    break;
                case "netmon":     // netmon <pipe> <uiPid>
                    Net.TrafficCollector.Run(args[1], int.Parse(args[2]));
                    break;
                case "resume-services":   // setup, after replacing files
                    AppInstall.ResumeServices();
                    break;
                default:
                    throw new ArgumentException($"Bilinmeyen komut: {string.Join(' ', args)}");
            }
            return 0;
        }
        catch (Exception e)
        {
            File.WriteAllText(AppPaths.ElevatedResultFile, e.Message);
            File.AppendAllText(Program.ErrorLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] elevated {string.Join(' ', args)}: {e}\n\n");
            return 1;
        }
    }

    // Leaves the system as it was before Geçit: original DNS, no services. Files in Program Files go
    // too when they're a development copy; a setup install keeps them for its own uninstaller.
    static void UninstallAll(bool full)
    {
        if (File.Exists(AppPaths.DnsBackupFile) || ServiceTool.Exists(DnsSetup.ServiceName))
            SystemDns.Restore();
        ServiceTool.Remove(DnsSetup.ServiceName);
        DpiInstaller.Uninstall();
        if (!AppPaths.RunningFromInstallRoot)
        {
            try { Directory.Delete(AppPaths.InstallRoot, recursive: true); }
            catch (IOException) { }      // a still-loaded WinDivert64.sys; gone after reboot
            catch (UnauthorizedAccessException) { }
        }
        if (full)
            Autostart.Set(false);
        DnsService.FlushCache();
    }
}
