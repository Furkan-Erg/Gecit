using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Win32;

namespace Gecit.Dns;

sealed record Adapter(int Index, string Id, string Name, string[] V4Dns, string[] V6Dns);

// Reads (unelevated) and changes (elevated) the DNS servers of the adapters that carry internet traffic.
static class SystemDns
{
    sealed record BackupEntry(int Index, string Id, string Name, string V4Static, string V6Static);

    // Up, non-loopback adapters with an IPv4 default gateway.
    public static List<Adapter> Adapters() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Select(n => (n, p: n.GetIPProperties()))
            .Where(x => x.p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
            .Select(x => new Adapter(
                x.p.GetIPv4Properties().Index,
                x.n.Id,
                x.n.Name,
                x.p.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToArray(),
                x.p.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6SiteLocal)
                    .Select(a => a.ToString()).ToArray()))
            .ToList();

    public static void Set(string[] v4, string[] v6)
    {
        var adapters = Adapters();
        if (adapters.Count == 0)
            throw new InvalidOperationException("İnternete bağlı ağ bağdaştırıcısı bulunamadı.");
        Backup(adapters);
        foreach (var a in adapters)
        {
            SetStatic("ipv4", a.Index, v4);
            // IPv6 may be disabled on the adapter; that's fine.
            try { SetStatic("ipv6", a.Index, v6); } catch (InvalidOperationException) { }
        }
    }

    // Puts back what was there before Geçit's first change (static servers, or DHCP).
    public static void Restore()
    {
        var entries = File.Exists(AppPaths.DnsBackupFile)
            ? JsonSerializer.Deserialize<List<BackupEntry>>(File.ReadAllText(AppPaths.DnsBackupFile)) ?? []
            : [];
        // Adapters that appeared since the backup go back to DHCP too.
        var current = Adapters();
        foreach (var a in current.Where(a => entries.All(e => e.Id != a.Id)))
            entries.Add(new(a.Index, a.Id, a.Name, "", ""));

        foreach (var e in entries)
        {
            int index = current.FirstOrDefault(a => a.Id == e.Id)?.Index ?? e.Index;
            try { Reset("ipv4", index, e.V4Static); } catch (InvalidOperationException) { }
            try { Reset("ipv6", index, e.V6Static); } catch (InvalidOperationException) { }
        }
        File.Delete(AppPaths.DnsBackupFile);
    }

    static void Backup(List<Adapter> adapters)
    {
        // Only the first change is backed up; later changes must not overwrite the original settings.
        if (File.Exists(AppPaths.DnsBackupFile))
            return;
        var entries = adapters.Select(a => new BackupEntry(a.Index, a.Id, a.Name,
            StaticServers(@"Tcpip\Parameters\Interfaces", a.Id), StaticServers(@"Tcpip6\Parameters\Interfaces", a.Id)));
        Directory.CreateDirectory(AppPaths.InstallRoot);
        File.WriteAllText(AppPaths.DnsBackupFile, JsonSerializer.Serialize(entries));
    }

    // Manually configured servers live in NameServer; DHCP-provided ones don't.
    static string StaticServers(string key, string id)
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{key}\{id}");
        return (k?.GetValue("NameServer") as string ?? "").Trim();
    }

    static void Reset(string family, int index, string staticServers)
    {
        string[] servers = staticServers.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (servers.Length > 0)
            SetStatic(family, index, servers);
        else
            ServiceTool.Netsh("interface", family, "set", "dnsservers", $"name={index}", "source=dhcp");
    }

    static void SetStatic(string family, int index, string[] servers)
    {
        ServiceTool.Netsh("interface", family, "set", "dnsservers", $"name={index}", "source=static",
            $"address={servers[0]}", "register=primary", "validate=no");
        for (int i = 1; i < servers.Length; i++)
            ServiceTool.Netsh("interface", family, "add", "dnsservers", $"name={index}",
                $"address={servers[i]}", $"index={i + 1}", "validate=no");
    }
}
