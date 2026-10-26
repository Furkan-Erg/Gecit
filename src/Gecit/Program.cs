namespace Gecit;

static class Program
{
    const string InstanceName = "Gecit.SingleInstance";
    public const string ShowEventName = "Gecit.ShowWindow";
    public const string BrowserEventName = "Gecit.OpenBrowser";
    public const string ExitEventName = "Gecit.Exit";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--elevated")
            return Elevated.Run(args[1..]);
        // The GecitDNS service (started by the service manager as LocalService).
        if (args.Length > 0 && args[0] == "--dns-service")
        {
            System.ServiceProcess.ServiceBase.Run(new Dns.DnsWindowsService(
                Dns.DnsProviders.Find(args.ElementAtOrDefault(1)) ?? Dns.DnsProviders.All[0]));
            return 0;
        }

        // --exit: ask a running instance to shut down cleanly (setup update/uninstall). Waits until it's gone.
        if (args.Contains("--exit"))
        {
            if (EventWaitHandle.TryOpenExisting(ExitEventName, out var exit))
                using (exit) exit.Set();
            for (int i = 0; i < 50 && Mutex.TryOpenExisting(InstanceName, out var running); i++)
            {
                running.Dispose();
                Thread.Sleep(100);
            }
            return 0;
        }

        // --boot-measure: one logon measurement scheduled through RunOnce (Açılış page); no window.
        if (args.Contains("--boot-measure"))
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new Boot.BootMeasureContext());
            return 0;
        }

        // --browser: open only the private browser (desktop shortcut); the main window stays in the tray.
        bool browserOnly = args.Contains("--browser");

        using var mutex = new Mutex(initiallyOwned: true, InstanceName, out bool first);
        if (!first)
        {
            // Already running (probably in the tray): ask it to show its window or the browser.
            if (EventWaitHandle.TryOpenExisting(browserOnly ? BrowserEventName : ShowEventName, out var signal))
                using (signal) signal.Set();
            return 0;
        }

        ApplicationConfiguration.Initialize();
        // Errors in async event handlers land here; log them instead of killing the tray app.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportError(e.Exception, fatal: false);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportError((Exception)e.ExceptionObject, fatal: true);
        Application.Run(new MainForm(startHidden: args.Contains("--tray") || browserOnly, openBrowser: browserOnly));
        return 0;
    }

    public static string ErrorLog => Path.Combine(AppPaths.LocalDir, "error.log");

    static void ReportError(Exception e, bool fatal)
    {
        try { File.AppendAllText(ErrorLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {(fatal ? "FATAL" : "ERROR")} {e}\n\n"); }
        catch (IOException) { }
        if (!fatal)
            MessageBox.Show($"Beklenmeyen bir hata oldu:\n\n{e.Message}\n\nAyrıntılar: {ErrorLog}", "Geçit",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
