using System.ServiceProcess;

namespace Gecit.Dns;

// The GecitDNS Windows service. Hosted by Gecit.exe (--dns-service <provider>) in a normal install;
// Gecit.Dns.exe can host it too for testing.
public sealed class DnsWindowsService : ServiceBase
{
    readonly DnsProvider provider;
    Forwarder? forwarder;

    public DnsWindowsService(DnsProvider provider)
    {
        this.provider = provider;
        ServiceName = "GecitDNS";
        CanShutdown = true;
        // AutoLog would register an event source, which LocalService isn't allowed to do.
        AutoLog = false;
    }

    // %ProgramData%\Gecit\dns.log: the only way to see why a service run as LocalService fails.
    public static string LogFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Gecit", "dns.log");

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            var info = new FileInfo(LogFile);
            if (info.Exists && info.Length > 512 * 1024)
                info.Delete();
            File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static string LogTail(int lines)
    {
        try { return string.Join("\n", File.ReadLines(LogFile).TakeLast(lines)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }

    protected override void OnStart(string[] args)
    {
        Log($"başlıyor: {provider.Id} ({provider.DohUrl}), hesap {Environment.UserName}");
        try
        {
            forwarder = new Forwarder(provider) { Log = Log };
            forwarder.Start();
            Log("127.0.0.1:53 ve [::1]:53 dinleniyor");
        }
        catch (Exception e)
        {
            Log($"başlatılamadı: {e}");
            throw;
        }
    }

    protected override void OnStop()
    {
        Log("duruyor");
        forwarder?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
    }

    protected override void OnShutdown() => OnStop();
}
