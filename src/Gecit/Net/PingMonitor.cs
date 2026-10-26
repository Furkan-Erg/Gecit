using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Gecit.Net;

sealed record PingStats(string Target, int? LastMs, double? AvgMs, int? MinMs, int? MaxMs, double? JitterMs, double LossPercent, int?[] History);

// Pings the internet target and the default gateway once a second while the İnternet page is open.
// Two lines tell apart "my modem/Wi-Fi is the problem" from "my ISP is the problem".
sealed class PingMonitor : IDisposable
{
    const int HistorySize = 120;

    readonly object gate = new();
    readonly Queue<int?> internet = new();
    readonly Queue<int?> gateway = new();
    CancellationTokenSource? cts;
    DateTime lastPolled;

    public string Target { get; private set; } = "1.1.1.1";
    public string? Gateway { get; private set; }

    // Called by every UI poll; the loop stops by itself when nobody asks for a while.
    public void KeepAlive()
    {
        lastPolled = DateTime.UtcNow;
        if (cts is null)
        {
            cts = new CancellationTokenSource();
            _ = Loop(cts.Token);
        }
    }

    public void SetTarget(string target)
    {
        lock (gate)
        {
            Target = target.Trim();
            internet.Clear();
        }
    }

    async Task Loop(CancellationToken ct)
    {
        Gateway = DefaultGateway();
        using var p1 = new Ping();
        using var p2 = new Ping();
        while (!ct.IsCancellationRequested)
        {
            if (DateTime.UtcNow - lastPolled > TimeSpan.FromSeconds(6))
                break;
            var tick = Task.Delay(1000, ct);
            var a = Once(p1, Target);
            var b = Gateway is null ? Task.FromResult<int?>(null) : Once(p2, Gateway);
            await Task.WhenAll(a, b);
            lock (gate)
            {
                Push(internet, a.Result);
                if (Gateway is not null)
                    Push(gateway, b.Result);
            }
            try { await tick; } catch (OperationCanceledException) { break; }
        }
        cts = null;
    }

    static async Task<int?> Once(Ping ping, string host)
    {
        try
        {
            var r = await ping.SendPingAsync(host, 1000);
            return r.Status == IPStatus.Success ? (int)r.RoundtripTime : null;
        }
        catch (PingException) { return null; }
    }

    static void Push(Queue<int?> q, int? v)
    {
        q.Enqueue(v);
        while (q.Count > HistorySize)
            q.Dequeue();
    }

    public (PingStats Internet, PingStats? Gateway) Snapshot()
    {
        lock (gate)
        {
            return (Stats(Target, internet), Gateway is null ? null : Stats(Gateway, gateway));
        }
    }

    static PingStats Stats(string target, Queue<int?> q)
    {
        var all = q.ToArray();
        var recent = all.TakeLast(60).ToArray();
        var ok = recent.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        double? jitter = ok.Length < 2 ? null : ok.Zip(ok.Skip(1), (x, y) => Math.Abs(x - y)).Average();
        double loss = recent.Length == 0 ? 0 : 100.0 * recent.Count(v => v is null) / recent.Length;
        return new(target, all.LastOrDefault(), ok.Length == 0 ? null : ok.Average(), ok.Length == 0 ? null : ok.Min(),
            ok.Length == 0 ? null : ok.Max(), jitter, loss, all);
    }

    static string? DefaultGateway() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().GatewayAddresses)
            .Select(g => g.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))
            ?.ToString();

    public void Dispose() => cts?.Cancel();
}
