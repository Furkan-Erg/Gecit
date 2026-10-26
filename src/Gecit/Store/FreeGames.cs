using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Gecit.Store.DemoFeed;

namespace Gecit.Store;

sealed record FreeGame(
    string Id, string Store, string Title, string? Description, string? Image, string Url,
    DateTimeOffset? Start, DateTimeOffset? End, bool Upcoming, string? OriginalPrice, string? DlcFor, string[] Players);

// Games given away to keep: Epic's weekly free games (plus next week's, which Epic announces) and
// Steam's "free to keep" promotions. Free-to-play games and Steam free weekends aren't here: they
// never become yours.
static class FreeGames
{
    const string EpicUrl = "https://store-site-backend-static-ipv4.ak.epicgames.com/freeGamesPromotions?locale=tr&country=TR&allowCountries=TR";
    // Free-to-keep games are specials at $0.00, so they come first when sorted by price.
    const string SteamSearchUrl = "https://store.steampowered.com/search/results/?query&count=25&specials=1&sort_by=Price_ASC&cc=tr&l=turkish&infinite=1";
    static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);

    static (DateTime At, List<FreeGame> Games, List<string> Errors)? cache;

    // When the list will change on its own: the next announced giveaway starts or a current one ends.
    public static DateTimeOffset? NextChange => cache?.Games
        .Select(g => g.Upcoming ? g.Start : g.End).Where(d => d is not null).Min();

    public static async Task<(List<FreeGame> Games, List<string> Errors, DateTime At)> GetAsync(bool refresh, CancellationToken ct = default)
    {
        if (!refresh && cache is { } c && DateTime.Now - c.At < CacheFor)
            return (c.Games, c.Errors, c.At);
        var games = new List<FreeGame>();
        var errors = new List<string>();
        var epic = EpicAsync(ct);
        var steam = SteamAsync(ct);
        foreach (var (store, task) in new[] { ("Epic", epic), ("Steam", steam) })
        {
            try
            {
                games.AddRange(await task);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                errors.Add($"{store}: {(e is TaskCanceledException ? "zaman aşımı" : e.Message)}");
            }
        }
        games = [.. games.OrderBy(g => g.Upcoming).ThenBy(g => g.Upcoming ? g.Start : g.End)];
        cache = (DateTime.Now, games, errors);
        return (games, errors, DateTime.Now);
    }

    static async Task<List<FreeGame>> EpicAsync(CancellationToken ct)
    {
        var root = JsonNode.Parse(await http.GetStringAsync(EpicUrl, ct));
        var list = new List<FreeGame>();
        foreach (var e in root?["data"]?["Catalog"]?["searchStore"]?["elements"]?.AsArray() ?? [])
        {
            if (e is null)
                continue;
            var price = e["price"]?["totalPrice"];
            // A discount to 0% of the price is a giveaway; other offers in this feed are ordinary sales.
            var now = FreeOffer(e["promotions"]?["promotionalOffers"]);
            bool freeNow = now is not null && price?["discountPrice"]?.GetValue<int>() == 0;
            var offer = freeNow ? now : FreeOffer(e["promotions"]?["upcomingPromotionalOffers"]);
            if (offer is not { } o)
                continue;
            string? original = Str(price?["fmtPrice"], "originalPrice");
            list.Add(new FreeGame(
                "epic:" + Str(e, "id"),
                "Epic",
                Str(e, "title") ?? "?",
                Str(e, "description"),
                EpicImage(e),
                EpicSlug(e) is { } slug ? $"https://store.epicgames.com/tr/p/{slug}" : "https://store.epicgames.com/tr/free-games",
                o.Start, o.End, !freeNow,
                original is null or "0" ? null : original,
                Str(e, "offerType") == "ADD_ON" ? "" : null,
                []));
        }
        return list;
    }

    static (DateTimeOffset Start, DateTimeOffset End)? FreeOffer(JsonNode? groups)
    {
        foreach (var g in groups?.AsArray() ?? [])
            foreach (var o in g?["promotionalOffers"]?.AsArray() ?? [])
                if (o?["discountSetting"]?["discountPercentage"]?.GetValue<int>() == 0
                    && DateTimeOffset.TryParse(Str(o, "startDate"), CultureInfo.InvariantCulture, out var start)
                    && DateTimeOffset.TryParse(Str(o, "endDate"), CultureInfo.InvariantCulture, out var end)
                    && end > DateTimeOffset.Now)
                    return (start, end);
        return null;
    }

    static string? EpicSlug(JsonNode e)
    {
        var mappings = (e["catalogNs"]?["mappings"]?.AsArray() ?? []).Concat(e["offerMappings"]?.AsArray() ?? []);
        string? slug = mappings.Where(m => Str(m, "pageType") is null or "productHome").Select(m => Str(m, "pageSlug")).FirstOrDefault(s => !string.IsNullOrEmpty(s))
                       ?? Str(e, "productSlug");
        return string.IsNullOrEmpty(slug) ? null : slug.Replace("/home", "", StringComparison.Ordinal);
    }

    static string? EpicImage(JsonNode e)
    {
        var images = (e["keyImages"]?.AsArray() ?? []).Where(k => k is not null).ToList();
        foreach (string type in new[] { "OfferImageWide", "DieselStoreFrontWide", "Thumbnail" })
            if (images.FirstOrDefault(k => Str(k, "type") == type) is { } k)
                return Str(k, "url");
        return images.Select(k => Str(k, "url")).FirstOrDefault();
    }

    static async Task<List<FreeGame>> SteamAsync(CancellationToken ct)
    {
        var ids = AppIds(JsonNode.Parse(await http.GetStringAsync(SteamSearchUrl, ct)));
        var items = await ItemsAsync(ids, new { include_basic_info = true, include_assets = true, include_all_purchase_options = true }, ct);
        var free = ids.Where(items.ContainsKey).Select(id => items[id])
            .Where(i => i["best_purchase_option"]?["is_free_to_keep"]?.GetValue<bool>() == true).ToList();
        // DLC: say which game it needs.
        var parentIds = free.Select(i => i["related_items"]?["parent_appid"]?.GetValue<int>()).OfType<int>();
        var parents = await ItemsAsync(parentIds, new { }, ct);

        return free.Select(i =>
        {
            int id = i["appid"]!.GetValue<int>();
            var best = i["best_purchase_option"];
            long ends = best?["free_to_keep_ends"]?.GetValue<long>() ?? 0;
            int? parent = i["related_items"]?["parent_appid"]?.GetValue<int>();
            bool dlc = i["type"]?.GetValue<int>() == 4;
            return new FreeGame(
                "steam:" + id,
                "Steam",
                Str(i, "name") ?? $"App {id}",
                Str(i["basic_info"], "short_description"),
                Header(i),
                $"https://store.steampowered.com/app/{id}",
                null,
                ends > 0 ? DateTimeOffset.FromUnixTimeSeconds(ends) : null,
                false,
                Str(best, "formatted_original_price"),
                dlc ? (parent is int p && parents.TryGetValue(p, out var pi) ? Str(pi, "name") ?? "" : "") : null,
                Players(i));
        }).ToList();
    }
}
