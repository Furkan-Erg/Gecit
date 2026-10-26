using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gecit.Dns;
using Gecit.Dpi;
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

    bool allowShow;
    bool exiting;
    bool busy;
    bool trayHintShown;
    DpiStatus status;
    DnsStatus dns;
    string dnsKey = "";

    public MainForm(bool startHidden)
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
        BuildTray();
        poll.Tick += (_, _) => RefreshStatus();
        poll.Start();

        ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => BeginInvoke(ShowFromTray), null, -1, false);
        _ = RestoreDpiAsync();
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
        UseDarkTitleBar();
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
        exiting = true;
        Close();
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

    void UseDarkTitleBar()
    {
        int on = 1;
        DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
