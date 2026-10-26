using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Gecit.Game;

// Freezing/thawing whole processes and system-wide CPU/RAM numbers.
static partial class NativeProcess
{
    const uint ProcessSuspendResume = 0x0800;
    const uint ProcessSetQuota = 0x0100;
    const uint ProcessQueryLimitedInformation = 0x1000;

    // Suspends every thread of the process, then pushes its memory out to the page file
    // so the game gets the physical RAM. Pages come back on demand after Resume.
    public static bool SuspendAndTrim(int pid)
    {
        var h = OpenProcess(ProcessSuspendResume | ProcessSetQuota | ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero)
            return false;
        try
        {
            if (NtSuspendProcess(h) != 0)
                return false;
            K32EmptyWorkingSet(h);
            return true;
        }
        finally { CloseHandle(h); }
    }

    public static bool Resume(int pid)
    {
        var h = OpenProcess(ProcessSuspendResume, false, pid);
        if (h == IntPtr.Zero)
            return false;
        try { return NtResumeProcess(h) == 0; }
        finally { CloseHandle(h); }
    }

    // Start time identifies a process across PID reuse.
    public static long StartTicks(Process p)
    {
        try { return p.StartTime.ToUniversalTime().Ticks; }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return 0; }
    }

    public static (long TotalBytes, long AvailableBytes) Memory()
    {
        var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        GlobalMemoryStatusEx(ref m);
        return ((long)m.TotalPhys, (long)m.AvailPhys);
    }

    static long lastIdle, lastTotal;

    // Percent of CPU busy since the previous call.
    public static int CpuPercent()
    {
        GetSystemTimes(out long idle, out long kernel, out long user);
        long total = kernel + user;   // kernel time includes idle
        long dIdle = idle - lastIdle, dTotal = total - lastTotal;
        lastIdle = idle;
        lastTotal = total;
        return dTotal <= 0 ? 0 : (int)Math.Round(100.0 * (dTotal - dIdle) / dTotal);
    }

    // Windows' own signal that a Direct3D exclusive-fullscreen app (a game) is in front.
    public static bool D3DFullscreenRunning() =>
        SHQueryUserNotificationState(out int state) == 0 && state == 3;   // QUNS_RUNNING_D3D_FULL_SCREEN

    public static int ForegroundPid()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        return (int)pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll")] private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseHandle(IntPtr h);
    [LibraryImport("ntdll.dll")] private static partial int NtSuspendProcess(IntPtr h);
    [LibraryImport("ntdll.dll")] private static partial int NtResumeProcess(IntPtr h);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool K32EmptyWorkingSet(IntPtr h);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx m);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetSystemTimes(out long idle, out long kernel, out long user);
    [LibraryImport("shell32.dll")] private static partial int SHQueryUserNotificationState(out int state);
    [LibraryImport("user32.dll")] private static partial IntPtr GetForegroundWindow();
    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
