using System.Diagnostics;
using System.Globalization;

namespace Gecit.Game;

sealed record GpuSample(string Name, int TempC, int UtilPercent, int MemUsedMb, int MemTotalMb);

// NVIDIA cards report through nvidia-smi (installed with the driver). Other GPUs: no data.
static class GpuStats
{
    static readonly string? Exe = new[]
    {
        Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
    }.FirstOrDefault(File.Exists);

    public static async Task<GpuSample?> SampleAsync()
    {
        if (Exe is null)
            return null;
        var psi = new ProcessStartInfo(Exe, "--query-gpu=name,temperature.gpu,utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        try
        {
            using var p = Process.Start(psi)!;
            string line = (await p.StandardOutput.ReadLineAsync()) ?? "";
            await p.WaitForExitAsync();
            var f = line.Split(',', StringSplitOptions.TrimEntries);
            if (f.Length < 5)
                return null;
            int I(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
            return new(f[0], I(f[1]), I(f[2]), I(f[3]), I(f[4]));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
