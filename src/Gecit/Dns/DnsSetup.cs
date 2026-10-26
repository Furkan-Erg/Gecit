using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Gecit.Dpi;

namespace Gecit.Dns;

enum DnsMode { Auto, Plain, Doh }

// Runs only inside the elevated helper process. Order matters in every branch: the system DNS is
// only pointed at the local forwarder after it has answered a real query, and it's moved away
// from the forwarder before the forwarder is removed, so there's never a window without DNS.
static class DnsSetup
{
    public const string ServiceName = "GecitDNS";

    // Interactive users may start the service (so the app can revive it) but not stop it:
    // while the system DNS points at it, stopping it would cut the internet.
    const string ServiceSddl =
        "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";

    public static void Apply(DnsMode mode, DnsProvider provider, DpiPreset dpiPreset)
    {
        switch (mode)
        {
            case DnsMode.Doh:
                InstallForwarder(provider);
                SystemDns.Set(["127.0.0.1"], ["::1"]);
                break;
            case DnsMode.Plain:
                SystemDns.Set(provider.V4, provider.V6);
                ServiceTool.Remove(ServiceName);
                break;
            case DnsMode.Auto:
                SystemDns.Restore();
                ServiceTool.Remove(ServiceName);
                break;
        }
        // GoodbyeDPI's Yandex redirect is only wanted when the system DNS is the ISP's.
        DpiInstaller.ReconfigureIfInstalled(dpiPreset, dnsRedirect: mode == DnsMode.Auto);
        DnsService.FlushCache();
    }

    static void InstallForwarder(DnsProvider provider)
    {
        ServiceTool.Stop(ServiceName);
        AppInstall.EnsureInstalledCopy();
        ServiceTool.CreateOrUpdate(ServiceName, $"\"{AppPaths.InstalledExe}\" --dns-service {provider.Id}", "auto", "Geçit DNS",
            "Geçit: şifreli DNS (DNS-over-HTTPS). Durdurulursa DNS çalışmaz; Geçit'ten Otomatik moda geçerek kaldırın.",
            ServiceSddl, account: @"NT AUTHORITY\LocalService");
        ServiceTool.Sc("failure", ServiceName, "reset=", "86400", "actions=", "restart/2000/restart/5000/restart/10000");

        string? problem = null;
        try
        {
            ServiceTool.Start(ServiceName);
            // A cold service start pays for runtime start-up plus a TLS handshake per upstream.
            if (!AnswersOnLoopback(TimeSpan.FromSeconds(20)))
                problem = "hizmet başladı ama sorgulara yanıt vermedi";
        }
        catch (Exception e) when (e is InvalidOperationException or System.ServiceProcess.TimeoutException)
        {
            problem = "hizmet başlatılamadı (53 numaralı port başka bir program tarafından kullanılıyor olabilir)";
        }
        if (problem is not null)
        {
            ServiceTool.Remove(ServiceName);
            string log = DnsWindowsService.LogTail(6);
            throw new InvalidOperationException($"Şifreli DNS açılamadı: {problem}. Sistem DNS'i değiştirilmedi."
                + (log.Length > 0 ? $"\n\nHizmet günlüğü ({DnsWindowsService.LogFile}):\n{log}" : ""));
        }
    }

    static bool AnswersOnLoopback(TimeSpan within)
    {
        var sw = Stopwatch.StartNew();
        byte[] query = DnsMessage.BuildQuery("cloudflare.com", id: 0x4745);
        while (sw.Elapsed < within)
        {
            using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ReceiveTimeout = 6000 };
            try
            {
                udp.SendTo(query, new IPEndPoint(IPAddress.Loopback, 53));
                var buf = new byte[1024];
                int n = udp.Receive(buf);
                if (n >= 12 && DnsMessage.Id(buf) == 0x4745 && (buf[3] & 0x0F) == 0 && DnsMessage.ReadA(buf.AsSpan(0, n)).Count > 0)
                    return true;
            }
            catch (SocketException) { }
            Thread.Sleep(500);
        }
        return false;
    }
}
