using System.Text.Json.Nodes;
using Gecit.Boot;
using Gecit.Net;

namespace Gecit;

// Güncelleme, Ağım and Açılış.
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
        };
        slow.Start();
        // Not right at logon: the PC is busy and the network may not be up yet.
        _ = Task.Delay(TimeSpan.FromSeconds(BootMeter.JustLoggedOn ? 90 : 10)).ContinueWith(
            _ => BeginInvoke(async () => { if (settings.UpdateCheck) await CheckUpdateAsync(manual: false); }));
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
