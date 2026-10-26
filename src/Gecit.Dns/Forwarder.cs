using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Gecit.Dns;

// Local DNS server on loopback that answers by asking DoH upstreams.
// Order: chosen provider, then the general-purpose fallbacks; plain UDP as a last resort
// so a DoH outage degrades privacy instead of breaking the internet.
public sealed class Forwarder : IAsyncDisposable
{
    static readonly string[] FallbackIds = ["cloudflare", "google", "quad9"];
    // Generous: the first query to each upstream also pays for TCP + TLS setup, which is slow when
    // the service has just started cold. Too short a timeout cascades through every upstream.
    static readonly TimeSpan UpstreamTimeout = TimeSpan.FromSeconds(4);
    static readonly TimeSpan PenaltyBox = TimeSpan.FromSeconds(30);

    sealed class Upstream(DohClient client)
    {
        public DohClient Client { get; } = client;
        long downUntil;
        public long DownUntil
        {
            get => Interlocked.Read(ref downUntil);
            set => Interlocked.Exchange(ref downUntil, value);
        }
    }

    readonly List<Upstream> upstreams;
    readonly IPEndPoint plainFallback;
    readonly int port;
    readonly CancellationTokenSource stop = new();
    readonly List<Socket> sockets = [];
    readonly List<Task> loops = [];

    public Action<string>? Log { get; init; }

    public Forwarder(DnsProvider primary, int port = 53)
    {
        this.port = port;
        upstreams = FallbackIds.Where(id => id != primary.Id)
            .Select(id => DnsProviders.Find(id)!)
            .Prepend(primary)
            .Select(p => new Upstream(new DohClient(p)))
            .ToList();
        plainFallback = new IPEndPoint(primary.BootstrapIps[0], 53);
    }

    public void Start()
    {
        foreach (var ip in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            var udp = new Socket(ip.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            udp.Bind(new IPEndPoint(ip, port));
            sockets.Add(udp);
            loops.Add(UdpLoop(udp));

            var tcp = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            tcp.Bind(new IPEndPoint(ip, port));
            tcp.Listen(64);
            sockets.Add(tcp);
            loops.Add(TcpLoop(tcp));
        }
    }

    async Task UdpLoop(Socket udp)
    {
        var buf = new byte[4096];
        EndPoint any = new IPEndPoint(udp.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
        while (!stop.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try { r = await udp.ReceiveFromAsync(buf, SocketFlags.None, any, stop.Token); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }   // e.g. ICMP port unreachable from a previous reply
            if (r.ReceivedBytes < 12)
                continue;
            byte[] query = buf.AsSpan(0, r.ReceivedBytes).ToArray();
            var client = r.RemoteEndPoint;
            _ = Task.Run(async () =>
            {
                byte[] resp = await ResolveAsync(query);
                if (resp.Length > DnsMessage.MaxUdpSize(query))
                    resp = DnsMessage.Truncated(query, resp);
                try { await udp.SendToAsync(resp, SocketFlags.None, client); }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            });
        }
    }

    async Task TcpLoop(Socket listener)
    {
        while (!stop.IsCancellationRequested)
        {
            Socket conn;
            try { conn = await listener.AcceptAsync(stop.Token); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }
            _ = Task.Run(() => ServeTcp(conn));
        }
    }

    async Task ServeTcp(Socket conn)
    {
        using var _ = conn;
        await using var stream = new NetworkStream(conn);
        var lenBuf = new byte[2];
        try
        {
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                idle.CancelAfter(TimeSpan.FromSeconds(10));
                await stream.ReadExactlyAsync(lenBuf, idle.Token);
                byte[] query = new byte[BinaryPrimitives.ReadUInt16BigEndian(lenBuf)];
                await stream.ReadExactlyAsync(query, idle.Token);
                if (query.Length < 12)
                    return;
                byte[] resp = await ResolveAsync(query);
                var outBuf = new byte[resp.Length + 2];
                BinaryPrimitives.WriteUInt16BigEndian(outBuf, (ushort)resp.Length);
                resp.CopyTo(outBuf, 2);
                await stream.WriteAsync(outBuf, stop.Token);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or EndOfStreamException or SocketException) { }
    }

    public async Task<byte[]> ResolveAsync(byte[] query)
    {
        long now = Environment.TickCount64;
        for (int i = 0; i < upstreams.Count; i++)
        {
            var up = upstreams[i];
            // Skip a recently failed upstream, unless it's the last DoH option left.
            if (up.DownUntil > now && i < upstreams.Count - 1)
                continue;
            try
            {
                byte[] resp = await up.Client.QueryAsync(query, UpstreamTimeout, stop.Token);
                up.DownUntil = 0;
                return resp;
            }
            catch (Exception e)
            {
                if (stop.IsCancellationRequested)
                    break;
                Log?.Invoke($"{up.Client.Provider.Id}: {e.GetType().Name}: {e.Message}");
                up.DownUntil = now + (long)PenaltyBox.TotalMilliseconds;
            }
        }
        return await PlainAsync(query) ?? DnsMessage.ServFail(query);
    }

    async Task<byte[]?> PlainAsync(byte[] query)
    {
        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await udp.SendToAsync(query, SocketFlags.None, plainFallback, cts.Token);
            var buf = new byte[4096];
            int n = await udp.ReceiveAsync(buf, SocketFlags.None, cts.Token);
            return buf[..n];
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        foreach (var s in sockets)
            s.Dispose();
        try { await Task.WhenAll(loops); } catch { }
        foreach (var up in upstreams)
            up.Client.Dispose();
    }
}
