using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Gecit.Game;

// Switching the active power scheme doesn't need admin rights.
static partial class PowerPlan
{
    public const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    public static string? Active() => GuidIn(Run("/getactivescheme"));

    public static bool Exists(string guid) => Run("/list").Contains(guid, StringComparison.OrdinalIgnoreCase);

    public static bool SetActive(string guid)
    {
        Run("/setactive", guid);
        return string.Equals(Active(), guid, StringComparison.OrdinalIgnoreCase);
    }

    static string? GuidIn(string text) => GuidPattern().Match(text) is { Success: true } m ? m.Value.ToLowerInvariant() : null;

    static string Run(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "powercfg.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        foreach (string a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();
}
