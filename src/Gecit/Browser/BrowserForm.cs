using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Gecit.Browser;

sealed class BrowserTab(int id, WebView2 view)
{
    public int Id { get; } = id;
    public WebView2 View { get; } = view;
    public CoreWebView2 Core => View.CoreWebView2;
    public string Title { get; set; } = "Yeni sekme";
    public string Url { get; set; } = "";
    public string? Favicon { get; set; }
    public bool Loading { get; set; }
    public int Blocked { get; set; }
    // Host of the page being loaded in the main frame; its document is never blocked.
    public string? NavigationHost { get; set; }
}

// The private browser: an HTML toolbar (wwwroot/browser/chrome.html) above one InPrivate WebView2 per tab.
// Tabs share a single InPrivate profile, whose data WebView2 discards once the last tab closes.
sealed class BrowserForm : Form
{
    const string AppOrigin = "https://gecit.example/";
    const string NewTabPage = AppOrigin + "browser/newtab.html";
    const string PrivateProfile = "gecit-private";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly Color Background = Color.FromArgb(10, 8, 20);

    readonly Settings settings;
    readonly AdBlocker blocker;
    readonly WebView2 toolbar = new() { Dock = DockStyle.Top, DefaultBackgroundColor = Background };
    readonly Panel content = new() { Dock = DockStyle.Fill, BackColor = Background };
    readonly List<BrowserTab> tabs = [];
    readonly System.Windows.Forms.Timer pushTimer = new() { Interval = 100 };

    CoreWebView2Environment? env;
    BrowserTab? active;
    int nextId = 1;
    string? initialUrl;
    bool fullscreen;
    FormWindowState savedWindowState;

    public BrowserForm(Settings settings, AdBlocker blocker, Icon icon, string? url = null)
    {
        this.settings = settings;
        this.blocker = blocker;
        initialUrl = url;
        Text = "Geçit · Gizli Tarayıcı";
        Icon = icon;
        BackColor = Background;
        ClientSize = new Size(1280, 800);
        MinimumSize = new Size(640, 420);
        StartPosition = FormStartPosition.CenterScreen;
        toolbar.Height = LogicalToDeviceUnits(84);
        Controls.Add(toolbar);
        Controls.Add(content);
        content.BringToFront();   // the Fill control must come after the Top one in docking order
        pushTimer.Tick += (_, _) => { pushTimer.Stop(); PushState(); };
    }

    SearchEngine Engine => SearchEngines.Get(settings.BrowserSearch);
    string NewTabUrl => $"{NewTabPage}?e={Engine.Id}";

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        NativeTheme.UseDarkTitleBar(Handle);
        env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.LocalDir, "Browser"));

        await toolbar.EnsureCoreWebView2Async(env);
        var core = toolbar.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.SetVirtualHostNameToFolderMapping("gecit.example", AppPaths.WebRoot, CoreWebView2HostResourceAccessKind.DenyCors);
        core.WebMessageReceived += OnToolbarMessage;
        core.Navigate(AppOrigin + "browser/chrome.html");

        await NewTabAsync(initialUrl);
    }

    // Opens a URL in a new tab of an already open window (e.g. from the main app).
    public async void OpenUrl(string? url)
    {
        if (env is null)
        {
            initialUrl = url;
            return;
        }
        await NewTabAsync(url);
    }

    // ---- Tabs ----

    async Task<BrowserTab> NewTabAsync(string? url, bool navigate = true)
    {
        var view = new WebView2 { Dock = DockStyle.Fill, Visible = false, DefaultBackgroundColor = Background };
        content.Controls.Add(view);
        var options = env!.CreateCoreWebView2ControllerOptions();
        options.ProfileName = PrivateProfile;
        options.IsInPrivateModeEnabled = true;
        await view.EnsureCoreWebView2Async(env, options);

        var tab = new BrowserTab(nextId++, view);
        tabs.Add(tab);
        Wire(tab);
        if (navigate)
            tab.Core.Navigate(url ?? NewTabUrl);
        Activate(tab);
        return tab;
    }

    void Wire(BrowserTab tab)
    {
        var core = tab.Core;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Profile.PreferredTrackingPreventionLevel = settings.BrowserAdBlock
            ? CoreWebView2TrackingPreventionLevel.Strict
            : CoreWebView2TrackingPreventionLevel.Balanced;
        core.SetVirtualHostNameToFolderMapping("gecit.example", AppPaths.WebRoot, CoreWebView2HostResourceAccessKind.DenyCors);

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => OnRequest(tab, e);

        core.NavigationStarting += (_, e) =>
        {
            tab.Loading = true;
            tab.Blocked = 0;
            tab.NavigationHost = Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) ? u.Host : null;
            MarkDirty();
        };
        core.NavigationCompleted += (_, _) => { tab.Loading = false; MarkDirty(); };
        core.SourceChanged += (_, _) => { tab.Url = core.Source; MarkDirty(); };
        core.DocumentTitleChanged += (_, _) =>
        {
            tab.Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? tab.Url : core.DocumentTitle;
            if (tab == active)
                Text = $"{tab.Title} · Gizli Tarayıcı";
            MarkDirty();
        };
        core.HistoryChanged += (_, _) => MarkDirty();
        core.FaviconChanged += async (_, _) =>
        {
            try
            {
                using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
                if (stream is null || stream.Length == 0) { tab.Favicon = null; }
                else
                {
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms);
                    tab.Favicon = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                }
                MarkDirty();
            }
            catch (Exception) when (tab.View.IsDisposed) { }
        };
        core.NewWindowRequested += async (_, e) =>
        {
            // Hand the page a real tab as its popup, so window.opener (e.g. "Sign in with Google") keeps working.
            var deferral = e.GetDeferral();
            try
            {
                var popup = await NewTabAsync(null, navigate: false);
                e.NewWindow = popup.Core;
                e.Handled = true;
            }
            finally
            {
                deferral.Complete();
            }
        };
        core.WindowCloseRequested += (_, _) => CloseTab(tab);
        core.ContainsFullScreenElementChanged += (_, _) =>
        {
            if (tab == active)
                SetFullscreen(core.ContainsFullScreenElement);
        };
    }

    void OnRequest(BrowserTab tab, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!settings.BrowserAdBlock || !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri))
            return;
        string host = uri.Host;
        // Never block the page the user asked for, only what it pulls in.
        if (e.ResourceContext == CoreWebView2WebResourceContext.Document && host == tab.NavigationHost)
            return;
        if (!blocker.IsBlocked(host))
            return;
        e.Response = env!.CreateWebResourceResponse(null, 403, "Blocked", "");
        tab.Blocked++;
        MarkDirty();
    }

    void Activate(BrowserTab tab)
    {
        active = tab;
        foreach (var t in tabs)
            t.View.Visible = t == tab;
        Text = $"{tab.Title} · Gizli Tarayıcı";
        if (fullscreen != tab.Core.ContainsFullScreenElement)
            SetFullscreen(tab.Core.ContainsFullScreenElement);
        tab.View.Focus();
        MarkDirty();
    }

    void CloseTab(BrowserTab tab)
    {
        int index = tabs.IndexOf(tab);
        if (index < 0)
            return;
        tabs.RemoveAt(index);
        content.Controls.Remove(tab.View);
        tab.View.Dispose();
        if (tabs.Count == 0)
        {
            Close();
            return;
        }
        if (tab == active)
            Activate(tabs[Math.Min(index, tabs.Count - 1)]);
        MarkDirty();
    }

    void CycleTab(int delta)
    {
        if (active is null || tabs.Count < 2)
            return;
        int i = (tabs.IndexOf(active) + delta + tabs.Count) % tabs.Count;
        Activate(tabs[i]);
    }

    // Wipes everything the private profile holds and starts over with one empty tab.
    async Task ClearAllAsync()
    {
        if (active is not null)
            await active.Core.Profile.ClearBrowsingDataAsync();
        foreach (var t in tabs)
        {
            content.Controls.Remove(t.View);
            t.View.Dispose();
        }
        tabs.Clear();
        active = null;
        await NewTabAsync(null);
    }

    void SetFullscreen(bool on)
    {
        if (on == fullscreen)
            return;
        fullscreen = on;
        toolbar.Visible = !on;
        if (on)
        {
            savedWindowState = WindowState;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;   // re-maximizing after removing the border covers the taskbar
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = savedWindowState;
        }
    }

    // ---- Toolbar bridge ----

    async void OnToolbarMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith(AppOrigin, StringComparison.Ordinal))
            return;
        var msg = JsonNode.Parse(e.WebMessageAsJson);
        string cmd = msg?["cmd"]?.GetValue<string>() ?? "";
        int id = msg?["id"]?.GetValue<int>() ?? 0;
        var tab = tabs.FirstOrDefault(t => t.Id == id);
        switch (cmd)
        {
            case "ready": PushState(); break;
            case "new": NewTabAndFocusAddress(); break;
            case "close": if (tab is not null) CloseTab(tab); break;
            case "activate": if (tab is not null) Activate(tab); break;
            case "go":
                string text = msg?["text"]?.GetValue<string>() ?? "";
                if (active is not null && text.Trim().Length > 0)
                {
                    active.Core.Navigate(SearchEngines.Resolve(text, Engine));
                    active.View.Focus();
                }
                break;
            case "back": if (active?.Core.CanGoBack == true) active.Core.GoBack(); break;
            case "forward": if (active?.Core.CanGoForward == true) active.Core.GoForward(); break;
            case "reload": active?.Core.Reload(); break;
            case "stop": active?.Core.Stop(); break;
            case "home": active?.Core.Navigate(NewTabUrl); break;
            case "clear": await ClearAllAsync(); break;
            case "focusPage": active?.View.Focus(); break;
        }
    }

    void MarkDirty()
    {
        if (!pushTimer.Enabled)
            pushTimer.Start();
    }

    void PushState()
    {
        if (toolbar.CoreWebView2 is null)
            return;
        var state = new
        {
            activeId = active?.Id,
            canBack = active?.Core.CanGoBack ?? false,
            canForward = active?.Core.CanGoForward ?? false,
            adblock = settings.BrowserAdBlock,
            tabs = tabs.Select(t => new
            {
                t.Id,
                t.Title,
                url = t.Url.StartsWith(NewTabPage, StringComparison.Ordinal) ? "" : t.Url,
                t.Favicon,
                t.Loading,
                t.Blocked,
            }),
        };
        toolbar.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { state }, Json));
    }

    async void NewTabAndFocusAddress()
    {
        await NewTabAsync(null);
        FocusAddress();
    }

    void FocusAddress()
    {
        toolbar.Focus();
        toolbar.CoreWebView2?.PostWebMessageAsJson("""{"focusAddress":true}""");
    }

    // Content WebView2s forward accelerator keys here.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.T: NewTabAndFocusAddress(); return true;
            case Keys.Control | Keys.W: if (active is not null) CloseTab(active); return true;
            case (Keys.Control | Keys.L) or (Keys.Alt | Keys.D) or Keys.F6: FocusAddress(); return true;
            case Keys.Control | Keys.Tab: CycleTab(+1); return true;
            case Keys.Control | Keys.Shift | Keys.Tab: CycleTab(-1); return true;
            case Keys.F5 or (Keys.Control | Keys.R): active?.Core.Reload(); return true;
            case Keys.Alt | Keys.Left: if (active?.Core.CanGoBack == true) active.Core.GoBack(); return true;
            case Keys.Alt | Keys.Right: if (active?.Core.CanGoForward == true) active.Core.GoForward(); return true;
            case Keys.Control | Keys.Shift | Keys.Delete: _ = ClearAllAsync(); return true;
            case Keys.F11: SetFullscreen(!fullscreen); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        foreach (var t in tabs)
            t.View.Dispose();
        tabs.Clear();
        toolbar.Dispose();
        base.OnFormClosed(e);
    }
}
