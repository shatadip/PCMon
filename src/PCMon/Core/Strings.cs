namespace PCMon.Core;

/// <summary>
/// Every joke in PCMon lives here (SRS §6). Data labels stay serious;
/// only status, empty states, confirmations and About get personality.
/// </summary>
public static class Strings
{
    public const string AllGreen = "All systems nominal. Suspiciously nominal.";
    // WPF can't draw colour emoji (newer ones render as boxes), so in-app strings stay emoji-free.
    public const string CpuHot = "CPU is hotter than your takes. Check cooling.";
    public const string GpuHot = "GPU is running a fever. Check cooling.";
    public const string RamFull = "RAM is full. Chrome has entered the chat.";
    public const string Warming = "Things are warming up. Keep an eye on the amber tiles.";
    public const string RamBusy = "Memory is getting crowded. Maybe close a tab. Or forty.";
    public const string DiskFull = "A disk is nearly full. Time for some digital decluttering.";
    public const string NetIdle = "The internet is quiet. Too quiet.";
    public const string Starting = "Warming up the sensors…";

    public static string EndTaskConfirm(string name) =>
        $"End {name}? It had hopes and dreams.\n\n(Unsaved work will be lost.)";

    public static string KillBlocked(string name) =>
        $"Nice try. Killing {name} would blue-screen us both.";

    public static string KillFailed(string name, string reason) =>
        $"{name} refused to leave: {reason}";

    public const string NoUnusedApps = "No unused apps found. You use everything? Respect.";
    public const string LoadingApps = "Rummaging through the registry…";
    public const string LoadingTasks = "Asking Task Scheduler what it's been up to…";

    public static string UninstallConfirm(int count, IEnumerable<string> names) =>
        $"Uninstall {count} app(s)? They won't be missed. Probably.\n\n• " +
        string.Join("\n• ", names) +
        "\n\nEach app's own uninstaller will run, one at a time.";

    public const string SensorShy = "This sensor is shy.";
    public const string UnknownUsage = @"Unknown ¯\_(ツ)_/¯";

    public const string About =
        "PCMon v{0}\n\nMade with C#, coffee, and mild thermal anxiety.\n\n" +
        "Sensors by LibreHardwareMonitor (MPL-2.0).\nhttps://github.com/shatadip/PCMon";

    public const string CpuSensorsNeedDriver =
        "CPU temperature, clock and power sensors are unavailable. LibreHardwareMonitor needs the free " +
        "PawnIO driver (pawnio.eu) to read them. PCMon doesn't install drivers, so it falls back to the " +
        "ACPI thermal zone where it can.";
}

public sealed record StatusInput(Level CpuTemp, Level GpuTemp, Level Ram, Level Disk, double NetIdleSeconds);

/// <summary>Picks the one status-bar message that matters most right now (worst problem wins).</summary>
public static class StatusMessenger
{
    public static string Pick(StatusInput s)
    {
        if (s.CpuTemp == Level.Critical) return Strings.CpuHot;
        if (s.GpuTemp == Level.Critical) return Strings.GpuHot;
        if (s.Ram == Level.Critical) return Strings.RamFull;
        if (Thresholds.Worst(s.CpuTemp, s.GpuTemp) >= Level.Warn) return Strings.Warming;
        if (s.Ram >= Level.Warn) return Strings.RamBusy;
        if (s.Disk >= Level.Warn) return Strings.DiskFull;
        if (s.NetIdleSeconds >= 60) return Strings.NetIdle;
        return Strings.AllGreen;
    }
}
