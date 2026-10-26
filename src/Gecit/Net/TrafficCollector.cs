using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace Gecit.Net;

// Elevated side of per-app traffic: a kernel ETW session (TCP/IP + UDP/IP send/receive events)
// summed per PID and written to the UI's pipe once a second. Windows only allows elevated
// processes to start kernel sessions, so this runs as `Gecit.exe --elevated netmon <pipe> <uiPid>`.
static class TrafficCollector
{
    const string SessionName = "GecitNetMon";

    public static void Run(string pipeName, int uiPid)
    {
        using var ui = Process.GetProcessById(uiPid);
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
        pipe.Connect(5000);
        using var writer = new StreamWriter(pipe) { AutoFlush = true };

        var rx = new ConcurrentDictionary<int, long>();
        var tx = new ConcurrentDictionary<int, long>();
        // ETW delivers events up to a second late, often after a short-lived process (curl, an updater)
        // has exited. Process start/rundown events give every image name up front as a fallback.
        var images = new ConcurrentDictionary<int, string>();
        var names = new ConcurrentDictionary<int, (string Process, string Name)>();
        void Add(ConcurrentDictionary<int, long> d, int pid, int size)
        {
            d.AddOrUpdate(pid, size, (_, v) => v + size);
            names.GetOrAdd(pid, p => Describe(p, images));
        }

        // Default options stop a leftover session with the same name (e.g. after a crash).
        using var session = new TraceEventSession(SessionName) { StopOnDispose = true };
        session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP | KernelTraceEventParser.Keywords.Process);
        var k = session.Source.Kernel;
        k.ProcessStart += d => images[d.ProcessID] = Path.GetFileNameWithoutExtension(d.ImageFileName);
        k.ProcessDCStart += d => images[d.ProcessID] = Path.GetFileNameWithoutExtension(d.ImageFileName);
        k.TcpIpRecv += d => Add(rx, d.ProcessID, d.size);
        k.TcpIpRecvIPV6 += d => Add(rx, d.ProcessID, d.size);
        k.UdpIpRecv += d => Add(rx, d.ProcessID, d.size);
        k.UdpIpRecvIPV6 += d => Add(rx, d.ProcessID, d.size);
        k.TcpIpSend += d => Add(tx, d.ProcessID, d.size);
        k.TcpIpSendIPV6 += d => Add(tx, d.ProcessID, d.size);
        k.UdpIpSend += d => Add(tx, d.ProcessID, d.size);
        k.UdpIpSendIPV6 += d => Add(tx, d.ProcessID, d.size);
        var processing = Task.Run(() => session.Source.Process());

        try
        {
            while (!ui.HasExited && pipe.IsConnected)
            {
                Thread.Sleep(1000);
                var rows = rx.Keys.Union(tx.Keys).Distinct()
                    .Select(pid => new TrafficRow(pid, Take(rx, pid), Take(tx, pid), names[pid].Process, names[pid].Name))
                    .Where(r => r.Rx + r.Tx > 0)
                    .ToArray();
                writer.WriteLine(JsonSerializer.Serialize(rows));
            }
        }
        catch (IOException) { }   // the UI closed the pipe: done
        session.Stop();
        processing.Wait(TimeSpan.FromSeconds(3));
    }

    static long Take(ConcurrentDictionary<int, long> d, int pid) => d.TryRemove(pid, out long v) ? v : 0;

    static (string, string) Describe(int pid, ConcurrentDictionary<int, string> images)
    {
        if (pid is 0 or 4 or -1)
            return ("System", "Windows (sistem)");
        try
        {
            using var p = Process.GetProcessById(pid);
            try
            {
                if (p.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } d)
                    return (p.ProcessName, d.Trim());
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            return (p.ProcessName, p.ProcessName);
        }
        catch (ArgumentException)
        {
            return images.TryGetValue(pid, out string? image) && image.Length > 0
                ? (image, image)
                : ($"pid {pid}", "Kapanmış işlem");
        }
    }
}

sealed record TrafficRow(int Pid, long Rx, long Tx, string Process, string Name);
