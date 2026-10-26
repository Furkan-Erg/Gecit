namespace Gecit.Browser;

sealed record SearchEngine(string Id, string Name, string UrlTemplate)
{
    public string SearchUrl(string query) => UrlTemplate.Replace("{q}", Uri.EscapeDataString(query));
}

static class SearchEngines
{
    public static readonly IReadOnlyList<SearchEngine> All =
    [
        new("duckduckgo", "DuckDuckGo", "https://duckduckgo.com/?q={q}"),
        new("brave", "Brave Search", "https://search.brave.com/search?q={q}"),
        new("startpage", "Startpage", "https://www.startpage.com/do/search?q={q}"),
        new("google", "Google", "https://www.google.com/search?q={q}"),
    ];

    public static SearchEngine Get(string? id) => All.FirstOrDefault(e => e.Id == id) ?? All[0];

    // Address bar text -> URL: explicit URLs and things that look like domains are opened, the rest is searched.
    public static string Resolve(string text, SearchEngine engine)
    {
        text = text.Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var abs) && abs.Scheme is "http" or "https")
            return abs.AbsoluteUri;
        bool looksLikeHost = !text.Contains(' ') && (text.Contains('.') || text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase));
        if (looksLikeHost && Uri.TryCreate("https://" + text, UriKind.Absolute, out var guessed)
            && (guessed.Host.Contains('.') || guessed.IsLoopback))
            return guessed.AbsoluteUri;
        return engine.SearchUrl(text);
    }
}
