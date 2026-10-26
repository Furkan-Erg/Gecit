using System.Net;

namespace Gecit.Dns;

public sealed record DnsProvider(
    string Id,
    string Name,
    string Tag,
    string[] V4,
    string[] V6,
    string DohUrl)
{
    public IPAddress[] BootstrapIps => V4.Select(IPAddress.Parse).ToArray();
}

public static class DnsProviders
{
    public static readonly IReadOnlyList<DnsProvider> All =
    [
        new("cloudflare", "Cloudflare", "Genelde en hızlı",
            ["1.1.1.1", "1.0.0.1"], ["2606:4700:4700::1111", "2606:4700:4700::1001"],
            "https://cloudflare-dns.com/dns-query"),
        new("google", "Google", "Çok yaygın, kararlı",
            ["8.8.8.8", "8.8.4.4"], ["2001:4860:4860::8888", "2001:4860:4860::8844"],
            "https://dns.google/dns-query"),
        new("quad9", "Quad9", "Zararlı siteleri engeller",
            ["9.9.9.9", "149.112.112.112"], ["2620:fe::fe", "2620:fe::9"],
            "https://dns.quad9.net/dns-query"),
        new("adguard", "AdGuard", "Reklam ve takipçileri engeller",
            ["94.140.14.14", "94.140.15.15"], ["2a10:50c0::ad1:ff", "2a10:50c0::ad2:ff"],
            "https://dns.adguard-dns.com/dns-query"),
        new("yandex", "Yandex", "Türkiye'ye yakın sunucular",
            ["77.88.8.8", "77.88.8.1"], ["2a02:6b8::feed:0ff", "2a02:6b8:0:1::feed:0ff"],
            "https://common.dot.dns.yandex.net/dns-query"),
    ];

    public static DnsProvider? Find(string? id) => All.FirstOrDefault(p => p.Id == id);
}
