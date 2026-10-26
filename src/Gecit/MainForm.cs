using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gecit.Browser;
using Gecit.Dns;
using Gecit.Dpi;
using Gecit.Game;
using Gecit.Net;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Gecit;

sealed partial class MainForm : Form
{
    const string AppOrigin = "https://gecit.example/";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(10, 8, 20) };
    readonly NotifyIcon tray = new();
    readonly ToolStripMenuItem trayToggle = new();
    readonly System.Windows.Forms.Timer poll = new() { Interval = 2000 };
    readonly Icon iconOn = LoadIcon("gecit.ico");
    readonly Icon iconOff = LoadIcon("gecit-off.ico");
    readonly DpiService dpi = new();
    readonly Settings settings = Settings.Load();
    readonly EventWaitHandle showEvent = new(false, EventResetMode.AutoReset, Program.ShowEventName);
    readonly EventWaitHandle browserEvent = new(false, EventResetMode.AutoReset, Program.BrowserEventName);
    readonly EventWaitHandle exitEvent = new(false, EventResetMode.AutoReset, Program.ExitEventName);
    readonly AdBlocker adBlocker = new();
    readonly GameModeService game;
    readonly PingMonitor ping = new();
    readonly TrafficMonitor traffic = new();
    CancellationTokenSource? speedCts;
    BrowserForm? browser;
    int gpuSampleCountdown;

    bool allowShow;
    bool exiting;
    bool busy;
    bool trayHintShown;
    DpiStatus status;
    DnsStatus dns;
    string dnsKey = "";

    public MainForm(bool startHidden, bool openBrowser)
    {
        allowShow = !startHidden;
        Text = "Geçit";
        Icon = iconOn;
        ClientSize = new Size(1120, 720);
        MinimumSize = new Size(940, 620);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(10, 8, 20);
        Controls.Add(web);

        status = dpi.GetStatus();
        dns = DnsService.GetStatus();
        dnsKey = DnsKey(dns);
        game = new GameModeService(settings.Game);
        game.Changed += OnGameModeChanged;
        BuildTray();
        poll.Tick += (_, _) =>
        {
            RefreshStatus();
            game.Tick();
            SampleGpuDuringGame();
        };
        poll.Start();

        ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => BeginInvoke(ShowFromTray), null, -1, false);
        ThreadPool.RegisterWaitForSingleObject(browserEvent, (_, _) => BeginInvoke(() => OpenBrowser()), null, -1, false);
        ThreadPool.RegisterWaitForSingleObject(exitEvent, (_, _) => BeginInvoke(ExitApp), null, -1, true);
        _ = RestoreDpiAsync();
        LoadBlocklist();
        StartTools();
        if (openBrowser)
            OpenBrowser();
    }

    async void LoadBlocklist()
    {
        await adBlocker.LoadAsync();
        PushState();
    }

    void OpenBrowser(string? url = null)
    {
        if (browser is null || browser.IsDisposed)
        {
            browser = new BrowserForm(settings, adBlocker, iconOn, url);
            browser.FormClosed += (_, _) => { browser = null; PushState(); };
            browser.Show();
            browser.Activate();
            PushState();
            return;
        }
        if (url is not null)
            browser.OpenUrl(url);
        if (browser.WindowState == FormWindowState.Minimized)
            browser.WindowState = FormWindowState.Normal;
        browser.Activate();
    }

    static void CreateBrowserShortcut()
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell yok.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Gizli Tarayıcı.lnk");
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = Environment.ProcessPath;
        link.Arguments = "--browser";
        link.IconLocation = Environment.ProcessPath + ",0";
        link.Description = "Geçit · Gizli Tarayıcı";
        link.Save();
    }

    // Keeps the window hidden when started with --tray, without breaking the message loop.
    protected override void SetVisibleCore(bool value)
    {
        if (!allowShow && !IsHandleCreated)
        {
            CreateHandle();
            value = false;
        }
        base.SetVisibleCore(allowShow && value);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        NativeTheme.UseDarkTitleBar(Handle);
        await InitWebViewAsync();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            if (!trayHintShown)
            {
                trayHintShown = true;
                tray.ShowBalloonTip(3000, "Geçit arka planda", "Engel aşma çalışmaya devam ediyor. Simgeye çift tıklayarak açabilirsin.", ToolTipIcon.Info);
            }
            return;
        }
        tray.Visible = false;
        base.OnFormClosing(e);
    }

    void ShowFromTray()
    {
        allowShow = true;
        Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Activate();
    }

    void ExitApp()
    {
        game.Stop();   // never leave apps frozen behind us
        traffic.Stop();
        slow.Stop();
        speedCts?.Cancel();
        exiting = true;
        Close();
    }

    // ---- Oyun Modu ----

    GameSession? lastAnnounced;

    void OnGameModeChanged()
    {
        PushState();
        var s = game.Active;
        if (s is not null && s != lastAnnounced)
        {
            lastAnnounced = s;
            string what = s.Frozen.Count + s.Closed.Count > 0
                ? $"{s.Frozen.Select(f => f.Name).Distinct().Count() + s.Closed.Count} uygulama durduruldu."
                : "Güç planı ve öncelik ayarlandı.";
            tray.ShowBalloonTip(4000, $"Oyun modu açık · {s.GameName}", what, ToolTipIcon.Info);
        }
        else if (s is null && lastAnnounced is not null)
        {
            lastAnnounced = null;
            if (game.LastReport is { } r)
                tray.ShowBalloonTip(4000, "Oyun modu kapandı", $"{r.GameName}: {FormatDuration(r.Duration)}. Her şey eski haline döndü.", ToolTipIcon.Info);
        }
    }

    static string FormatDuration(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} sa {t.Minutes} dk" : $"{Math.Max(1, t.Minutes)} dk";

    // Peak GPU temperature for the session report (every ~10 s while a game runs).
    async void SampleGpuDuringGame()
    {
        if (game.Active is null || --gpuSampleCountdown > 0)
            return;
        gpuSampleCountdown = 5;
        if (await GpuStats.SampleAsync() is { } g)
            game.NoteGpuTemp(g.TempC);
    }

    async Task<object> GameStatsAsync()
    {
        var (total, avail) = NativeProcess.Memory();
        var gpu = await GpuStats.SampleAsync();
        if (gpu is not null)
            game.NoteGpuTemp(gpu.TempC);
        return new { cpu = NativeProcess.CpuPercent(), ramTotal = total, ramAvailable = avail, gpu };
    }

    object GameState()
    {
        var gs = settings.Game;
        var s = game.Active;
        return new
        {
            active = s is null ? null : new
            {
                name = s.GameName,
                s.Manual,
                started = s.Started,
                frozen = s.Frozen.Select(f => f.Name).Distinct().Count(),
                closed = s.Closed.Count,
                freedBytes = s.FreedBytes,
                notes = s.Notes,
            },
            last = game.LastReport is { } r ? new
            {
                name = r.GameName,
                minutes = (int)Math.Ceiling(r.Duration.TotalMinutes),
                freedBytes = r.FreedBytes,
                peakGpuTemp = r.PeakGpuTemp,
                r.Frozen,
                r.Closed,
                r.Notes,
                ended = r.Ended,
            } : null,
            settings = new { gs.AutoDetect, gs.DetectFullscreen, gs.PowerPlan, gs.HighPriority, gs.RelaunchClosed },
            targets = gs.Targets,
            knownCount = game.Games.Count(g => g.Source == "known"),
            games = game.Games.Where(g => g.Source != "known")
                .GroupBy(g => g.Name).Select(g => new { name = g.Key, exes = g.Select(x => x.Exe), source = g.First().Source }),
        };
    }

    async Task<object?> HandleGameAsync(string cmd, JsonNode? args)
    {
        var gs = settings.Game;
        switch (cmd)
        {
            case "game.toggle":
                if (args!["on"]!.GetValue<bool>()) { if (game.Active is null) game.StartManual(); }
                else game.Stop();
                return BuildState();
            case "game.settings":
                foreach (var (key, value) in args!.AsObject())
                {
                    bool on = value!.GetValue<bool>();
                    switch (key)
                    {
                        case "autoDetect": gs.AutoDetect = on; break;
                        case "detectFullscreen": gs.DetectFullscreen = on; break;
                        case "powerPlan": gs.PowerPlan = on; break;
                        case "highPriority": gs.HighPriority = on; break;
                        case "relaunchClosed": gs.RelaunchClosed = on; break;
                    }
                }
                settings.Save();
                return BuildState();
            case "game.target":
                string process = args!["process"]!.GetValue<string>();
                string action = args["action"]!.GetValue<string>();
                if (action is "suspend" or "close" && !GameCatalog.IsProtected(process))
                    gs.Targets[process] = action;
                else
                    gs.Targets.Remove(process);
                settings.Save();
                return BuildState();
            case "game.addGame":
                string exe = Path.GetFileNameWithoutExtension(args!["exe"]!.GetValue<string>().Trim()).ToLowerInvariant();
                if (exe.Length > 0 && !GameCatalog.IsProtected(exe) && !gs.CustomGames.Contains(exe))
                    gs.CustomGames.Add(exe);
                gs.Targets.Remove(exe);
                settings.Save();
                game.RefreshGames();
                return BuildState();
            case "game.removeGame":
                gs.CustomGames.Remove(args!["exe"]!.GetValue<string>());
                settings.Save();
                game.RefreshGames();
                return BuildState();
            case "game.apps":
                return await Task.Run(() => GameCatalog.RunningApps(game.GameExes));
            case "game.stats":
                return await GameStatsAsync();
            default:
                throw new InvalidOperationException($"Bilinmeyen komut: {cmd}");
        }
    }

    // ---- DPI ----

    async Task RestoreDpiAsync()
    {
        if (!settings.DpiWanted || status.State is DpiState.Running or DpiState.NotInstalled)
            return;
        await RunBusy(dpi.StartAsync);
    }

    async Task SetDpiAsync(bool on)
    {
        settings.DpiWanted = on;
        settings.Save();
        await RunBusy(async () =>
        {
            if (!on)
            {
                await dpi.StopAsync();
                return;
            }
            var s = dpi.GetStatus();
            var preset = DpiPresets.Get(settings.PresetId);
            if (s.State == DpiState.NotInstalled || s.InstalledPresetId != preset.Id || s.InstalledDnsRedirect != WantDnsRedirect)
                await dpi.InstallAsync(preset, WantDnsRedirect);
            await dpi.StartAsync();
        });
    }

    async Task SetPresetAsync(string id)
    {
        var preset = DpiPresets.Get(id);
        settings.PresetId = preset.Id;
        settings.Save();
        // The installer restarts the service itself if it was running.
        if (dpi.GetStatus().State != DpiState.NotInstalled)
            await RunBusy(() => dpi.InstallAsync(preset, WantDnsRedirect));
        else
            PushState();
    }

    // GoodbyeDPI's own DNS fix is only needed while the system uses the ISP's DNS.
    bool WantDnsRedirect => dns.Mode == DnsMode.Auto;

    async Task ApplyDnsAsync(DnsMode mode, string providerId)
    {
        await RunBusy(() => DnsService.ApplyAsync(mode, providerId, DpiPresets.Get(settings.PresetId).Id));
        if (dns.Mode == mode)
            Notify("ok", mode switch
            {
                DnsMode.Doh => "Şifreli DNS açık.",
                DnsMode.Plain => "DNS değiştirildi.",
                _ => "DNS eski haline döndü.",
            });
    }

    async Task RunBusy(Func<Task> action)
    {
        busy = true;
        PushState();
        try
        {
            await action();
        }
        catch (Exception e)
        {
            Notify("error", e.Message);
        }
        finally
        {
            busy = false;
            RefreshStatus(force: true);
        }
    }

    void RefreshStatus(bool force = false)
    {
        var s = dpi.GetStatus();
        bool changed = s.State != status.State || s.InstalledPresetId != status.InstalledPresetId
                       || s.InstalledDnsRedirect != status.InstalledDnsRedirect
                       || !s.Conflicts.SequenceEqual(status.Conflicts);
        status = s;

        dns = DnsService.GetStatus();
        string key = DnsKey(dns);
        changed |= key != dnsKey;
        dnsKey = key;
        // The system DNS points at the forwarder; if it died, nothing resolves. Bring it back.
        if (dns.Mode == DnsMode.Doh && dns.ForwarderState == "stopped" && !busy && DnsService.TryReviveForwarder())
            Notify("error", "Şifreli DNS hizmeti durmuştu, yeniden başlatıldı.");

        if (changed || force)
        {
            UpdateTray();
            PushState();
        }
    }

    static string DnsKey(DnsStatus d) =>
        $"{d.Mode}|{d.ProviderId}|{d.ForwarderState}|{string.Join(",", d.Adapters.SelectMany(a => a.V4Dns.Concat(a.V6Dns)))}";

    // ---- Tray ----

    void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Geçit'i aç", null, (_, _) => ShowFromTray());
        trayToggle.Click += async (_, _) => await SetDpiAsync(status.State != DpiState.Running);
        menu.Items.Add(trayToggle);
        menu.Items.Add("Gizli tarayıcı", null, (_, _) => OpenBrowser());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Çıkış", null, (_, _) => ExitApp());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => ShowFromTray();
        tray.Visible = true;
        UpdateTray();
    }

    void UpdateTray()
    {
        bool on = status.State == DpiState.Running;
        tray.Icon = on ? iconOn : iconOff;
        tray.Text = on ? "Geçit: engel aşma açık" : "Geçit: engel aşma kapalı";
        trayToggle.Text = on ? "Engel aşmayı kapat" : "Engel aşmayı aç";
        trayToggle.Enabled = !busy;
    }

    // ---- WebView bridge ----

    async Task InitWebViewAsync()
    {
        if (web.CoreWebView2 is not null)
            return;
        var env = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewDataDir);
        await web.EnsureCoreWebView2Async(env);
        var core = web.CoreWebView2!;
#if DEBUG
        const bool dev = true;
#else
        const bool dev = false;
#endif
        core.Settings.AreDevToolsEnabled = dev;
        core.Settings.AreDefaultContextMenusEnabled = dev;
        core.Settings.AreBrowserAcceleratorKeysEnabled = dev;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.SetVirtualHostNameToFolderMapping("gecit.example", AppPaths.WebRoot, CoreWebView2HostResourceAccessKind.DenyCors);
        // The UI is local files; a stale HTTP cache would show the previous version's CSS/JS after an update.
        await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache);
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };
        core.WebMessageReceived += OnWebMessage;
        web.Source = new Uri(AppOrigin + "index.html");
    }

    async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith(AppOrigin, StringComparison.Ordinal))
            return;

        JsonNode? msg = JsonNode.Parse(e.WebMessageAsJson);
        int id = msg?["id"]?.GetValue<int>() ?? 0;
        string cmd = msg?["cmd"]?.GetValue<string>() ?? "";
        JsonNode? args = msg?["args"];
        try
        {
            object? result = await HandleAsync(cmd, args);
            Post(new { id, ok = true, result });
        }
        catch (Exception ex)
        {
            Post(new { id, ok = false, error = ex.Message });
        }
    }

    async Task<object?> HandleAsync(string cmd, JsonNode? args)
    {
        switch (cmd)
        {
            case "getState":
                BeginInvoke(ShowPendingToast);   // the page asks for its state once it has loaded
                return BuildState();
            case "dpi.set":
                await SetDpiAsync(args!["on"]!.GetValue<bool>());
                return BuildState();
            case "dpi.preset":
                await SetPresetAsync(args!["id"]!.GetValue<string>());
                return BuildState();
            case "app.uninstall":
                settings.DpiWanted = false;
                settings.Save();
                await RunBusy(dpi.UninstallAllAsync);
                return BuildState();
            case "probe.run":
                return await SiteProbe.RunAllAsync();
            case "dns.bench":
                return await DnsService.BenchmarkAsync();
            case "dns.diagnose":
                return await DnsService.DiagnoseAsync(
                    goodbyeDpiRedirectsDns: status.State == DpiState.Running && status.InstalledDnsRedirect);
            case "dns.apply":
                await ApplyDnsAsync(Enum.Parse<DnsMode>(args!["mode"]!.GetValue<string>(), ignoreCase: true),
                    args["provider"]!.GetValue<string>());
                return BuildState();
            case "browser.open":
                OpenBrowser();
                return null;
            case "browser.settings":
                if (args?["adblock"] is JsonNode ab)
                    settings.BrowserAdBlock = ab.GetValue<bool>();
                if (args?["search"] is JsonNode se)
                    settings.BrowserSearch = SearchEngines.Get(se.GetValue<string>()).Id;
                settings.Save();
                return BuildState();
            case "browser.shortcut":
                CreateBrowserShortcut();
                return null;
            case var c when c.StartsWith("game.", StringComparison.Ordinal):
                return await HandleGameAsync(cmd, args);
            case var c when c.StartsWith("update.", StringComparison.Ordinal) || c.StartsWith("lan.", StringComparison.Ordinal)
                            || c.StartsWith("boot.", StringComparison.Ordinal):
                return await HandleToolsAsync(cmd, args);
            case "net.ping":
                ping.KeepAlive();
                var (inet, gw) = ping.Snapshot();
                return new { internet = inet, gateway = gw };
            case "net.target":
                ping.SetTarget(args!["target"]!.GetValue<string>());
                return null;
            case "net.speed":
                speedCts?.Cancel();
                speedCts = new CancellationTokenSource();
                try
                {
                    return await SpeedTest.RunAsync(
                        p => BeginInvoke(() => Post(new { @event = "speed", data = p })), speedCts.Token);
                }
                catch (OperationCanceledException) { return null; }
            case "net.speed.stop":
                speedCts?.Cancel();
                return null;
            case "net.traffic.start":
                await traffic.StartAsync();
                return new { running = traffic.Running };
            case "net.traffic.stop":
                traffic.Stop();
                return new { running = false };
            case "net.traffic":
                return new { running = traffic.Running, since = traffic.Since, apps = traffic.Snapshot() };
            case "app.autostart":
                Autostart.Set(args!["on"]!.GetValue<bool>());
                return BuildState();
            case "app.open":
                OpenExternal(args!["url"]!.GetValue<string>());
                return null;
            default:
                throw new InvalidOperationException($"Bilinmeyen komut: {cmd}");
        }
    }

    object BuildState() => new
    {
        version = Application.ProductVersion.Split('+')[0],
        autostart = Autostart.IsEnabled,
        presetId = settings.PresetId,
        presets = DpiPresets.All,
        busy,
        dpi = new
        {
            state = status.State.ToString().ToLowerInvariant(),
            installedPresetId = status.InstalledPresetId,
            dnsRedirect = status.InstalledDnsRedirect,
            conflicts = status.Conflicts,
            wanted = settings.DpiWanted,
            busy,
        },
        dns = new
        {
            mode = dns.Mode.ToString().ToLowerInvariant(),
            providerId = dns.ProviderId,
            forwarder = dns.ForwarderState,
            adapters = dns.Adapters.Select(a => new { a.Name, servers = a.V4Dns.Concat(a.V6Dns) }),
        },
        dnsProviders = DnsProviders.All.Select(p => new { p.Id, p.Name, p.Tag, servers = p.V4 }),
        browser = new
        {
            open = browser is { IsDisposed: false },
            adblock = settings.BrowserAdBlock,
            search = settings.BrowserSearch,
            engines = SearchEngines.All.Select(e => new { e.Id, e.Name }),
            listCount = adBlocker.Count,
            listDate = adBlocker.ListDate,
        },
        game = GameState(),
        update = UpdateState(),
        lanWatch = settings.LanWatch,
    };

    void PushState() => Post(new { @event = "state", data = BuildState() });

    void Notify(string level, string message)
    {
        // Window never opened (started in tray): fall back to a balloon.
        if (web.CoreWebView2 is null || !Visible)
            tray.ShowBalloonTip(4000, "Geçit", message, level == "error" ? ToolTipIcon.Error : ToolTipIcon.Info);
        Post(new { @event = "toast", data = new { level, message } });
    }

    void Post(object payload) =>
        web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload, Json));

    static void OpenExternal(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    static Icon LoadIcon(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
        return new Icon(s);
    }

}
