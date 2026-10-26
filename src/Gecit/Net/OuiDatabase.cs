using System.IO.Compression;
using System.Reflection;

namespace Gecit.Net;

// MAC prefix -> manufacturer, from the embedded IEEE registry (tools\build-oui.ps1).
static class OuiDatabase
{
    static readonly Lazy<(Dictionary<string, string> L24, List<(string Prefix, string Name)> Long)> Data = new(Load);

    // Registry names are legal names; people know the brand.
    static readonly (string Starts, string Brand)[] Brands =
    [
        ("ASUSTek", "ASUS"), ("Samsung", "Samsung"), ("XIAOMI", "Xiaomi"), ("Xiaomi", "Xiaomi"), ("Beijing Xiaomi", "Xiaomi"),
        ("Huawei", "Huawei"), ("HUAWEI", "Huawei"), ("Honor Device", "Honor"), ("Guangdong Oppo", "OPPO"), ("vivo Mobile", "vivo"),
        ("OnePlus", "OnePlus"), ("Realme", "realme"), ("Sony Interactive", "Sony PlayStation"), ("Sony", "Sony"), ("Nintendo", "Nintendo"),
        ("Microsoft", "Microsoft"), ("LG Electronics", "LG"), ("LG Innotek", "LG"), ("Vestel", "Vestel"), ("Arçelik", "Arçelik"),
        ("Tp-Link", "TP-Link"), ("TP-LINK", "TP-Link"), ("zte", "ZTE"), ("ZTE", "ZTE"), ("Taicang T&W", "T&W (modem)"),
        ("Sagemcom", "Sagemcom (modem)"), ("Arcadyan", "Arcadyan (modem)"), ("AVM", "AVM FRITZ!"), ("Zyxel", "Zyxel"), ("ZyXEL", "Zyxel"),
        ("Hon Hai", "Foxconn"), ("Intel", "Intel"), ("Realtek", "Realtek"), ("Qualcomm", "Qualcomm"), ("MediaTek", "MediaTek"),
        ("Murata", "Murata (Wi-Fi modülü)"), ("AzureWave", "AzureWave (Wi-Fi modülü)"), ("Liteon", "Lite-On"), ("Lite-On", "Lite-On"),
        ("Espressif", "Espressif (akıllı ev cihazı)"), ("Tuya", "Tuya (akıllı ev cihazı)"), ("Raspberry Pi", "Raspberry Pi"),
        ("Amazon", "Amazon"), ("Google", "Google"), ("Hewlett Packard", "HP"), ("HP Inc", "HP"), ("Dell", "Dell"), ("Lenovo", "Lenovo"),
        ("Micro-Star", "MSI"), ("GIGA-BYTE", "Gigabyte"), ("Canon", "Canon"), ("Seiko Epson", "Epson"), ("Brother", "Brother"),
        ("Apple", "Apple"), ("Motorola", "Motorola"), ("Nokia", "Nokia"), ("Tenda", "Tenda"), ("D-Link", "D-Link"), ("Netgear", "Netgear"),
        ("TCL", "TCL"), ("Hisense", "Hisense"), ("Philips", "Philips"), ("Roku", "Roku"), ("Ubiquiti", "Ubiquiti"), ("MikroTik", "MikroTik"),
    ];

    // Phones and newer laptops make up a MAC per network for privacy (the "locally administered" bit).
    public static bool IsRandomized(string mac) => mac.Length >= 2 && (Convert.ToByte(mac[..2], 16) & 0x02) != 0;

    public static string? Lookup(string mac)
    {
        string hex = mac.Replace(":", "").Replace("-", "").ToUpperInvariant();
        if (hex.Length < 6 || IsRandomized(hex))
            return null;
        var (l24, longer) = Data.Value;
        // 28/36-bit blocks are sub-allocations inside a 24-bit "IEEE Registration Authority" block.
        foreach (var (prefix, name) in longer)
            if (hex.StartsWith(prefix, StringComparison.Ordinal))
                return Pretty(name);
        return l24.TryGetValue(hex[..6], out string? n) ? Pretty(n) : null;
    }

    static string? Pretty(string name)
    {
        if (name == "Private")
            return null;
        foreach (var (starts, brand) in Brands)
            if (name.StartsWith(starts, StringComparison.OrdinalIgnoreCase))
                return brand;
        return name;
    }

    static (Dictionary<string, string>, List<(string, string)>) Load()
    {
        var l24 = new Dictionary<string, string>(60000, StringComparer.Ordinal);
        var longer = new List<(string, string)>();
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("oui.txt.gz")!;
        using var reader = new StreamReader(new GZipStream(s, CompressionMode.Decompress));
        while (reader.ReadLine() is { } line)
        {
            int tab = line.IndexOf('\t');
            if (tab <= 0)
                continue;
            string key = line[..tab], name = line[(tab + 1)..];
            int slash = key.IndexOf('/');
            if (slash < 0)
                l24[key] = name;
            else
                longer.Add((key[..slash], name));
        }
        return (l24, longer);
    }
}
