using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Gecit.Game;
using Microsoft.Win32;

namespace Gecit.Boot;

sealed record BootItemImpact(string Id, string Name, long CpuMs, long IoBytes, bool Running);

sealed record BootReport(
    DateTime DesktopAt, DateTime MeasuredAt, double? ReadySeconds, double? BiosSeconds, List<BootItemImpact> Items);

// Measures a logon: how long after the desktop appears the PC is actually usable (CPU and disk calm
// down), and how much CPU/disk each startup item used in those first minutes. Windows keeps its own
// boot timings in an admin-only event log; this needs no admin and runs from the autostart entry (or
// once, from RunOnce, when the user asks for it).
static partial class BootMeter
{
    const string MutexName = "Gecit.BootMeasure";
    const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    const string RunOnceValue = "GecitBootMeasure";
    static readonly TimeSpan Cap = TimeSpan.FromMinutes(3);
    static string HistoryFile => Path.Combine(AppPaths.LocalDir, "boot-history.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // When the desktop appeared in this session: explorer.exe's start time.
    public static DateTime? DesktopTime()
    {
#if DEBUG
        // Testing without a reboot: pretend the desktop appeared N seconds ago.
        if (int.TryParse(Environment.GetEnvironmentVariable("GECIT_DESKTOP_AGO"), out int ago))
            return Process.GetCurrentProcess().StartTime.AddSeconds(-ago);
#endif
        int session = Process.GetCurrentProcess().SessionId;
        DateTime? first = null;
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            using (p)
            {
                try
                {
                    if (p.SessionId == session && (first is null || p.StartTime < first))
                        first = p.StartTime;
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return first;
    }

    public static bool JustLoggedOn =>
#if DEBUG
        Environment.GetEnvironmentVariable("GECIT_DESKTOP_AGO") is not null ||
#endif
        DesktopTime() is { } t && DateTime.Now - t < TimeSpan.FromMinutes(2);

    public static bool MeasureScheduled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunOnceKey);
            return k?.GetValue(RunOnceValue) is string;
        }
    }

    // One measurement at the next logon, for users who don't start Geçit with Windows.
    public static void ScheduleOnce(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunOnceKey);
        if (on)
            k.SetValue(RunOnceValue, $"\"{Environment.ProcessPath}\" --boot-measure");
        else
            k.DeleteValue(RunOnceValue, throwOnMissingValue: false);
    }

    public static List<BootReport> History()
    {
        try
        {
            return File.Exists(HistoryFile) ? JsonSerializer.Deserialize<List<BootReport>>(File.ReadAllText(HistoryFile)) ?? [] : [];
        }
        catch (JsonException) { return []; }
    }

    // Null when another Geçit process is already measuring this logon (autostart + RunOnce both fire).
    public static async Task<BootReport?> MeasureAsync(CancellationToken ct = default)
    {
        using var token = new Mutex(false, MutexName, out bool first);
        if (!first || DesktopTime() is not { } desktop)
            return null;
        if (History().Any(r => Math.Abs((r.DesktopAt - desktop).TotalSeconds) < 5))
            return null;   // this logon is already measured (Geçit restarted)

        using var disk = DiskBusy.TryCreate();
        NativeProcess.CpuPercent();
        disk?.Percent();
        DateTime? ready = null;
        int calm = 0;
        while (DateTime.Now - desktop < Cap)
        {
            await Task.Delay(1000, ct);
            int cpu = NativeProcess.CpuPercent();
            double diskBusy = disk?.Percent() ?? 0;
            // "Usable": five quiet seconds in a row. An HDD stays busy long after the CPU calms down.
            calm = cpu < 25 && diskBusy < 30 ? calm + 1 : 0;
            if (calm >= 5 && DateTime.Now - desktop > TimeSpan.FromSeconds(10))
            {
                ready = DateTime.Now.AddSeconds(-calm);
                break;
            }
        }

        // Like Task Manager: the first minute or so after logon decides an item's impact.
        var settle = desktop.AddSeconds(75) - DateTime.Now;
        if (settle > TimeSpan.Zero)
            await Task.Delay(settle, ct);

        var report = new BootReport(desktop, DateTime.Now, ready is null ? null : Math.Round((ready.Value - desktop).TotalSeconds, 1),
            BiosSeconds(), Impacts(desktop));
        var history = History();
        history.Add(report);
        File.WriteAllText(HistoryFile, JsonSerializer.Serialize(history.TakeLast(12).ToList(), Json));
        return report;
    }

    // Firmware (BIOS/UEFI) time of the last cold boot, recorded by Windows.
    public static double? BiosSeconds()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
        return k?.GetValue("FwPOSTTime") is int ms && ms > 0 ? Math.Round(ms / 1000.0, 1) : null;
    }

    // Task Manager's thresholds for "Başlangıç etkisi".
    public static string Impact(long cpuMs, long ioBytes) =>
        cpuMs > 1000 || ioBytes > 3_000_000 ? "high"
        : cpuMs > 300 || ioBytes > 300_000 ? "medium"
        : "low";

    // CPU time and I/O of every process an enabled startup item started after logon, children included
    // (Steam's web helpers, Edge's renderers...).
    static List<BootItemImpact> Impacts(DateTime desktop)
    {
        var procs = Snapshot();
        var children = procs.Values.GroupBy(p => p.ParentPid).ToDictionary(g => g.Key, g => g.ToList());
        var result = new List<BootItemImpact>();
        foreach (var item in StartupItems.List().Where(i => i.Enabled && i.ExePath is not null))
        {
            var roots = procs.Values.Where(p => p.Path is not null && p.Started >= desktop.AddSeconds(-30)
                && string.Equals(p.Path, item.ExePath, StringComparison.OrdinalIgnoreCase)).ToList();
            var all = new Dictionary<int, ProcInfo>();
            var stack = new Stack<ProcInfo>(roots);
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                if (!all.TryAdd(p.Pid, p))
                    continue;
                foreach (var c in children.GetValueOrDefault(p.Pid) ?? [])
                    if (c.Started >= p.Started)   // a PID can be reused by an unrelated older process
                        stack.Push(c);
            }
            result.Add(new BootItemImpact(item.Id, item.Name, all.Values.Sum(p => p.CpuMs), all.Values.Sum(p => p.IoBytes), roots.Count > 0));
        }
        return result;
    }

    sealed record ProcInfo(int Pid, int ParentPid, string? Path, DateTime Started, long CpuMs, long IoBytes);

    static Dictionary<int, ProcInfo> Snapshot()
    {
        var list = new Dictionary<int, ProcInfo>();
        IntPtr snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == new IntPtr(-1))
            return list;
        try
        {
            var e = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
            {
                int pid = (int)e.ProcessId;
                if (pid <= 4)
                    continue;
                IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
                if (h == IntPtr.Zero)
                    continue;
                try
                {
                    var buf = new char[1024];
                    int len = buf.Length;
                    string? path = QueryFullProcessImageNameW(h, 0, buf, ref len) ? new string(buf, 0, len) : null;
                    if (!GetProcessTimes(h, out long created, out _, out long kernel, out long user))
                        continue;
                    long io = GetProcessIoCounters(h, out var c) ? (long)(c.ReadTransferCount + c.WriteTransferCount) : 0;
                    list[pid] = new ProcInfo(pid, (int)e.ParentProcessId, path, DateTime.FromFileTime(created), (kernel + user) / 10_000, io);
                }
                finally { CloseHandle(h); }
            }
        }
        finally { CloseHandle(snap); }
        return list;
    }

    // Busy percent of all physical disks (100 - "% Idle Time") through PDH; English counter names
    // work on a Turkish Windows too.
    sealed partial class DiskBusy : IDisposable
    {
        IntPtr query, counter;

        public static DiskBusy? TryCreate()
        {
            var d = new DiskBusy();
            if (PdhOpenQueryW(null, IntPtr.Zero, out d.query) != 0)
                return null;
            if (PdhAddEnglishCounterW(d.query, @"\PhysicalDisk(_Total)\% Idle Time", IntPtr.Zero, out d.counter) != 0)
            {
                d.Dispose();
                return null;
            }
            PdhCollectQueryData(d.query);
            return d;
        }

        public double Percent()
        {
            if (PdhCollectQueryData(query) != 0
                || PdhGetFormattedCounterValue(counter, 0x200 /* PDH_FMT_DOUBLE */, out _, out var v) != 0)
                return 0;
            return Math.Clamp(100 - v.Value, 0, 100);
        }

        public void Dispose()
        {
            if (query != IntPtr.Zero)
                PdhCloseQuery(query);
            query = IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PdhValue { public uint Status; public double Value; }

        [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);
        [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
        [LibraryImport("pdh.dll")] private static partial int PdhCollectQueryData(IntPtr query);
        [LibraryImport("pdh.dll")] private static partial int PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhValue value);
        [LibraryImport("pdh.dll")] private static partial int PdhCloseQuery(IntPtr query);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry32
    {
        public uint Size, Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadTransferCount, WriteTransferCount, OtherTransferCount; }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref ProcessEntry32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref ProcessEntry32 e);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, char[] buf, ref int size);
    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr h, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll")] static extern bool GetProcessIoCounters(IntPtr h, out IoCounters c);
}
