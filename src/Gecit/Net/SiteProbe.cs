using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Gecit.Net;

sealed record ProbeResult(string Name, string Host, bool Ok, int? Ms, string Detail);

// Checks whether commonly blocked sites actually open from this machine.
static class SiteProbe
{
    public static readonly (string Name, string Host)[] Targets =
    [
        ("Discord", "discord.com"),
        ("Discord CDN", "cdn.discordapp.com"),
        ("Roblox", "www.roblox.com"),
        ("Wattpad", "www.wattpad.com"),
        ("Google (kontrol)", "www.google.com"),
    ];

    // Where ISPs point blocked names instead of the real servers (seen: Türk Telekom).
    static readonly HashSet<IPAddress> BlockPageIps = [IPAddress.Parse("195.175.254.2")];

    public static bool IsBlockPage(IPAddress ip) => BlockPageIps.Contains(ip);

    public static Task<ProbeResult[]> RunAllAsync() => Task.WhenAll(Targets.Select(t => RunAsync(t.Name, t.Host)));

    static async Task<ProbeResult> RunAsync(string name, string host)
    {
        try
        {
            var addrs = await System.Net.Dns.GetHostAddressesAsync(host);
            if (addrs.Any(BlockPageIps.Contains))
                return new(name, host, false, null, "DNS engeli: operatörün engel sayfasına yönleniyor");
        }
        catch (SocketException)
        {
            return new(name, host, false, null, "DNS çözülemedi (DNS engeli)");
        }

        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.Zero,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 Gecit/0.1");

        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await http.GetAsync($"https://{host}/", HttpCompletionOption.ResponseHeadersRead);
            return new(name, host, true, (int)sw.ElapsedMilliseconds, $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception e)
        {
            return new(name, host, false, null, Describe(e));
        }
    }

    static string Describe(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException)
        {
            switch (x)
            {
                case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData }:
                    return "DNS çözülemedi (DNS engeli)";
                case SocketException { SocketErrorCode: SocketError.ConnectionReset }:
                    return "Bağlantı kesildi (DPI engeli)";
                case SocketException { SocketErrorCode: SocketError.TimedOut }:
                    return "Zaman aşımı";
                case AuthenticationException:
                    return "TLS başarısız (sahte sertifika / DPI)";
                case TaskCanceledException or TimeoutException:
                    return "Zaman aşımı";
                case IOException when x.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset }:
                    return "Bağlantı kesildi (DPI engeli)";
            }
        }
        return e is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError }
            ? "DNS çözülemedi (DNS engeli)"
            : e.GetBaseException().Message;
    }
}
