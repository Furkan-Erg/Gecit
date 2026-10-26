using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Gecit.Dns;

namespace Gecit.Net;

sealed record LanNetwork(string Adapter, string Kind, string? Ssid, IPAddress LocalIp, int Prefix, IPAddress Gateway, int InterfaceIndex)
{
    public string Cidr => $"{LocalIp}/{Prefix}";
}

sealed record LanDevice(
    string Mac, string Ip, long? PingMs, string? Name, string? NameSource, string? Vendor, string? Model,
    string Kind, bool IsSelf, bool IsGateway, bool RandomMac);

// Finds the devices on the local network without admin rights: a ping sweep makes Windows ARP every
// address (devices that ignore ping still answer ARP), then the ARP table gives IP + MAC. Names come
// from the modem's DNS (DHCP host names), mDNS (Apple, Android, Linux), NetBIOS (Windows) and UPnP
// (TVs, consoles, modems, printers).
static class LanScanner
{
    public static LanNetwork? CurrentNetwork()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            var props = nic.GetIPProperties();
            var gw = props.GatewayAddresses.Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            var addr = props.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
            if (gw is null || addr is null || !IsPrivate(addr.Address))
                continue;
            bool wifi = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            return new LanNetwork(nic.Name, wifi ? "wifi" : "ethernet", wifi ? CurrentSsid() : null,
                addr.Address, addr.PrefixLength, gw, props.GetIPv4Properties().Index);
        }
        return null;
    }

    public static async Task<List<LanDevice>> ScanAsync(LanNetwork net, CancellationToken ct = default)
    {
        // Big subnets (a /16 at work) would mean 65k pings: stay within our own /24.
        byte[] baseIp = net.LocalIp.GetAddressBytes();
        var hosts = Enumerable.Range(1, 254).Select(i => new IPAddress([baseIp[0], baseIp[1], baseIp[2], (byte)i]))
            .Where(ip => !ip.Equals(net.LocalIp)).ToList();

        // Two rounds: a phone in power saving often misses the first ARP.
        var rtt = new ConcurrentDictionary<string, long>();
        for (int round = 0; round < 2; round++)
        {
            await Task.WhenAll(hosts.Select(async ip =>
            {
                using var p = new Ping();
                try
                {
                    var r = await p.SendPingAsync(ip, TimeSpan.FromMilliseconds(1000), null, null, ct);
                    if (r.Status == IPStatus.Success)
                        rtt.AddOrUpdate(ip.ToString(), r.RoundtripTime, (_, old) => Math.Min(old, r.RoundtripTime));
                }
                catch (PingException) { }
            }));
        }

        var found = ArpTable(net.InterfaceIndex)
            .Where(e => e.Ip.GetAddressBytes() is var b && b[0] == baseIp[0] && b[1] == baseIp[1] && b[2] == baseIp[2] && b[3] is > 0 and < 255)
            .ToDictionary(e => e.Ip.ToString(), e => e.Mac);
        string selfMac = SelfMac(net) ?? "";
        found[net.LocalIp.ToString()] = selfMac;

        var upnpTask = UpnpAsync(found.Keys.ToHashSet(), ct);
        var infoTasks = found.Keys.Where(ip => ip != net.LocalIp.ToString())
            .ToDictionary(ip => ip, ip => DeviceApiAsync(IPAddress.Parse(ip), ct));
        var upnp = await upnpTask;
        foreach (var (ip, task) in infoTasks)
            if (await task is { } info)
                upnp[ip] = info;   // the device's own API knows more than its UPnP description
        // A modem's UPnP name is the generic "Internet Home Gateway Device"; its maker is what's useful.
        foreach (var (ip, u) in upnp.ToList())
            if (u.DeviceType?.Contains("InternetGatewayDevice", StringComparison.OrdinalIgnoreCase) == true)
                upnp[ip] = u with { FriendlyName = null, Model = null };
        var names = new ConcurrentDictionary<string, (string Name, string Source)>();
        await Task.WhenAll(found.Keys.Where(ip => ip != net.LocalIp.ToString()).Select(async ip =>
        {
            var addr = IPAddress.Parse(ip);
            // Ask all three at once; take the most descriptive one.
            var mdns = PtrAsync(addr, 5353, addr, ct);
            var modem = PtrAsync(net.Gateway, 53, addr, ct);
            var nb = NetBiosAsync(addr, ct);
            if (CleanHost(await mdns) is { } m) names[ip] = (m, "mDNS");
            else if (await nb is { } n) names[ip] = (n, "NetBIOS");
            else if (CleanHost(await modem) is { } d) names[ip] = (d, "modem");
        }));

        var list = new List<LanDevice>();
        foreach (var (ip, mac) in found)
        {
            bool self = ip == net.LocalIp.ToString();
            bool gateway = ip == net.Gateway.ToString();
            bool random = mac.Length > 0 && OuiDatabase.IsRandomized(mac);
            string? vendor = mac.Length > 0 ? OuiDatabase.Lookup(mac) : null;
            upnp.TryGetValue(ip, out var u);
            string? name = self ? Environment.MachineName : u?.FriendlyName;
            string? source = self ? "Windows" : u?.FriendlyName is not null ? "UPnP" : null;
            if (name is null && names.TryGetValue(ip, out var nm))
                (name, source) = nm;
            string? model = u is null ? null : string.Join(" ", new[] { u.Manufacturer, u.Model }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
            list.Add(new LanDevice(mac, ip, self ? 0 : rtt.TryGetValue(ip, out long ms) ? ms : null, name, source, vendor,
                string.IsNullOrEmpty(model) ? null : model, GuessKind(self, gateway, random, vendor, name, u), self, gateway, random));
        }
        return list.OrderBy(d => d.IsGateway ? 0 : d.IsSelf ? 1 : 2)
            .ThenBy(d => IPAddress.Parse(d.Ip).GetAddressBytes()[3]).ToList();
    }

    static string GuessKind(bool self, bool gateway, bool random, string? vendor, string? name, UpnpInfo? u)
    {
        if (gateway) return "router";
        if (self) return "pc";
        string text = $" {name} {u?.DeviceType} {u?.Model} {u?.Manufacturer} {vendor} ".ToLowerInvariant().Replace('[', ' ').Replace(']', ' ');
        bool Has(params string[] words) => words.Any(w => text.Contains(w, StringComparison.Ordinal));
        if (Has(" tv ", " tv-", "smarttv", "mediarenderer", "tizen", "webos", "bravia", "chromecast", "roku", "fire tv", "android tv")) return "tv";
        if (Has("playstation", "xbox", "nintendo", "ps4", "ps5")) return "console";
        if (Has("printer", "epson", "canon", "brother", "laserjet", "deskjet", "kyocera")) return "printer";
        if (Has("iphone", "ipad", "android", "galaxy", "redmi", "xiaomi", "oppo", "vivo", "huawei", "honor", "realme", "oneplus", "pixel")) return "phone";
        if (Has("espressif", "tuya", "akıllı ev", "raspberry", "shelly", "sonoff")) return "iot";
        if (Has("internetgatewaydevice", "access point", "tp-link", "tenda", "zyxel", "mikrotik", "ubiquiti", "netgear", "d-link", "modem")) return "router";
        if (Has("macbook", "imac", "desktop-", "laptop-", "-pc", "intel", "realtek", "dell", "lenovo", "msi", "gigabyte", "foxconn", "wi-fi modülü")) return "pc";
        if (random) return "phone";   // privacy MACs are almost always phones/tablets
        return "unknown";
    }

    // "Fuat-iPhone.local" / "android-1234.lan" -> readable host name; generic router names dropped.
    static string? CleanHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return null;
        string h = host.TrimEnd('.');
        foreach (string suffix in new[] { ".local", ".lan", ".home", ".domain", ".localdomain", ".router", ".ttnet", ".superonline", ".home.arpa" })
            if (h.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                h = h[..^suffix.Length];
        if (h.EndsWith(".in-addr.arpa", StringComparison.OrdinalIgnoreCase) || h.Length == 0 || IPAddress.TryParse(h, out _))
            return null;
        return h;
    }

    // ---- ARP table ----

    readonly record struct ArpEntry(IPAddress Ip, string Mac);

    static List<ArpEntry> ArpTable(int ifIndex)
    {
        int size = 0;
        GetIpNetTable(IntPtr.Zero, ref size, false);
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpNetTable(buf, ref size, true) != 0)
                return [];
            int count = Marshal.ReadInt32(buf);
            var list = new List<ArpEntry>(count);
            int rowSize = Marshal.SizeOf<MibIpNetRow>();
            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibIpNetRow>(buf + 4 + i * rowSize);
                // 3 = dynamic, 4 = static; 2 = invalid (no ARP answer).
                if (row.Index != ifIndex || row.Type is not (3 or 4) || row.PhysAddrLen != 6)
                    continue;
                var mac = row.PhysAddr.AsSpan(0, 6);
                if (mac.IndexOfAnyExcept((byte)0) < 0 || mac.IndexOfAnyExcept((byte)0xFF) < 0 || (mac[0] & 1) != 0)
                    continue;   // empty, broadcast, multicast
                list.Add(new ArpEntry(new IPAddress(BitConverter.GetBytes(row.Addr)), string.Join(":", row.PhysAddr[..6].Select(x => x.ToString("X2")))));
            }
            return list;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static string? SelfMac(LanNetwork net)
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == net.Adapter);
        var b = nic?.GetPhysicalAddress().GetAddressBytes();
        return b is { Length: 6 } ? string.Join(":", b.Select(x => x.ToString("X2"))) : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MibIpNetRow
    {
        public int Index;
        public int PhysAddrLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] PhysAddr;
        public uint Addr;
        public int Type;
    }

    [DllImport("iphlpapi.dll")]
    static extern int GetIpNetTable(IntPtr table, ref int size, bool sort);

    // ---- Names ----

    // PTR query for the device's reverse name: to the modem (port 53) or to the device itself (mDNS, 5353).
    static async Task<string?> PtrAsync(IPAddress server, int port, IPAddress target, CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(800);
        try
        {
            ushort id = (ushort)Random.Shared.Next(1, 65535);
            byte[] q = DnsMessage.BuildQuery(DnsMessage.ReverseName(target), type: 12, id: id);
            await udp.SendAsync(q, new IPEndPoint(server, port), cts.Token);
            while (true)
            {
                var r = await udp.ReceiveAsync(cts.Token);
                // mDNS responders answer with id 0.
                if (r.Buffer.Length > 12 && (DnsMessage.Id(r.Buffer) == id || port == 5353))
                    return DnsMessage.ReadPtr(r.Buffer);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    // NetBIOS node status ("nbtstat -A"): the computer name of Windows PCs and Samba boxes.
    static async Task<string?> NetBiosAsync(IPAddress ip, CancellationToken ct)
    {
        byte[] q = new byte[50];
        q[0] = 0x4E; q[1] = 0x42;                 // id
        q[5] = 1;                                 // one question
        q[12] = 0x20;                             // encoded "*" padded to 16 bytes
        q[13] = (byte)'C'; q[14] = (byte)'K';
        for (int i = 15; i < 45; i++) q[i] = (byte)'A';
        q[47] = 0x21;                             // NBSTAT
        q[49] = 1;                                // IN
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(800);
        try
        {
            await udp.SendAsync(q, new IPEndPoint(ip, 137), cts.Token);
            var r = (await udp.ReceiveAsync(cts.Token)).Buffer;
            // Header 12 + name 34 + type/class/ttl 8 + rdlength 2 -> name count at 56, then 18-byte entries.
            if (r.Length < 57 + 18 || r[56] == 0)
                return null;
            for (int i = 0; i < r[56] && 57 + (i + 1) * 18 <= r.Length; i++)
            {
                int o = 57 + i * 18;
                bool group = (r[o + 16] & 0x80) != 0;
                if (r[o + 15] == 0x00 && !group)   // workstation service, unique name = the computer name
                    return Encoding.ASCII.GetString(r, o, 15).Trim();
            }
            return null;
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException) { return null; }
    }

    // ---- UPnP / SSDP ----

    sealed record UpnpInfo(string? FriendlyName, string? Manufacturer, string? Model, string? DeviceType);

    static async Task<Dictionary<string, UpnpInfo>> UpnpAsync(HashSet<string> lanIps, CancellationToken ct)
    {
        var locations = new Dictionary<string, Uri>();
        using (var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)))
        {
            byte[] search = Encoding.ASCII.GetBytes(
                "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: upnp:rootdevice\r\n\r\n");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(2500);
            try
            {
                await udp.SendAsync(search, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900), cts.Token);
                await Task.Delay(100, cts.Token);
                await udp.SendAsync(search, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900), cts.Token);
                while (true)
                {
                    var r = await udp.ReceiveAsync(cts.Token);
                    string from = r.RemoteEndPoint.Address.ToString();
                    string text = Encoding.ASCII.GetString(r.Buffer);
                    string? loc = text.Split("\r\n").FirstOrDefault(l => l.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))?[9..].Trim();
                    // Only fetch descriptions from the device that answered, on the LAN.
                    if (loc is not null && lanIps.Contains(from) && !locations.ContainsKey(from)
                        && Uri.TryCreate(loc, UriKind.Absolute, out var uri) && uri.Scheme == "http" && uri.Host == from)
                        locations[from] = uri;
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3), MaxResponseContentBufferSize = 256 * 1024 };
        var result = new ConcurrentDictionary<string, UpnpInfo>();
        await Task.WhenAll(locations.Select(async kv =>
        {
            try
            {
                var xml = XDocument.Parse(await http.GetStringAsync(kv.Value, ct));
                var dev = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "device");
                string? V(string n) => dev?.Elements().FirstOrDefault(e => e.Name.LocalName == n)?.Value.Trim();
                if (dev is not null)
                    result[kv.Key] = new UpnpInfo(V("friendlyName"), V("manufacturer"), V("modelName"), V("deviceType"));
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Xml.XmlException) { }
        }));
        return new Dictionary<string, UpnpInfo>(result);
    }

    // ---- Smart TV / streamer APIs ----

    // Read-only info endpoints that need no pairing: Samsung Tizen TVs (8001), Chromecast / Google TV (8008)
    // and Roku (8060). A closed port fails fast; an open one answers with the name the owner gave it.
    static async Task<UpnpInfo?> DeviceApiAsync(IPAddress ip, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2), MaxResponseContentBufferSize = 128 * 1024 };

        async Task<UpnpInfo?> Samsung()
        {
            using var doc = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync($"http://{ip}:8001/api/v2/", ct));
            var dev = doc.RootElement.GetProperty("device");
            string? Get(string n) => dev.TryGetProperty(n, out var v) ? v.GetString() : null;
            return new UpnpInfo(Get("name")?.Replace("[TV] ", ""), "Samsung", Get("modelName"), Get("type") ?? "Samsung SmartTV");
        }
        async Task<UpnpInfo?> Cast()
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                await http.GetStringAsync($"http://{ip}:8008/setup/eureka_info?params=name,device_info", ct));
            var root = doc.RootElement;
            string? model = root.TryGetProperty("device_info", out var di) && di.TryGetProperty("model_name", out var m) ? m.GetString() : null;
            return new UpnpInfo(root.TryGetProperty("name", out var n) ? n.GetString() : null, "Google", model ?? "Chromecast", "Chromecast TV");
        }
        async Task<UpnpInfo?> Roku()
        {
            var xml = XDocument.Parse(await http.GetStringAsync($"http://{ip}:8060/query/device-info", ct));
            string? V(string n) => xml.Root?.Element(n)?.Value;
            return new UpnpInfo(V("user-device-name") ?? V("friendly-device-name"), "Roku", V("model-name"), "Roku TV");
        }
        async Task<UpnpInfo?> Try(Func<Task<UpnpInfo?>> probe)
        {
            try { return await probe(); }
            catch (Exception e) when (IsProbeFailure(e)) { return null; }
        }

        var results = await Task.WhenAll(Try(Samsung), Try(Cast), Try(Roku));
        return results.FirstOrDefault(r => r is not null);
    }
    static bool IsProbeFailure(Exception e) =>
        e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or System.Xml.XmlException
            or KeyNotFoundException or InvalidOperationException;

    // ---- helpers ----

    static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    static string? CurrentSsid()
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", "wlan show interfaces")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            // "    SSID                   : EvAgi"  (BSSID line comes after and starts with "BSSID")
            var line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("SSID", StringComparison.Ordinal));
            int colon = line?.IndexOf(':') ?? -1;
            return colon > 0 ? line![(colon + 1)..].Trim() : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }
}
