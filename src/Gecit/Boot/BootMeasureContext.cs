using System.Diagnostics;
using System.Reflection;

namespace Gecit.Boot;

// "Gecit.exe --boot-measure" from RunOnce: measure this logon without opening the app, say the result
// in a balloon, exit.
sealed class BootMeasureContext : ApplicationContext
{
    readonly NotifyIcon tray = new();

    public BootMeasureContext()
    {
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("gecit.ico")!)
            tray.Icon = new Icon(s);
        tray.Text = "Geçit: açılış ölçülüyor";
        tray.Visible = true;
        tray.BalloonTipClicked += (_, _) => Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true })?.Dispose();
        _ = RunAsync();
    }

    async Task RunAsync()
    {
        try
        {
            var r = await BootMeter.MeasureAsync();
            if (r is not null)
            {
                string ready = r.ReadySeconds is { } s ? $"Masaüstünden {s:0} sn sonra kullanıma hazırdı." : "Bilgisayar 3 dakika boyunca meşguldü.";
                int heavy = r.Items.Count(i => BootMeter.Impact(i.CpuMs, i.IoBytes) == "high");
                tray.ShowBalloonTip(10000, "Açılış ölçüldü", $"{ready} {(heavy > 0 ? $"{heavy} program açılışı ciddi yavaşlatıyor. " : "")}Ayrıntılar için tıkla.", ToolTipIcon.Info);
                await Task.Delay(12000);
            }
        }
        catch (Exception e)
        {
            try { File.AppendAllText(Program.ErrorLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] boot-measure: {e}\n\n"); }
            catch (IOException) { }
        }
        tray.Visible = false;
        tray.Dispose();
        ExitThread();
    }
}
