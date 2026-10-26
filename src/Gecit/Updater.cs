using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace Gecit;

sealed record UpdateInfo(string Version, string PageUrl, string DownloadUrl, long Size, string Sha256, DateTimeOffset Published);

// New versions are the GitHub releases of the public repo. The app only downloads and verifies the
// setup; the setup does the update (stops services, replaces files, restarts them, relaunches Geçit).
static class Updater
{
    const string Latest = "https://api.github.com/repos/Furkan-Erg/Gecit/releases/latest";
#if DEBUG
    // Testing against a local fake release.
    static string LatestUrl => Environment.GetEnvironmentVariable("GECIT_UPDATE_URL") ?? Latest;
#else
    const string LatestUrl = Latest;
#endif

    public static Version Current { get; } = Version.Parse(Application.ProductVersion.Split('+')[0]);

    static readonly HttpClient http = CreateClient();
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Gecit/{Current}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    static string Dir => Path.Combine(AppPaths.LocalDir, "update");

    // Null when this is the newest version (or the release has no setup we can verify).
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        var rel = await http.GetFromJsonAsync<Release>(LatestUrl, Json, ct)
                  ?? throw new InvalidOperationException("GitHub'dan boş yanıt geldi.");
        if (rel.Draft || rel.Prerelease || !Version.TryParse(rel.TagName.TrimStart('v', 'V'), out var v) || v <= Current)
            return null;
        var asset = rel.Assets.FirstOrDefault(a => a.Name.StartsWith("Gecit-Setup-", StringComparison.OrdinalIgnoreCase)
                                                   && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        // GitHub computes the digest itself when the file is uploaded; without it there's nothing to check against.
        if (asset?.Digest is not { } digest || !digest.StartsWith("sha256:", StringComparison.Ordinal))
            return null;
        return new UpdateInfo(v.ToString(), rel.HtmlUrl, asset.BrowserDownloadUrl, asset.Size, digest[7..].ToLowerInvariant(), rel.PublishedAt);
    }

    public static async Task<string> DownloadAsync(UpdateInfo u, IProgress<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        string path = Path.Combine(Dir, $"Gecit-Setup-{u.Version}.exe");
        using (var resp = await http.GetAsync(u.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(path);
            byte[] buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                progress.Report(u.Size > 0 ? Math.Min(1, (double)done / u.Size) : 0);
            }
        }
        return path;
    }

    // Runs the setup (UAC) silently; it closes this app through "Gecit.exe --exit" and starts the new one.
    // The file stays open read-only from the hash check until the setup has started, so it can't be
    // swapped in between.
    public static void Install(UpdateInfo u, string path, bool showWindowAfter)
    {
        using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        string hash = Convert.ToHexStringLower(SHA256.HashData(locked));
        if (hash != u.Sha256)
        {
            locked.Dispose();
            File.Delete(path);
            throw new InvalidOperationException("İndirilen dosya doğrulanamadı (SHA-256 tutmuyor). Tekrar dene.");
        }
        // Not "runas": the setup elevates itself and remembers this unelevated user, so the relaunched
        // Geçit ("runasoriginaluser" in gecit.iss) isn't an admin process. Started elevated, it would be.
        var psi = new ProcessStartInfo(path, $"/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH={(showWindowAfter ? "show" : "tray")}")
        {
            UseShellExecute = true,
        };
        try { Process.Start(psi)?.Dispose(); }
        catch (Win32Exception e)
        {
            throw new InvalidOperationException("Kurulum başlatılamadı: " + e.Message);
        }
    }

    // Leftovers of an earlier update (the new version is running by now).
    public static void CleanUp()
    {
        try
        {
            if (Directory.Exists(Dir))
                Directory.Delete(Dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    sealed record Release(string TagName, string HtmlUrl, bool Draft, bool Prerelease, DateTimeOffset PublishedAt, List<Asset> Assets);
    sealed record Asset(string Name, string BrowserDownloadUrl, long Size, string? Digest);
}
