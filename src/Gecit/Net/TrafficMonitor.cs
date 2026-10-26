using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace Gecit.Net;

sealed record AppTraffic(string Process, string Name, long RxRate, long TxRate, long RxTotal, long TxTotal);

// UI side of per-app traffic: owns the pipe, starts the elevated collector and folds PIDs into apps.
sealed class TrafficMonitor : IDisposable
{
    sealed class Totals
    {
        public string Name = "";
        public long RxRate, TxRate, RxTotal, TxTotal;
    }

    readonly Dictionary<string, Totals> apps = new(StringComparer.OrdinalIgnoreCase);
    readonly object gate = new();
    NamedPipeServerStream? pipe;
    CancellationTokenSource? cts;

    public bool Running { get; private set; }
    public DateTime? Since { get; private set; }

    public async Task StartAsync()
    {
        if (Running)
            return;
        string pipeName = $"gecit-netmon-{Environment.ProcessId}-{Guid.NewGuid():N}";
        pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        cts = new CancellationTokenSource();
        var helper = Elevated.StartDetached("netmon", pipeName, Environment.ProcessId.ToString());

        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(30));
            var waitForExit = helper.WaitForExitAsync(connect.Token);
            var connected = pipe.WaitForConnectionAsync(connect.Token);
            if (await Task.WhenAny(connected, waitForExit) != connected || !pipe.IsConnected)
            {
                Stop();
                string detail = File.Exists(AppPaths.ElevatedResultFile) ? File.ReadAllText(AppPaths.ElevatedResultFile) : "";
                throw new InvalidOperationException("İzleme başlatılamadı. " + detail);
            }
        }
        lock (gate)
            apps.Clear();
        Running = true;
        Since = DateTime.Now;
        _ = ReadLoop(pipe, cts.Token);
    }

    async Task ReadLoop(NamedPipeServerStream p, CancellationToken ct)
    {
        using var reader = new StreamReader(p);
        try
        {
            while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) is { } line)
            {
                var rows = JsonSerializer.Deserialize<TrafficRow[]>(line) ?? [];
                lock (gate)
                {
                    foreach (var t in apps.Values)
                        t.RxRate = t.TxRate = 0;
                    foreach (var r in rows)
                    {
                        if (!apps.TryGetValue(r.Process, out var t))
                            apps[r.Process] = t = new Totals { Name = r.Name };
                        t.RxRate += r.Rx;
                        t.TxRate += r.Tx;
                        t.RxTotal += r.Rx;
                        t.TxTotal += r.Tx;
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or JsonException) { }
        Running = false;
    }

    public List<AppTraffic> Snapshot()
    {
        lock (gate)
        {
            return apps.Select(kv => new AppTraffic(kv.Key, kv.Value.Name, kv.Value.RxRate, kv.Value.TxRate, kv.Value.RxTotal, kv.Value.TxTotal))
                .OrderByDescending(a => a.RxRate + a.TxRate)
                .ThenByDescending(a => a.RxTotal + a.TxTotal)
                .Take(25)
                .ToList();
        }
    }

    // Closing the pipe makes the elevated collector exit.
    public void Stop()
    {
        cts?.Cancel();
        pipe?.Dispose();
        pipe = null;
        Running = false;
    }

    public void Dispose() => Stop();
}
