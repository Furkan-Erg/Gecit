namespace Gecit;

static class Program
{
    const string InstanceName = "Gecit.SingleInstance";
    public const string ShowEventName = "Gecit.ShowWindow";
    public const string BrowserEventName = "Gecit.OpenBrowser";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--elevated")
            return Elevated.Run(args[1..]);

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
