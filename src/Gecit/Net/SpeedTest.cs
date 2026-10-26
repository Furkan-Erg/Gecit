using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;

namespace Gecit.Net;

sealed record SpeedProgress(string Phase, double? DownMbps, double? UpMbps, int? IdleMs, int? LoadedMs, double Fraction);

sealed record SpeedResult(int IdleMs, double IdleJitterMs, double DownMbps, double UpMbps, int DownLoadedMs, int UpLoadedMs, string Grade);

// Download/upload against Cloudflare's speed test endpoints with several parallel streams,
// plus latency under load ("bufferbloat"): how much ping grows while the line is full.
static class SpeedTest
{
    const string Base = "https://speed.cloudflare.com";
    const string PingHost = "1.1.1.1";
    static readonly TimeSpan PhaseLength = TimeSpan.FromSeconds(8);
    static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(1.5);   // skip TCP slow start

    public static async Task<SpeedResult> RunAsync(Action<SpeedProgress> progress, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            MaxConnectionsPerServer = 8,
            AutomaticDecompression = DecompressionMethods.None,
        };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        progress(new("ping", null, null, null, null, 0));
        var idle = await PingSeries(20, TimeSpan.FromMilliseconds(150), ct);
        int idleMs = (int)Median(idle);
        double jitter = idle.Count < 2 ? 0 : idle.Zip(idle.Skip(1), (a, b) => Math.Abs(a - b)).Average();

        var (down, downLoaded) = await Phase("download", 6, (s, c) => DownloadStream(http, s, c), idleMs, null, 0.05, progress, ct);
        var (up, upLoaded) = await Phase("upload", 4, (s, c) => UploadStream(http, s, c), idleMs, down, 0.55, progress, ct);

        int worst = Math.Max(downLoaded, upLoaded) - idleMs;
        // No final progress event: it would arrive after the result (posted asynchronously) and overwrite it.
        return new SpeedResult(idleMs, Math.Round(jitter, 1), Math.Round(down, 1), Math.Round(up, 1), downLoaded, upLoaded, Grade(worst));
    }

    // Grades as used by common bufferbloat tests: extra latency under load.
    static string Grade(int extraMs) => extraMs switch
    {
        < 5 => "A+",
        < 30 => "A",
        < 60 => "B",
        < 200 => "C",
        < 400 => "D",
        _ => "F",
    };

    sealed class Counter { public long Bytes; }

    static async Task<(double Mbps, int LoadedMs)> Phase(string name, int streams, Func<Counter, CancellationToken, Task> stream,
        int idleMs, double? downDone, double fractionStart, Action<SpeedProgress> progress, CancellationToken ct)
    {
        var counter = new Counter();
        using var phase = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workers = Enumerable.Range(0, streams).Select(_ => stream(counter, phase.Token)).ToList();
        var loaded = new List<long>();
        var sw = Stopwatch.StartNew();
        long bytesAtWarmUp = 0;
        double mbps = 0;
        using var pinger = new Ping();

        while (sw.Elapsed < PhaseLength)
        {
            await Task.Delay(250, ct);
            if (sw.Elapsed >= WarmUp)
            {
                if (bytesAtWarmUp == 0)
                    bytesAtWarmUp = Interlocked.Read(ref counter.Bytes);
                double secs = (sw.Elapsed - WarmUp).TotalSeconds;
                if (secs > 0.2)
                    mbps = (Interlocked.Read(ref counter.Bytes) - bytesAtWarmUp) * 8 / secs / 1e6;
                try
                {
                    var r = await pinger.SendPingAsync(PingHost, 1000);
                    if (r.Status == IPStatus.Success)
                        loaded.Add(r.RoundtripTime);
                }
                catch (PingException) { }
            }
            double fraction = fractionStart + 0.45 * Math.Min(1, sw.Elapsed / PhaseLength);
            progress(new(name, name == "download" ? mbps : downDone, name == "upload" ? mbps : null, idleMs,
                loaded.Count > 0 ? (int)Median(loaded) : null, fraction));
        }
        phase.Cancel();
        try { await Task.WhenAll(workers); }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException or IOException) { }
        ct.ThrowIfCancellationRequested();
        if (Interlocked.Read(ref counter.Bytes) == 0)
        {
            // Say why: a stream that failed for real (not by our cancel) carries the server's answer.
            var reason = workers.Select(w => w.Exception?.GetBaseException()).FirstOrDefault(e => e is HttpRequestException or IOException);
            throw new InvalidOperationException("Hız testi sunucusuna ulaşılamadı" + (reason is null ? "." : $": {reason.Message}"));
        }
        return (mbps, loaded.Count > 0 ? (int)Median(loaded) : idleMs);
    }

    static async Task DownloadStream(HttpClient http, Counter counter, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        while (!ct.IsCancellationRequested)
        {
            // Cloudflare refuses requests above ~50 MB with 403; streams simply loop.
            using var resp = await http.GetAsync($"{Base}/__down?bytes=50000000", HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            int n;
            while ((n = await s.ReadAsync(buf, ct)) > 0)
                Interlocked.Add(ref counter.Bytes, n);
        }
    }

    static async Task UploadStream(HttpClient http, Counter counter, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var content = new CountingContent(25_000_000, counter);
            using var resp = await http.PostAsync($"{Base}/__up", content, ct);
        }
    }

    // Streams random-ish bytes and counts what actually went out.
    sealed class CountingContent(long length, Counter counter) : HttpContent
    {
        static readonly byte[] Chunk = MakeChunk();

        static byte[] MakeChunk()
        {
            var b = new byte[64 * 1024];
            Random.Shared.NextBytes(b);   // incompressible
            return b;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            for (long sent = 0; sent < length; sent += Chunk.Length)
            {
                int n = (int)Math.Min(Chunk.Length, length - sent);
                await stream.WriteAsync(Chunk.AsMemory(0, n), ct);
                Interlocked.Add(ref counter.Bytes, n);
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long len)
        {
            len = length;
            return true;
        }
    }

    static async Task<List<long>> PingSeries(int count, TimeSpan gap, CancellationToken ct)
    {
        var list = new List<long>();
        using var p = new Ping();
        for (int i = 0; i < count; i++)
        {
            try
            {
                var r = await p.SendPingAsync(PingHost, 1000);
                if (r.Status == IPStatus.Success)
                    list.Add(r.RoundtripTime);
            }
            catch (PingException) { }
            await Task.Delay(gap, ct);
        }
        if (list.Count == 0)
            throw new InvalidOperationException("İnternete ping atılamıyor.");
        return list;
    }

    static double Median(List<long> xs)
    {
        var s = xs.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2.0;
    }
}
