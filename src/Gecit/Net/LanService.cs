using System.Text.Json;

namespace Gecit.Net;

sealed class KnownDevice
{
    public string Mac { get; set; } = "";
    public string? CustomName { get; set; }
    public string? Name { get; set; }
    public string? Vendor { get; set; }
    public string? Model { get; set; }
    public string Kind { get; set; } = "unknown";
    public string Ip { get; set; } = "";
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
}

sealed class KnownNetwork
{
    public DateTime FirstScan { get; set; }
    public Dictionary<string, KnownDevice> Devices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

sealed record LanDeviceView(
    string Mac, string Ip, long? PingMs, string Name, string? NameSource, string? CustomName, string? Vendor, string? Model,
    string Kind, bool IsSelf, bool IsGateway, bool RandomMac, bool IsNew, DateTime FirstSeen, DateTime LastSeen);

sealed record LanScanResult(
    string Adapter, string Kind, string? Ssid, string Cidr, string Gateway, DateTime At,
    List<LanDeviceView> Online, List<LanDeviceView> Offline, List<LanDeviceView> NewDevices);

// Remembers the devices seen on each network (keyed by the modem's MAC, so a laptop moving between
// home and a café keeps two separate lists) and tells which ones are new.
sealed class LanService
{
    static string StoreFile => Path.Combine(AppPaths.RoamingDir, "devices.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    readonly Dictionary<string, KnownNetwork> networks;
    readonly SemaphoreSlim scanning = new(1, 1);

    public LanScanResult? Last { get; private set; }
    public bool Scanning => scanning.CurrentCount == 0;

    public LanService()
    {
        try
        {
            networks = File.Exists(StoreFile)
                ? JsonSerializer.Deserialize<Dictionary<string, KnownNetwork>>(File.ReadAllText(StoreFile)) ?? new()
                : new();
        }
        catch (JsonException) { networks = new(); }
    }

    // A background check only makes sense where the user has looked at the list once.
    public bool HasBaseline => LanScanner.CurrentNetwork() is { } net && networks.ContainsKey(NetworkKeyGuess(net));

    public async Task<LanScanResult> ScanAsync(CancellationToken ct = default)
    {
        await scanning.WaitAsync(ct);
        try
        {
            var net = LanScanner.CurrentNetwork() ?? throw new InvalidOperationException("Bir yerel ağa (modeme) bağlı değilsin.");
            var found = await LanScanner.ScanAsync(net, ct);
            DateTime now = DateTime.Now;

            var gw = found.FirstOrDefault(d => d.IsGateway);
            string key = gw is { Mac.Length: > 0 } ? gw.Mac : NetworkKeyGuess(net);
            bool baseline = networks.TryGetValue(key, out var known);
            known ??= networks[key] = new KnownNetwork { FirstScan = now };
            lastKeys[net.Gateway.ToString()] = key;

            var online = new List<LanDeviceView>();
            var fresh = new List<LanDeviceView>();
            foreach (var d in found.Where(d => d.Mac.Length > 0))
            {
                bool isNew = !known.Devices.TryGetValue(d.Mac, out var k);
                k ??= known.Devices[d.Mac] = new KnownDevice { Mac = d.Mac, FirstSeen = now };
                // Keep the best name we ever saw: a phone answers mDNS only while awake.
                k.Name = d.Name ?? k.Name;
                k.Vendor = d.Vendor ?? k.Vendor;
                k.Model = d.Model ?? k.Model;
                k.Kind = d.Kind != "unknown" || k.Kind == "unknown" ? d.Kind : k.Kind;
                k.Ip = d.Ip;
                k.LastSeen = now;
                // "Yeni" badge for a day; devices of the very first scan are the baseline, not new.
                bool badge = k.FirstSeen > known.FirstScan.AddMinutes(1) && now - k.FirstSeen < TimeSpan.FromDays(1);
                var view = View(k, d.PingMs, d.NameSource, d.IsSelf, d.IsGateway, d.RandomMac, badge);
                online.Add(view);
                if (isNew && baseline && !d.IsSelf)
                    fresh.Add(view);
            }
            var onlineMacs = online.Select(o => o.Mac).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var offline = known.Devices.Values.Where(k => !onlineMacs.Contains(k.Mac)).OrderByDescending(k => k.LastSeen)
                .Select(k => View(k, null, null, false, false, OuiDatabase.IsRandomized(k.Mac), false)).ToList();
            Save();

            Last = new LanScanResult(net.Adapter, net.Kind, net.Ssid, net.Cidr, net.Gateway.ToString(), now, online, offline, fresh);
            return Last;
        }
        finally { scanning.Release(); }
    }

    public void Rename(string mac, string? name)
    {
        foreach (var n in networks.Values)
            if (n.Devices.TryGetValue(mac, out var d))
                d.CustomName = string.IsNullOrWhiteSpace(name) ? null : name.Trim()[..Math.Min(40, name.Trim().Length)];
        Save();
        Last = Last is null ? null : Last with { Online = Last.Online.Select(Refresh).ToList(), Offline = Last.Offline.Select(Refresh).ToList() };
    }

    public void Forget(string mac)
    {
        foreach (var n in networks.Values)
            n.Devices.Remove(mac);
        Save();
        if (Last is not null)
            Last = Last with { Offline = Last.Offline.Where(d => !d.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase)).ToList() };
    }

    LanDeviceView Refresh(LanDeviceView v)
    {
        var k = networks.Values.Select(n => n.Devices.GetValueOrDefault(v.Mac)).FirstOrDefault(d => d is not null);
        return k is null ? v : v with { CustomName = k.CustomName, Name = DisplayName(k, v.IsSelf, v.IsGateway, v.RandomMac) };
    }

    static LanDeviceView View(KnownDevice k, long? ping, string? source, bool self, bool gateway, bool random, bool isNew) =>
        new(k.Mac, k.Ip, ping, DisplayName(k, self, gateway, random), source, k.CustomName, k.Vendor, k.Model,
            k.Kind, self, gateway, random, isNew, k.FirstSeen, k.LastSeen);

    // A modem's own host name ("dsldevice", "RTK_GW") means nothing to people; "Modem" does.
    static string DisplayName(KnownDevice k, bool self, bool gateway, bool random) =>
        k.CustomName ?? (gateway ? "Modem" : k.Name)
        ?? (k.Vendor is not null ? $"{k.Vendor} cihazı" : random ? "Gizli MAC'li cihaz" : "Bilinmeyen cihaz");

    // Before the first scan we don't know the modem's MAC; the remembered key for this gateway IP, if any.
    readonly Dictionary<string, string> lastKeys = new();
    string NetworkKeyGuess(LanNetwork net) =>
        lastKeys.TryGetValue(net.Gateway.ToString(), out string? k) ? k
        : networks.FirstOrDefault(n => n.Value.Devices.Values.Any(d => d.Ip == net.Gateway.ToString() && d.Kind == "router")).Key
          ?? "ip:" + net.Gateway;

    void Save()
    {
        try { File.WriteAllText(StoreFile, JsonSerializer.Serialize(networks, Json)); }
        catch (IOException) { }
    }
}
