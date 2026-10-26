namespace Gecit;

static class Program
{
    const string InstanceName = "Gecit.SingleInstance";
    public const string ShowEventName = "Gecit.ShowWindow";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--elevated")
            return Elevated.Run(args[1..]);

        using var mutex = new Mutex(initiallyOwned: true, InstanceName, out bool first);
        if (!first)
        {
            // Already running (probably in the tray): ask it to show its window.
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var show))
                using (show) show.Set();
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(startHidden: args.Contains("--tray")));
        return 0;
    }
}
