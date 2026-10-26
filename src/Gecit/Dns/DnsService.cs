using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Gecit.Net;
using Microsoft.Win32;

namespace Gecit.Dns;

sealed record DnsStatus(
    DnsMode Mode,
    string? ProviderId,
    string ForwarderState,          // running | stopped | notinstalled
    IReadOnlyList<Adapter> Adapters);

sealed record ProviderBench(string Id, int? PingMs, int? DohMs, string? Error);

sealed record DnsDiagnosis(
    bool SystemDnsBlocked,           // does the current system resolver return the ISP's block page?
    bool? IspHijacksPort53);         // null: couldn't test (GoodbyeDPI is redirecting DNS)

// The unelevated side of Akıllı DNS.
static partial class DnsService
{
    public static DnsStatus GetStatus()
    {
        var adapters = SystemDns.Adapters();
        string forwarder = ForwarderState(out string? forwarderProvider);
        var v4 = adapters.SelectMany(a => a.V4Dns).Distinct().ToList();

        if (v4.Count > 0 && v4.All(s => s == "127.0.0.1"))
            return new(DnsMode.Doh, forwarderProvider, forwarder, adapters);
        var plain = DnsProviders.All.FirstOrDefault(p => v4.Count > 0 && v4.All(p.V4.Contains));
        if (plain is not null)
            return new(DnsMode.Plain, plain.Id, forwarder, adapters);
        return new(DnsMode.Auto, null, forwarder, adapters);
    }

    // If DoH mode is active and the forwarder died, start it again (interactive users may start it).
    public static bool TryReviveForwarder()
    {
        try
        {
            using var sc = new ServiceController(DnsSetup.ServiceName);
            if (sc.Status != ServiceControllerStatus.Stopped)
                return false;
            sc.Start();
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    public static Task ApplyAsync(DnsMode mode, string providerId, string dpiPresetId) =>
        Elevated.RunAsync("dns", mode.ToString().ToLowerInvariant(), providerId, dpiPresetId);

    static string ForwarderState(out string? providerId)
    {
        providerId = null;
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{DnsSetup.ServiceName}");
        if (key?.GetValue("ImagePath") is not string imagePath)
            return "notinstalled";
        providerId = imagePath[(imagePath.LastIndexOf('"') + 1)..].Trim();
        try
        {
            using var sc = new ServiceController(DnsSetup.ServiceName);
            return sc.Status == ServiceControllerStatus.Running ? "running" : "stopped";
        }
        catch (InvalidOperationException) { return "notinstalled"; }
    }

    public static async Task<ProviderBench[]> BenchmarkAsync() =>
        await Task.WhenAll(DnsProviders.All.Select(BenchOneAsync));

    static async Task<ProviderBench> BenchOneAsync(DnsProvider p)
    {
        try
        {
            // Network distance: TCP handshake to the resolver (unaffected by GoodbyeDPI's port-53 redirect).
            var pings = new List<long>();
            for (int i = 0; i < 3; i++)
            {
                using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var sw = Stopwatch.StartNew();
                await s.ConnectAsync(new IPEndPoint(p.BootstrapIps[0], 443), cts.Token);
                pings.Add(sw.ElapsedMilliseconds);
            }

            // Real lookups over an established DoH connection (the first one pays for TLS).
            using var doh = new DohClient(p);
            await doh.QueryAsync(DnsMessage.BuildQuery("example.com"), TimeSpan.FromSeconds(5));
            var times = new List<long>();
            foreach (string name in new[] { "discord.com", "youtube.com", "wikipedia.org", "github.com" })
            {
                var sw = Stopwatch.StartNew();
                await doh.QueryAsync(DnsMessage.BuildQuery(name), TimeSpan.FromSeconds(5));
                times.Add(sw.ElapsedMilliseconds);
            }
            return new(p.Id, (int)Median(pings), (int)Median(times), null);
        }
        catch (Exception e)
        {
            return new(p.Id, null, null, e is OperationCanceledException ? "Zaman aşımı" : e.GetBaseException().Message);
        }
    }

    public static async Task<DnsDiagnosis> DiagnoseAsync(bool goodbyeDpiRedirectsDns)
    {
        bool systemBlocked;
        try
        {
            var addrs = await System.Net.Dns.GetHostAddressesAsync("discord.com");
            systemBlocked = addrs.Any(SiteProbe.IsBlockPage);
        }
        catch (SocketException) { systemBlocked = true; }

        bool? hijack = null;
        if (!goodbyeDpiRedirectsDns)
        {
            // Ask Cloudflare directly over plain UDP; a block-page answer means the ISP rewrites port 53.
            try
            {
                using var udp = new UdpClient(AddressFamily.InterNetwork);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                byte[] q = DnsMessage.BuildQuery("discord.com", id: 0x1234);
                await udp.SendAsync(q, new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53), cts.Token);
                var r = await udp.ReceiveAsync(cts.Token);
                hijack = DnsMessage.ReadA(r.Buffer).Any(SiteProbe.IsBlockPage);
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException) { hijack = true; }
        }
        return new(systemBlocked, hijack);
    }

    static double Median(List<long> xs)
    {
        xs.Sort();
        return xs.Count % 2 == 1 ? xs[xs.Count / 2] : (xs[xs.Count / 2 - 1] + xs[xs.Count / 2]) / 2.0;
    }

    public static void FlushCache() => DnsFlushResolverCache();

    [LibraryImport("dnsapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DnsFlushResolverCache();
}
