using System.Net.Http;

namespace Gecit.Browser;

// Domain blocklist for the private browser: StevenBlack's unified hosts (ads + malware),
// cached locally and refreshed weekly, plus a few built-ins so it works before the first download.
sealed class AdBlocker
{
    const string ListUrl = "https://raw.githubusercontent.com/StevenBlack/hosts/master/hosts";
    static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    static string CacheFile => Path.Combine(AppPaths.LocalDir, "blocklist-hosts.txt");

    static readonly string[] BuiltIn =
    [
        "doubleclick.net", "googlesyndication.com", "googleadservices.com", "adservice.google.com",
        "google-analytics.com", "googletagservices.com", "adnxs.com", "criteo.com", "criteo.net",
        "taboola.com", "outbrain.com", "scorecardresearch.com", "moatads.com", "amazon-adsystem.com",
        "pubmatic.com", "rubiconproject.com", "openx.net", "adform.net", "smartadserver.com",
    ];

    volatile HashSet<string> domains = new(BuiltIn, StringComparer.OrdinalIgnoreCase);

    public int Count => domains.Count;
    public DateTime? ListDate { get; private set; }

    public async Task LoadAsync()
    {
        var info = new FileInfo(CacheFile);
        if (!info.Exists || DateTime.UtcNow - info.LastWriteTimeUtc > MaxAge)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                string text = await http.GetStringAsync(ListUrl);
                if (text.Contains("0.0.0.0 "))
                    await File.WriteAllTextAsync(CacheFile, text);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
                // Offline: keep using the cached copy (or the built-ins).
            }
            info.Refresh();
        }
        if (!info.Exists)
            return;

        var set = await Task.Run(() => Parse(CacheFile));
        domains = set;
        ListDate = info.LastWriteTime;
    }

    static HashSet<string> Parse(string path)
    {
        var set = new HashSet<string>(BuiltIn, StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadLines(path))
        {
            // "0.0.0.0 ads.example.com  # comment"
            if (!line.StartsWith("0.0.0.0 ", StringComparison.Ordinal))
                continue;
            var span = line.AsSpan(8).Trim();
            int end = span.IndexOfAny(' ', '\t', '#');
            if (end >= 0)
                span = span[..end];
            if (span.Length > 0 && span is not "0.0.0.0")
                set.Add(span.ToString());
        }
        return set;
    }

    // Matches the host and every parent domain (ads.tracker.com -> tracker.com).
    public bool IsBlocked(string host)
    {
        var set = domains;
        while (true)
        {
            if (set.Contains(host))
                return true;
            int dot = host.IndexOf('.');
            if (dot < 0 || host.IndexOf('.', dot + 1) < 0)
                return false;
            host = host[(dot + 1)..];
        }
    }
}
