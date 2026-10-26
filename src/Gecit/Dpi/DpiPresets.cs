namespace Gecit.Dpi;

sealed record DpiPreset(string Id, string Name, string Description, string BaseArgs, bool UsesTtl)
{
    // GoodbyeDPI-Turkey adds Yandex DNS on port 1253 to beat DNS blocking. When Akıllı DNS is on,
    // that redirect would override the user's DNS, so it's only added in automatic DNS mode.
    public string Args(bool dnsRedirect) => dnsRedirect ? $"{BaseArgs} {DpiPresets.YandexDnsRedirect}" : BaseArgs;
}

// The method sets shipped by GoodbyeDPI-Turkey (windows/*.cmd). Its 7 scripts are these 4 methods,
// each with or without the DNS redirect.
static class DpiPresets
{
    public const string YandexDnsRedirect =
        "--dns-addr 77.88.8.8 --dns-port 1253 --dnsv6-addr 2a02:6b8::feed:0ff --dnsv6-port 1253";

    public static readonly IReadOnlyList<DpiPreset> All =
    [
        new("standart", "Standart", "Önerilen. Çoğu operatörde çalışır.", "-5 --set-ttl 5", true),
        new("ttlsiz", "TTL'siz", "Bazı siteler yavaş açılıyorsa veya hiç açılmıyorsa bunu dene.", "-5", false),
        new("saglam", "Sağlam mod", "Yanlış sağlama toplamlı sahte paketler, QUIC engelli.", "-9", false),
        new("ttl3", "Superonline (TTL 3)", "Superonline fiberde Discord açılmıyorsa.", "--set-ttl 3", true),
    ];

    // Ids from v0.1, when DNS redirect was part of the preset.
    static readonly Dictionary<string, string> Legacy = new()
    {
        ["alt1"] = "ttl3", ["alt2"] = "ttlsiz", ["alt3"] = "ttl3",
        ["alt4"] = "ttlsiz", ["alt5"] = "saglam", ["alt6"] = "saglam",
    };

    public static DpiPreset Default => All[0];

    public static DpiPreset Get(string? id)
    {
        if (id is not null && Legacy.TryGetValue(id, out var mapped))
            id = mapped;
        return All.FirstOrDefault(p => p.Id == id) ?? Default;
    }

    // Splits installed service args back into (method, dnsRedirect). Method is null for unknown args.
    public static (DpiPreset? Preset, bool DnsRedirect) Parse(string args)
    {
        args = args.Trim();
        bool redirect = args.Contains(YandexDnsRedirect);
        string baseArgs = args.Replace(YandexDnsRedirect, "").Trim();
        return (All.FirstOrDefault(p => p.BaseArgs == baseArgs), redirect);
    }
}
