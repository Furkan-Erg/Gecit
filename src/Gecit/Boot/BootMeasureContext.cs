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
        tray.Text = L.T("Geçit: açılış ölçülüyor");
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
                string ready = r.ReadySeconds is { } s
                    ? L.T("Masaüstünden {s} sn sonra kullanıma hazırdı.", ("s", s.ToString("0", L.Culture)))
                    : L.T("Bilgisayar 3 dakika boyunca meşguldü.");
                int heavy = r.Items.Count(i => BootMeter.Impact(i.CpuMs, i.IoBytes) == "high");
                string slow = heavy > 0 ? L.T("{n} program açılışı ciddi yavaşlatıyor.", ("n", heavy)) + " " : "";
                tray.ShowBalloonTip(10000, L.T("Açılış ölçüldü"), $"{ready} {slow}{L.T("Ayrıntılar için tıkla.")}", ToolTipIcon.Info);
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
