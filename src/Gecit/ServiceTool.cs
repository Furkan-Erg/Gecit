using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;

namespace Gecit;

// Service and file helpers for the elevated installer process.
static class ServiceTool
{
    public static bool Exists(string name) =>
        ServiceController.GetServices().Concat(ServiceController.GetDevices())
            .Any(s => s.ServiceName.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static void CreateOrUpdate(string name, string binPath, string start, string displayName,
        string description, string sddl, string? account = null)
    {
        // Built-in accounts like LocalService have no password, but sc still wants an empty one.
        string[] accountArgs = account is null ? [] : ["obj=", account, "password=", ""];
        if (Exists(name))
            Sc(["config", name, "binPath=", binPath, "start=", start, .. accountArgs]);
        else
            Sc(["create", name, "binPath=", binPath, "start=", start, "DisplayName=", displayName, .. accountArgs]);
        Sc("description", name, description);
        Sc("sdset", name, sddl);
    }

    // Returns whether the service was running.
    public static bool Stop(string name)
    {
        if (!Exists(name))
            return false;
        using var sc = new ServiceController(name);
        if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
        {
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            return false;
        }
        sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
        return true;
    }

    public static void Start(string name)
    {
        using var sc = new ServiceController(name);
        if (sc.Status != ServiceControllerStatus.Running)
            sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
    }

    public static void Remove(string name)
    {
        Stop(name);
        if (Exists(name))
            Sc("delete", name);
    }

    public static void CopyFiles(string from, string to, string pattern)
    {
        if (!Directory.Exists(from) || !Directory.EnumerateFiles(from, pattern).Any())
            throw new FileNotFoundException($"Kurulacak dosyalar bulunamadı: {Path.Combine(from, pattern)}");

        foreach (string src in Directory.EnumerateFiles(from, pattern, SearchOption.AllDirectories))
        {
            string dst = Path.Combine(to, Path.GetRelativePath(from, src));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            // A loaded driver or running exe can't be overwritten, so skip files that are already identical.
            if (File.Exists(dst) && SameContent(src, dst))
                continue;
            File.Copy(src, dst, overwrite: true);
        }
    }

    static bool SameContent(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length)
            return false;
        using var fa = File.OpenRead(a);
        using var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return SHA256.HashData(fa).AsSpan().SequenceEqual(SHA256.HashData(fb));
    }

    public static void Sc(params string[] args) => Run(Path.Combine(Environment.SystemDirectory, "sc.exe"), args);

    public static void Netsh(params string[] args) => Run(Path.Combine(Environment.SystemDirectory, "netsh.exe"), args);

    static void Run(string exe, string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = oem,
            StandardErrorEncoding = oem,
        };
        foreach (string a in args)
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileNameWithoutExtension(exe)} {args[0]} başarısız ({p.ExitCode}): {output.Trim()}");
    }
}
