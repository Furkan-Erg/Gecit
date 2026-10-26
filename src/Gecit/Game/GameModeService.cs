using System.Diagnostics;
using System.Text.Json;

namespace Gecit.Game;

sealed record FrozenProcess(int Pid, long StartTicks, string Name);

// Everything a session changed, persisted so a crash of Geçit can't leave apps frozen.
sealed class GameSession
{
    public string? GameExe { get; set; }
    public string GameName { get; set; } = "";
    public int GamePid { get; set; }
    public long GameStartTicks { get; set; }
    public bool Manual { get; set; }
    public DateTime Started { get; set; } = DateTime.Now;
    public string? PreviousPowerPlan { get; set; }
    public List<FrozenProcess> Frozen { get; set; } = [];
    public List<string> Closed { get; set; } = [];           // exe paths to relaunch afterwards
    public long AvailableBefore { get; set; }
    public long FreedBytes { get; set; }
    public int PeakGpuTemp { get; set; }
    public List<string> Notes { get; set; } = [];
}

sealed record SessionReport(string GameName, TimeSpan Duration, long FreedBytes, int PeakGpuTemp, int Frozen, int Closed, List<string> Notes, DateTime Ended);

// Runs on the UI thread: Tick() is driven by a WinForms timer.
sealed class GameModeService
{
    static string SessionFile => Path.Combine(AppPaths.LocalDir, "gamemode-session.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    readonly GameModeSettings settings;
    List<GameEntry> games = [];
    HashSet<string> gameExes = new(StringComparer.OrdinalIgnoreCase);

    public GameSession? Active { get; private set; }
    public SessionReport? LastReport { get; private set; }
    public event Action? Changed;

    public GameModeService(GameModeSettings settings)
    {
        this.settings = settings;
        RecoverCrashedSession();
        RefreshGames();
    }

    public IReadOnlyList<GameEntry> Games => games;

    public void RefreshGames()
    {
        games = GameCatalog.All(settings.CustomGames);
        gameExes = games.Select(g => g.Exe).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlySet<string> GameExes => gameExes;

    // ---- Detection ----

    public void Tick()
    {
        if (Active is { Manual: false } s)
        {
            if (!StillRunning(s.GamePid, s.GameStartTicks))
                Stop();
            return;
        }
        if (Active is not null || !settings.AutoDetect)
            return;

        var game = FindRunningGame();
        if (game is not null)
            Start(game.Value.Process, game.Value.Name, manual: false);
    }

    (Process Process, string Name)? FindRunningGame()
    {
        Process? hit = null;
        foreach (var p in Process.GetProcesses())
        {
            if (hit is null && gameExes.Contains(p.ProcessName))
                hit = p;
            else
                p.Dispose();
        }
        if (hit is not null)
            return (hit, games.First(g => g.Exe.Equals(hit.ProcessName, StringComparison.OrdinalIgnoreCase)).Name);
        if (settings.DetectFullscreen && NativeProcess.D3DFullscreenRunning())
        {
            try
            {
                var p = Process.GetProcessById(NativeProcess.ForegroundPid());
                if (!GameCatalog.IsProtected(p.ProcessName) && p.Id != Environment.ProcessId)
                    return (p, Describe(p));
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    static string Describe(Process p)
    {
        try { return p.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } d ? d : p.ProcessName; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return p.ProcessName; }
    }

    static bool StillRunning(int pid, long startTicks)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && NativeProcess.StartTicks(p) == startTicks;
        }
        catch (ArgumentException) { return false; }
    }

    // ---- Session ----

    public void StartManual() => Start(null, "Elle açıldı", manual: true);

    void Start(Process? game, string name, bool manual)
    {
        var s = new GameSession
        {
            GameExe = game?.ProcessName,
            GameName = name,
            GamePid = game?.Id ?? 0,
            GameStartTicks = game is null ? 0 : NativeProcess.StartTicks(game),
            Manual = manual,
            AvailableBefore = NativeProcess.Memory().AvailableBytes,
        };
        Active = s;
        Save(s);   // before touching anything, so a crash mid-way is still recoverable

        if (settings.PowerPlan)
        {
            string? current = PowerPlan.Active();
            if (current != PowerPlan.HighPerformance && PowerPlan.Exists(PowerPlan.HighPerformance))
            {
                s.PreviousPowerPlan = current;
                if (!PowerPlan.SetActive(PowerPlan.HighPerformance))
                    s.Notes.Add("Güç planı değiştirilemedi.");
            }
        }

        if (game is not null && settings.HighPriority)
        {
            try { game.PriorityClass = ProcessPriorityClass.High; }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                s.Notes.Add("Oyunun önceliği değiştirilemedi (oyun yönetici olarak çalışıyor ya da hile koruması izin vermiyor).");
            }
        }
        game?.Dispose();

        foreach (var (target, action) in settings.Targets)
        {
            if (GameCatalog.IsProtected(target) || gameExes.Contains(target) || target.Equals(s.GameExe, StringComparison.OrdinalIgnoreCase))
                continue;
            var procs = Process.GetProcessesByName(target).Where(p => p.SessionId == Process.GetCurrentProcess().SessionId).ToList();
            if (action == "close")
                CloseApp(s, target, procs);
            else
                FreezeApp(s, target, procs);
            foreach (var p in procs)
                p.Dispose();
        }
        Save(s);

        // Measure what the game actually got once the trimmed pages have left RAM.
        _ = Task.Delay(1500).ContinueWith(_ =>
        {
            s.FreedBytes = Math.Max(0, NativeProcess.Memory().AvailableBytes - s.AvailableBefore);
            Changed?.Invoke();
        }, TaskScheduler.FromCurrentSynchronizationContext());
        Changed?.Invoke();
    }

    static void FreezeApp(GameSession s, string name, List<Process> procs)
    {
        int failed = 0;
        foreach (var p in procs)
        {
            if (NativeProcess.SuspendAndTrim(p.Id))
                s.Frozen.Add(new(p.Id, NativeProcess.StartTicks(p), name));
            else
                failed++;
        }
        if (failed > 0)
            s.Notes.Add($"{name}: {failed} işlem dondurulamadı (yönetici olarak çalışıyor olabilir).");
    }

    static void CloseApp(GameSession s, string name, List<Process> procs)
    {
        string? path = null;
        foreach (var p in procs.Where(p => p.MainWindowHandle != IntPtr.Zero))
        {
            try { path ??= p.MainModule?.FileName; }
            catch (System.ComponentModel.Win32Exception) { }
            p.CloseMainWindow();   // a polite close; apps may ask to save, nothing is killed
        }
        if (path is not null)
            s.Closed.Add(path);
        else if (procs.Count > 0)
            s.Notes.Add($"{name}: penceresi yok, kapatılamadı. Bunun yerine 'Dondur' seç.");
    }

    public void Stop()
    {
        if (Active is not { } s)
            return;
        Restore(s, relaunch: settings.RelaunchClosed);
        LastReport = new(s.GameName, DateTime.Now - s.Started, s.FreedBytes, s.PeakGpuTemp,
            s.Frozen.Count, s.Closed.Count, s.Notes, DateTime.Now);
        Active = null;
        File.Delete(SessionFile);
        Changed?.Invoke();
    }

    static void Restore(GameSession s, bool relaunch)
    {
        foreach (var f in s.Frozen)
        {
            // Only thaw the exact process we froze; the PID may have been reused since.
            try
            {
                using var p = Process.GetProcessById(f.Pid);
                if (NativeProcess.StartTicks(p) == f.StartTicks)
                    NativeProcess.Resume(f.Pid);
            }
            catch (ArgumentException) { }
        }
        // Leave the plan alone if the user switched it themselves during the game.
        if (s.PreviousPowerPlan is not null && PowerPlan.Active() == PowerPlan.HighPerformance)
            PowerPlan.SetActive(s.PreviousPowerPlan);
        if (relaunch)
        {
            foreach (string path in s.Closed.Where(File.Exists))
            {
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
    }

    public void NoteGpuTemp(int tempC)
    {
        if (Active is { } s && tempC > s.PeakGpuTemp)
            s.PeakGpuTemp = tempC;
    }

    static void Save(GameSession s) => File.WriteAllText(SessionFile, JsonSerializer.Serialize(s, Json));

    void RecoverCrashedSession()
    {
        if (!File.Exists(SessionFile))
            return;
        try
        {
            var s = JsonSerializer.Deserialize<GameSession>(File.ReadAllText(SessionFile));
            if (s is not null)
                Restore(s, relaunch: false);
        }
        catch (JsonException) { }
        File.Delete(SessionFile);
    }
}
