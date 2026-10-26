using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gecit.Boot;
using Gecit.Net;
using Gecit.Store;
using Microsoft.Win32;

namespace Gecit;

// Güncelleme, Ağım, Açılış, Demolar and Bedava.
sealed partial class MainForm
{
    readonly LanService lan = new();
    readonly System.Windows.Forms.Timer slow = new() { Interval = 60_000 };
    int slowTicks;

    UpdateInfo? update;
    bool updateChecking, updateInstalling, updateDismissed;
    DateTime? updateLastCheck;
    string? updateError;
    bool bootMeasuring;

    void StartTools()
    {
        Updater.CleanUp();
        string current = Updater.Current.ToString();
        if (settings.LastVersion is { } last && last != current)
            pendingToast = $"Geçit {current} sürümüne güncellendi.";
        if (settings.LastVersion != current)
        {
            settings.LastVersion = current;
            settings.Save();
        }

        if (BootMeter.JustLoggedOn)
            _ = MeasureBootAsync();

        slow.Tick += async (_, _) =>
        {
            slowTicks++;
            if (settings.UpdateCheck && slowTicks % (12 * 60) == 0)
                await CheckUpdateAsync(manual: false);
            if (settings.LanWatch && slowTicks % 10 == 0)
                await WatchLanAsync();
            // Every 2 hours; every 10 minutes once a watched demo's announced time has come.
            bool due = settings.DemoWatch.Any(w => !w.Released && w.Demo.Date <= DateTimeOffset.Now);
            if (slowTicks % (due ? 10 : 120) == 0)
                await CheckDemoWatchAsync();
            // Every 3 hours, and soon after a giveaway is due to start or end (Epic's weekly switch).
            if (slowTicks % 180 == 0 || (slowTicks % 10 == 0 && FreeGames.NextChange <= DateTimeOffset.Now))
                await CheckFreeGamesAsync();
        };
        slow.Start();
        // Not right at logon: the PC is busy and the network may not be up yet.
        _ = Task.Delay(TimeSpan.FromSeconds(BootMeter.JustLoggedOn ? 90 : 10)).ContinueWith(
            _ => BeginInvoke(async () =>
            {
                if (settings.UpdateCheck) await CheckUpdateAsync(manual: false);
                await CheckDemoWatchAsync();
                await CheckFreeGamesAsync();
            }));
    }

    string? pendingToast;

    // Called when the page has loaded.
    void ShowPendingToast()
    {
        if (pendingToast is null)
            return;
        Notify("ok", pendingToast);
        pendingToast = null;
    }

    // ---- Güncelleme ----

    async Task CheckUpdateAsync(bool manual)
    {
        if (updateChecking || updateInstalling)
            return;
        updateChecking = true;
        PushState();
        try
        {
            update = await Updater.CheckAsync();
            updateError = null;
            updateLastCheck = DateTime.Now;
            if (update is not null && !Visible && settings.UpdateNotified != update.Version)
            {
                settings.UpdateNotified = update.Version;
                settings.Save();
                tray.ShowBalloonTip(6000, $"Geçit {update.Version} çıktı", "Güncellemek için Geçit'i aç.", ToolTipIcon.Info);
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            // Offline or GitHub hiccup: only worth telling when the user pressed the button.
            updateError = manual ? "Denetlenemedi: " + (e is TaskCanceledException ? "zaman aşımı" : e.Message) : updateError;
        }
        finally
        {
            updateChecking = false;
            PushState();
        }
    }

    async Task InstallUpdateAsync()
    {
        if (update is not { } u || updateInstalling)
            return;
        updateInstalling = true;
        PushState();
        try
        {
            var progress = new Progress<double>(f => Post(new { @event = "update", data = new { fraction = f } }));
            string path = await Updater.DownloadAsync(u, progress, CancellationToken.None);
            Updater.Install(u, path, showWindowAfter: Visible);
            // The setup now closes this instance with "--exit" and starts the new version.
        }
        catch (Exception e)
        {
            updateInstalling = false;
            Notify("error", e is HttpRequestException ? "İndirilemedi: " + e.Message : e.Message);
            PushState();
        }
    }

    object UpdateState() => new
    {
        current = Updater.Current.ToString(),
        available = update is null ? null : new { update.Version, url = update.PageUrl, update.Size, update.Published },
        dismissed = updateDismissed,
        auto = settings.UpdateCheck,
        checking = updateChecking,
        installing = updateInstalling,
        lastCheck = updateLastCheck,
        error = updateError,
    };

    // ---- Ağım ----

    async Task WatchLanAsync()
    {
        if (lan.Scanning || !lan.HasBaseline || game.Active is not null)
            return;   // no pinging the whole network in the middle of a game
        try
        {
            var r = await lan.ScanAsync();
            Post(new { @event = "lan", data = r });
            foreach (var d in r.NewDevices)
            {
                string what = d.Vendor is not null ? $"{d.Vendor}{(d.Model is not null ? " · " + d.Model : "")}" : d.RandomMac ? "gizli MAC adresli (büyük ihtimalle telefon)" : "üreticisi bilinmiyor";
                tray.ShowBalloonTip(8000, "Ağına yeni bir cihaz bağlandı", $"{d.Name} ({d.Ip}), {what}. Tanıyorsan Ağım'dan ad verebilirsin.", ToolTipIcon.Warning);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.Net.Sockets.SocketException or IOException) { }
    }

    // ---- Açılış ----

    async Task MeasureBootAsync()
    {
        bootMeasuring = true;
        try
        {
            if (await BootMeter.MeasureAsync() is not null)
                Post(new { @event = "boot" });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        finally { bootMeasuring = false; }
    }

    object BootState()
    {
        var history = BootMeter.History();
        var last = history.LastOrDefault();
        var impacts = last?.Items.ToDictionary(i => i.Id) ?? [];
        var items = StartupItems.List().Select(i =>
        {
            var imp = impacts.GetValueOrDefault(i.Id);
            return new
            {
                i.Id, i.Name, i.Publisher, i.Command, i.Enabled, i.NeedsAdmin, i.Missing, i.IsWindows, i.Hint, i.Suggested,
                source = i.Source.ToString(),
                icon = StartupItems.IconDataUrl(i.ExePath),
                impact = imp is null ? null : imp.Running ? BootMeter.Impact(imp.CpuMs, imp.IoBytes) : "none",
                cpuMs = imp?.CpuMs,
                ioBytes = imp?.IoBytes,
            };
        }).ToList();
        return new
        {
            items,
            history = history.Select(r => new { r.DesktopAt, r.ReadySeconds, r.BiosSeconds }),
            bios = BootMeter.BiosSeconds(),
            measuring = bootMeasuring,
            scheduled = BootMeter.MeasureScheduled,
            autostart = Autostart.IsEnabled,
        };
    }

    // ---- Demolar ----

    static bool SteamInstalled => Registry.ClassesRoot.OpenSubKey(@"steam\shell\open\command") is not null;

    async Task CheckDemoWatchAsync()
    {
        var waiting = settings.DemoWatch.Where(w => !w.Released).ToList();
        if (waiting.Count == 0)
            return;
        Dictionary<int, DemoStatus> fresh;
        try
        {
            fresh = await DemoFeed.StatusAsync(waiting.Select(w => w.Demo.Id));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { return; }
        var now = new List<WatchedDemo>();
        foreach (var w in waiting)
        {
            if (!fresh.TryGetValue(w.Demo.Id, out var s))
                continue;
            // Keep the shown date current, and stop the 10-minute checks for a date that slipped.
            w.Demo = w.Demo with { ComingSoon = s.ComingSoon, Date = s.Date, DateText = s.DateText };
            if (!s.ComingSoon)
            {
                w.Released = true;
                now.Add(w);
            }
        }
        settings.Save();
        Post(new { @event = "demos" });
        if (now.Count == 0)
            return;
        tray.ShowBalloonTip(8000,
            now.Count == 1 ? $"{now[0].Demo.Name} demosu çıktı" : $"Takip ettiğin {now.Count} demo çıktı",
            now.Count == 1 ? "Steam'de ücretsiz oynayabilirsin. Geçit → Demolar'dan açabilirsin." : string.Join(", ", now.Select(w => w.Demo.Name)),
            ToolTipIcon.Info);
    }

    object DemoWatchState() => new
    {
        steam = SteamInstalled,
        players = settings.DemoPlayers,
        watch =settings.DemoWatch.Select(w => new { w.Demo, w.Released }),
    };

    // ---- Bedava ----

    async Task CheckFreeGamesAsync()
    {
        var (games, _, _) = await FreeGames.GetAsync(refresh: true);
        Post(new { @event = "free" });
        if (!settings.FreeNotify)
            return;
        // Games only: a free DLC for a game you don't own is noise.
        var fresh = games.Where(g => !g.Upcoming && g.DlcFor is null && !settings.FreeSeen.Contains(g.Id)).ToList();
        if (fresh.Count == 0)
            return;
        settings.FreeSeen.AddRange(fresh.Select(g => g.Id));
        if (settings.FreeSeen.Count > 300)
            settings.FreeSeen.RemoveRange(0, settings.FreeSeen.Count - 300);
        settings.Save();
        var g0 = fresh[0];
        tray.ShowBalloonTip(8000,
            fresh.Count == 1 ? $"Bedava oyun: {g0.Title}" : $"{fresh.Count} oyun bedava",
            fresh.Count == 1
                ? $"{(g0.Store == "Epic" ? "Epic'te" : "Steam'de")}{(g0.End is { } end ? " " + end.ToLocalTime().ToString("d MMMM HH:mm", CultureInfo.GetCultureInfo("tr-TR")) + " saatine kadar" : "")} ücretsiz. Alırsan hesabında kalır."
                : string.Join(", ", fresh.Select(g => $"{g.Title} ({g.Store})")),
            ToolTipIcon.Info);
    }

    async Task<object?> HandleFreeAsync(string cmd, JsonNode? args)
    {
        switch (cmd)
        {
            case "free.list":
                var (games, errors, at) = await FreeGames.GetAsync(args?["refresh"]?.GetValue<bool>() ?? false);
                return new { games, errors, at, notify = settings.FreeNotify };
            case "free.notify":
                settings.FreeNotify = args!["on"]!.GetValue<bool>();
                settings.Save();
                return null;
            default:
                throw new InvalidOperationException($"Bilinmeyen komut: {cmd}");
        }
    }

    async Task<object?> HandleDemosAsync(string cmd, JsonNode? args)
    {
        switch (cmd)
        {
            case "demos.list":
                var page = await DemoFeed.GetAsync(args!["list"]!.GetValue<string>(), args["players"]?.GetValue<string>() ?? "all",
                    args["start"]?.GetValue<int>() ?? 0, args["refresh"]?.GetValue<bool>() ?? false);
                return new { page, watched = DemoWatchState() };
            case "demos.players":
                // Remembered: people tend to always look for the same kind (e.g. co-op with friends).
                string players = args!["players"]!.GetValue<string>();
                if (DemoFeed.PlayerFilters.ContainsKey(players))
                {
                    settings.DemoPlayers = players;
                    settings.Save();
                }
                return null;
            case "demos.watched":
                return DemoWatchState();
            case "demos.watch":
                var demo = args!["demo"].Deserialize<Demo>(Json) ?? throw new InvalidOperationException("Demo yok.");
                settings.DemoWatch.RemoveAll(w => w.Demo.Id == demo.Id);
                if (args["on"]!.GetValue<bool>())
                    settings.DemoWatch.Add(new WatchedDemo { Demo = demo, Released = !demo.ComingSoon });
                settings.Save();
                return DemoWatchState();
            case "demos.install":
                // Only a number goes into the URL; Steam asks before installing anything.
                int id = args!["id"]!.GetValue<int>();
                if (!SteamInstalled)
                    throw new InvalidOperationException("Steam bu bilgisayarda kurulu değil.");
                Process.Start(new ProcessStartInfo($"steam://install/{id}") { UseShellExecute = true })?.Dispose();
                return null;
            default:
                throw new InvalidOperationException($"Bilinmeyen komut: {cmd}");
        }
    }

    async Task<object?> HandleToolsAsync(string cmd, JsonNode? args)
    {
        switch (cmd)
        {
            case "update.check":
                updateDismissed = false;
                await CheckUpdateAsync(manual: true);
                return BuildState();
            case "update.install":
                await InstallUpdateAsync();
                return BuildState();
            case "update.dismiss":
                updateDismissed = true;
                return BuildState();
            case "update.auto":
                settings.UpdateCheck = args!["on"]!.GetValue<bool>();
                settings.Save();
                return BuildState();

            case "lan.scan":
                return await lan.ScanAsync();
            case "lan.last":
                return lan.Last;
            case "lan.rename":
                lan.Rename(args!["mac"]!.GetValue<string>(), args["name"]?.GetValue<string>());
                return lan.Last;
            case "lan.forget":
                lan.Forget(args!["mac"]!.GetValue<string>());
                return lan.Last;
            case "lan.watch":
                settings.LanWatch = args!["on"]!.GetValue<bool>();
                settings.Save();
                return BuildState();

            case "boot.list":
                return await Task.Run(BootState);
            case "boot.set":
                string id = args!["id"]!.GetValue<string>();
                bool on = args["on"]!.GetValue<bool>();
                var item = StartupItems.List().FirstOrDefault(i => i.Id == id) ?? throw new InvalidOperationException("Öğe artık yok.");
                if (item.NeedsAdmin)
                    await Elevated.RunAsync("startup", item.Source.ToString(), item.Key, on ? "on" : "off");
                else
                    StartupItems.SetEnabled(item.Source, item.Key, on);
                return await Task.Run(BootState);
            case "boot.schedule":
                BootMeter.ScheduleOnce(args!["on"]!.GetValue<bool>());
                return await Task.Run(BootState);
            default:
                throw new InvalidOperationException($"Bilinmeyen komut: {cmd}");
        }
    }
}
