using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Gecit.Store;

sealed record Demo(
    int Id, int GameId, string Name, string? Description, string? Image, string[] Tags,
    DateTimeOffset? Date, string? DateText, bool ComingSoon, bool Turkish, int Deck,
    int Reviews, int ReviewPercent, string? ReviewLabel, string? GameDate, string Url, string[]? Players);

sealed record DemoStatus(bool ComingSoon, DateTimeOffset? Date, string? DateText);

sealed record DemoPage(string List, int Start, int Total, List<Demo> Items, DateTime At);

// The demo lists the Steam store itself shows: newest, upcoming (with their dates) and popular.
// Anonymous endpoints only: the store search gives the order, IStoreBrowseService/GetItems the details.
static partial class DemoFeed
{
    const string SearchUrl = "https://store.steampowered.com/search/results/";
    const string ItemsUrl = "https://api.steampowered.com/IStoreBrowseService/GetItems/v1/";
    const string TagsUrl = "https://api.steampowered.com/IStoreService/GetTagList/v1/?language=turkish";
    const string AssetBase = "https://shared.fastly.steamstatic.com/store_item_assets/";
    const int PageSize = 30;
    const int TurkishLanguage = 21;   // ELanguage
    static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(20);
    static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    internal static readonly HttpClient http = CreateClient();
    static readonly Dictionary<string, DemoPage> cache = [];
    static Dictionary<int, string>? tagNames;

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Mozilla/5.0 Gecit/{Updater.Current}");
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("tr-TR,tr;q=0.9");
        return c;
    }

    [GeneratedRegex("data-ds-appid=\"(\\d+)")]
    private static partial Regex AppIdRegex();

    // Store search "category3" values (player features); several would mean all of them at once.
    public static readonly Dictionary<string, string> PlayerFilters = new()
    {
        ["all"] = "",
        ["coop"] = "&category3=9",
        ["online"] = "&category3=38",
        ["multi"] = "&category3=1",
    };

    public static async Task<DemoPage> GetAsync(string list, string players, int start, bool refresh, CancellationToken ct = default)
    {
        string order = list switch
        {
            "new" => "&sort_by=Released_DESC",
            "soon" => "&filter=comingsoon",   // the store sorts these by date, soonest first
            "popular" => "",
            _ => throw new InvalidOperationException($"Bilinmeyen liste: {list}"),
        };
        string filter = PlayerFilters.GetValueOrDefault(players) ?? throw new InvalidOperationException($"Bilinmeyen filtre: {players}");
        string key = $"{list}|{players}|{start}";
        if (!refresh && cache.TryGetValue(key, out var hit) && DateTime.Now - hit.At < CacheFor)
            return hit;

        var root = JsonNode.Parse(await http.GetStringAsync(
            $"{SearchUrl}?query&start={start}&count={PageSize}&category1=10{order}{filter}&cc=tr&l=turkish&infinite=1", ct));
        int total = root?["total_count"]?.GetValue<int>() ?? 0;
        var ids = AppIds(root);

        var demos = await ItemsAsync(ids, new { include_release = true, include_assets = true, include_basic_info = true, include_supported_languages = true, include_platforms = true }, ct);
        var gameIds = demos.Values.Select(d => d["related_items"]?["parent_appid"]?.GetValue<int>()).OfType<int>();
        var games = await ItemsAsync(gameIds, new { include_release = true, include_assets = true, include_basic_info = true, include_supported_languages = true, include_platforms = true, include_reviews = true, include_tag_count = 6 }, ct);
        var tags = await TagNamesAsync(ct);

        var items = ids.Where(demos.ContainsKey).Select(id =>
        {
            var d = demos[id];
            var g = d["related_items"]?["parent_appid"]?.GetValue<int>() is int gid ? games.GetValueOrDefault(gid) : null;
            return Build(id, d, g, tags);
        }).ToList();
        var page = new DemoPage(list, start, total, items, DateTime.Now);
        cache[key] = page;
        return page;
    }

    // App ids of a store search page, in the store's order.
    internal static List<int> AppIds(JsonNode? searchResult) =>
        AppIdRegex().Matches(searchResult?["results_html"]?.GetValue<string>() ?? "")
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().ToList();

    // Current release state of watched demos: out yet, or a new date (developers move them often).
    public static async Task<Dictionary<int, DemoStatus>> StatusAsync(IEnumerable<int> ids, CancellationToken ct = default)
    {
        var items = await ItemsAsync(ids, new { include_release = true }, ct);
        return items.ToDictionary(kv => kv.Key, kv =>
        {
            bool soon = IsComingSoon(kv.Value);
            var (date, text) = When(kv.Value["release"], soon);
            return new DemoStatus(soon, date, text);
        });
    }

    internal static async Task<Dictionary<int, JsonNode>> ItemsAsync(IEnumerable<int> ids, object dataRequest, CancellationToken ct)
    {
        var result = new Dictionary<int, JsonNode>();
        foreach (var chunk in ids.Distinct().Chunk(50))
        {
            string input = JsonSerializer.Serialize(new
            {
                ids = chunk.Select(id => new { appid = id }),
                context = new { language = "turkish", country_code = "TR" },
                data_request = dataRequest,
            });
            var root = JsonNode.Parse(await http.GetStringAsync(ItemsUrl + "?input_json=" + Uri.EscapeDataString(input), ct));
            foreach (var item in root?["response"]?["store_items"]?.AsArray() ?? [])
                if (item?["success"]?.GetValue<int>() == 1 && item["appid"]?.GetValue<int>() is int id)
                    result[id] = item;
        }
        return result;
    }

    static async Task<Dictionary<int, string>> TagNamesAsync(CancellationToken ct)
    {
        if (tagNames is not null)
            return tagNames;
        try
        {
            var root = JsonNode.Parse(await http.GetStringAsync(TagsUrl, ct));
            tagNames = (root?["response"]?["tags"]?.AsArray() ?? [])
                .Where(t => t?["tagid"] is not null && t["name"] is not null)
                .ToDictionary(t => t!["tagid"]!.GetValue<int>(), t => t!["name"]!.GetValue<string>());
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return [];   // tags are a nicety; try again next time
        }
        return tagNames;
    }

    static Demo Build(int id, JsonNode demo, JsonNode? game, Dictionary<int, string> tags)
    {
        var g = game ?? demo;
        bool soon = IsComingSoon(demo);
        var (date, dateText) = When(demo["release"], soon);
        string? gameDate = game is not null && IsComingSoon(game) ? WhenText(game["release"]) : null;
        var reviews = game?["reviews"]?["summary_filtered"];
        int deck = Deck(demo) is > 0 and var dd ? dd : Deck(game);
        string name = Str(g, "name") is { Length: > 0 } n ? n : Regex.Replace(Str(demo, "name") ?? $"App {id}", @"\s+Demo$", "");

        return new Demo(
            id,
            game?["appid"]?.GetValue<int>() ?? 0,
            name,
            Str(g["basic_info"], "short_description") ?? Str(demo["basic_info"], "short_description"),
            Header(game) ?? Header(demo),
            (g["tagids"]?.AsArray() ?? []).Select(t => t?.GetValue<int>() ?? 0)
                .Select(t => tags.GetValueOrDefault(t)).OfType<string>().Take(4).ToArray(),
            date,
            dateText,
            soon,
            HasTurkish(demo) || HasTurkish(game),
            deck,
            reviews?["review_count"]?.GetValue<int>() ?? 0,
            reviews?["percent_positive"]?.GetValue<int>() ?? 0,
            Str(reviews, "review_score_label"),
            gameDate,
            "https://store.steampowered.com/" + (Str(g, "store_url_path") ?? $"app/{id}"),
            Players(demo) is { Length: > 0 } p ? p : Players(game));
    }

    // Steam player categories: 1 multiplayer, 9 co-op, 38 online co-op, 39 split-screen co-op.
    // The demo's own list first: that's what you'll play (and what the store search filters on).
    internal static string[] Players(JsonNode? item)
    {
        var ids = (item?["categories"]?["supported_player_categoryids"]?.AsArray() ?? [])
            .Select(c => c?.GetValue<int>() ?? 0).ToHashSet();
        var list = new List<string>();
        if (ids.Contains(38)) list.Add("Online co-op");
        if (ids.Contains(39)) list.Add("Aynı ekran co-op");
        if (ids.Contains(9) && list.Count == 0) list.Add("Co-op");
        if (ids.Contains(1) && list.Count == 0) list.Add("Çok oyunculu");
        return [.. list];
    }

    static bool IsComingSoon(JsonNode item) =>
        item["is_coming_soon"]?.GetValue<bool>() ?? item["release"]?["is_coming_soon"]?.GetValue<bool>() ?? false;

    // An exact date when Steam has one; otherwise the store's own wording ("2027", "Pek yakında"…).
    static (DateTimeOffset? Date, string? Text) When(JsonNode? release, bool soon)
    {
        long unix = release?["steam_release_date"]?.GetValue<long>() ?? release?["original_release_date"]?.GetValue<long>() ?? 0;
        DateTimeOffset? date = unix > 0 ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;
        if (!soon || (Str(release, "coming_soon_display") == "date_full" && date is not null))
            return (date, null);
        return (null, WhenText(release));
    }

    static string WhenText(JsonNode? release)
    {
        long unix = release?["steam_release_date"]?.GetValue<long>() ?? 0;
        var d = unix > 0 ? DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime() : (DateTimeOffset?)null;
        if (Str(release, "custom_release_date_message") is { Length: > 0 } custom)
            return custom;
        return (Str(release, "coming_soon_display"), d) switch
        {
            ("date_full", { } x) => x.ToString("d MMMM yyyy", Tr),
            ("date_month", { } x) => x.ToString("MMMM yyyy", Tr),
            ("date_quarter", { } x) => $"{x.Year} {(x.Month + 2) / 3}. çeyrek",
            ("date_year", { } x) => x.Year.ToString(CultureInfo.InvariantCulture),
            ("text_tba", _) => "Duyurulacak",
            _ => "Pek yakında",
        };
    }

    internal static string? Header(JsonNode? item)
    {
        var a = item?["assets"];
        return Str(a, "asset_url_format") is { } fmt && Str(a, "header") is { } file
            ? AssetBase + fmt.Replace("${FILENAME}", file, StringComparison.Ordinal)
            : null;
    }

    static bool HasTurkish(JsonNode? item) =>
        (item?["supported_languages"]?.AsArray() ?? []).Any(l =>
            l?["elanguage"]?.GetValue<int>() == TurkishLanguage && (l["supported"]?.GetValue<bool>() ?? false));

    // 0 unknown, 1 unsupported, 2 playable, 3 verified.
    static int Deck(JsonNode? item) => item?["platforms"]?["steam_deck_compat_category"]?.GetValue<int>() ?? 0;

    internal static string? Str(JsonNode? node, string name) => node?[name] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}
