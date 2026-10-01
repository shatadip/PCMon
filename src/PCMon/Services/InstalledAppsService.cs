using System.Diagnostics;
using Microsoft.Win32;
using PCMon.Core;

namespace PCMon.Services;

public sealed record InstalledApp(
    string Key, string Name, string Publisher, string Version, long SizeBytes, DateTime? InstalledOn,
    DateTime? LastUsed, string? LastUsedSource, string UninstallString);

/// <summary>Installed-app inventory and "last used" estimates (SRS FR-15/16/18).</summary>
static class InstalledAppsService
{
    const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static IReadOnlyList<InstalledApp> Load()
    {
        var prefetch = PrefetchIndex();
        var userAssist = UserAssistIndex();
        var apps = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Registry64),
                 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(UninstallPath);
                if (uninstall is null) continue;

                foreach (var subName in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var k = uninstall.OpenSubKey(subName);
                        if (k is null) continue;
                        var app = Read(k, $"{hive}\\{view}\\{subName}", prefetch, userAssist);
                        if (app is not null && seen.Add(app.Name + "|" + app.Version))
                            apps.Add(app);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("uninstall entry " + subName, ex);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"uninstall hive {hive}/{view}", ex);
            }
        }

        return apps;
    }

    static InstalledApp? Read(RegistryKey k, string key, Dictionary<string, DateTime> prefetch, Dictionary<string, DateTime> userAssist)
    {
        var name = k.GetValue("DisplayName") as string;
        var uninstall = k.GetValue("UninstallString") as string;
        int systemComponent = k.GetValue("SystemComponent") is int sc ? sc : 0;
        if (!InstalledAppFilter.Include(name, systemComponent, k.GetValue("ParentKeyName") as string,
                uninstall, k.GetValue("ReleaseType") as string))
            return null;

        long size = k.GetValue("EstimatedSize") is int kb ? kb * 1024L : 0;
        var installed = InstalledAppFilter.ParseInstallDate(k.GetValue("InstallDate") as string);

        DateTime? lastUsed = null;
        string? source = null;
        foreach (var exe in CandidateExes(k))
        {
            if (prefetch.TryGetValue(exe, out var p) && (lastUsed is null || p > lastUsed)) { lastUsed = p; source = "Prefetch"; }
            if (userAssist.TryGetValue(exe, out var u) && (lastUsed is null || u > lastUsed)) { lastUsed = u; source = "UserAssist"; }
        }

        return new InstalledApp(key, name!.Trim(), (k.GetValue("Publisher") as string ?? "").Trim(),
            (k.GetValue("DisplayVersion") as string ?? "").Trim(), size, installed, lastUsed, source, uninstall!);
    }

    /// <summary>Upper-case exe file names that belong to the app: its icon exe plus exes in its install folder.</summary>
    static IEnumerable<string> CandidateExes(RegistryKey k)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? folder = k.GetValue("InstallLocation") as string;

        if (k.GetValue("DisplayIcon") is string icon)
        {
            var iconPath = icon.Split(',')[0].Trim().Trim('"');
            if (iconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                names.Add(Path.GetFileName(iconPath));
                if (string.IsNullOrWhiteSpace(folder)) folder = Path.GetDirectoryName(iconPath);
            }
        }

        folder = folder?.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            try
            {
                var exes = Directory.EnumerateFiles(folder, "*.exe").Take(40).ToList();
                if (exes.Count == 0)
                    exes = Directory.EnumerateDirectories(folder).Take(20)
                        .SelectMany(d => SafeEnumerate(d).Take(10)).Take(40).ToList();
                foreach (var e in exes) names.Add(Path.GetFileName(e));
            }
            catch (Exception ex)
            {
                Log.Error("scan " + folder, ex);
            }
        }

        return names.Where(n => !UsageEvidence.IsNoiseExe(n)).Select(n => n.ToUpperInvariant());
    }

    static IEnumerable<string> SafeEnumerate(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*.exe").ToList(); }
        catch { return []; }
    }

    /// <summary>Exe name → newest Prefetch write time. Needs admin.</summary>
    static Dictionary<string, DateTime> PrefetchIndex()
    {
        var map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
            foreach (var file in Directory.EnumerateFiles(dir, "*.pf"))
            {
                var exe = UsageEvidence.ExeFromPrefetch(Path.GetFileName(file));
                if (exe is null) continue;
                var t = File.GetLastWriteTime(file);
                if (!map.TryGetValue(exe, out var prev) || t > prev) map[exe] = t;
            }
        }
        catch (Exception ex)
        {
            Log.Error("prefetch index", ex);
        }
        return map;
    }

    /// <summary>Exe name → last run time from Explorer's ROT13-encoded UserAssist key.</summary>
    static Dictionary<string, DateTime> UserAssistIndex()
    {
        var map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var ua = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist");
            if (ua is null) return map;
            foreach (var guid in ua.GetSubKeyNames())
            {
                using var count = ua.OpenSubKey(guid + @"\Count");
                if (count is null) continue;
                foreach (var valueName in count.GetValueNames())
                {
                    var decoded = Rot13.Decode(valueName);
                    if (!decoded.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    var when = UsageEvidence.LastRunFromUserAssist(count.GetValue(valueName) as byte[]);
                    if (when is null) continue;
                    var exe = Path.GetFileName(decoded).ToUpperInvariant();
                    if (!map.TryGetValue(exe, out var prev) || when > prev) map[exe] = when.Value;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("userassist index", ex);
        }
        return map;
    }

    /// <summary>Runs the app's own uninstaller and waits for it. PCMon never deletes anything itself.</summary>
    public static async Task<int?> RunUninstallerAsync(InstalledApp app)
    {
        var (file, args) = UninstallCommand.Split(Environment.ExpandEnvironmentVariables(app.UninstallString));
        Log.Info($"Uninstall '{app.Name}': {file} {args}");
        using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
        if (p is null) return null;
        await p.WaitForExitAsync();
        return p.ExitCode;
    }
}
