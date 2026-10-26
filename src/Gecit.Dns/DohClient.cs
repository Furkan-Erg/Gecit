using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace Gecit.Dns;

// RFC 8484 client. Connects to the provider's fixed IPs, so it never needs DNS itself
// (it may be the system's only resolver).
public sealed class DohClient : IDisposable
{
    static readonly MediaTypeHeaderValue DnsMessageType = new("application/dns-message");

    readonly HttpClient http;
    readonly Uri url;

    public DnsProvider Provider { get; }

    public DohClient(DnsProvider provider)
    {
        Provider = provider;
        url = new Uri(provider.DohUrl);
        var ips = provider.BootstrapIps;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            EnableMultipleHttp2Connections = true,
            ConnectCallback = async (ctx, ct) =>
            {
                Exception? last = null;
                foreach (var ip in ips)
                {
                    var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception e) when (e is SocketException or OperationCanceledException)
                    {
                        socket.Dispose();
                        last = e;
                        ct.ThrowIfCancellationRequested();
                    }
                }
                throw last ?? new SocketException((int)SocketError.HostUnreachable);
            },
        };
        http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<byte[]> QueryAsync(byte[] query, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var content = new ByteArrayContent(query);
        content.Headers.ContentType = DnsMessageType;
        // RFC 8484 wants HTTP/2; Quad9 answers HTTP/1.1 with 505. (HttpClient.DefaultRequestVersion
        // doesn't apply to hand-built requests, so it's set here.)
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content,
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
        req.Headers.Accept.ParseAdd("application/dns-message");
        using var resp = await http.SendAsync(req, cts.Token);
        resp.EnsureSuccessStatusCode();
        byte[] body = await resp.Content.ReadAsByteArrayAsync(cts.Token);
        if (body.Length < 12)
            throw new InvalidDataException("DoH yanıtı çok kısa");
        return body;
    }

    public void Dispose() => http.Dispose();
}
