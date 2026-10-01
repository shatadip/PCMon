using System.Text;
using System.Text.RegularExpressions;

namespace PCMon.Core;

public static class Rot13
{
    public static string Decode(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                >= 'a' and <= 'z' => (char)('a' + (c - 'a' + 13) % 26),
                >= 'A' and <= 'Z' => (char)('A' + (c - 'A' + 13) % 26),
                _ => c,
            });
        }
        return sb.ToString();
    }
}

/// <summary>Rules for which Uninstall registry entries are real, user-facing apps (SRS FR-15).</summary>
public static class InstalledAppFilter
{
    public static bool Include(string? displayName, int systemComponent, string? parentKeyName,
        string? uninstallString, string? releaseType)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return false;
        if (systemComponent == 1) return false;
        if (!string.IsNullOrWhiteSpace(parentKeyName)) return false;
        if (string.IsNullOrWhiteSpace(uninstallString)) return false;
        if (releaseType is not null &&
            (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
             releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)))
            return false;
        return true;
    }

    /// <summary>InstallDate is "yyyyMMdd" when vendors bother to set it.</summary>
    public static DateTime? ParseInstallDate(string? value) =>
        value is { Length: 8 } &&
        DateTime.TryParseExact(value, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d)
            ? d
            : null;
}

public static class UnusedRule
{
    /// <param name="days">0 means "Show all".</param>
    public static bool IsUnused(DateTime? lastUsed, int days, DateTime now, bool includeUnknown)
    {
        if (days <= 0) return true;
        if (lastUsed is null) return includeUnknown;
        return (now - lastUsed.Value).TotalDays >= days;
    }
}

/// <summary>Where "last used" estimates come from (SRS FR-16).</summary>
public static class UsageEvidence
{
    /// <summary>"CHROME.EXE-1A2B3C4D.pf" → "CHROME.EXE".</summary>
    public static string? ExeFromPrefetch(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        int dash = name.LastIndexOf('-');
        if (dash <= 0) return null;
        var exe = name[..dash];
        return exe.EndsWith(".EXE", StringComparison.OrdinalIgnoreCase) ? exe.ToUpperInvariant() : null;
    }

    /// <summary>UserAssist "Count" value data (Win7+): 72 bytes, last-run FILETIME at offset 60.</summary>
    public static DateTime? LastRunFromUserAssist(byte[]? data)
    {
        if (data is null || data.Length < 68) return null;
        long ft = BitConverter.ToInt64(data, 60);
        if (ft <= 0) return null;
        try { return DateTime.FromFileTimeUtc(ft).ToLocalTime(); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>
    /// Exe names that say nothing about whether the app itself is used
    /// (uninstallers, updaters, crash reporters, or names too generic to match on).
    /// </summary>
    public static bool IsNoiseExe(string exeName)
    {
        var n = exeName.ToLowerInvariant();
        return n.StartsWith("unins") || n.StartsWith("uninst") || n.StartsWith("setup") ||
               n.StartsWith("update") || n.StartsWith("install") || n.Contains("crash") ||
               n is "helper.exe" or "launcher.exe" or "elevate.exe" or "notification_helper.exe";
    }
}

/// <summary>Turns a registry UninstallString into something Process.Start can run (SRS FR-18).</summary>
public static partial class UninstallCommand
{
    public static (string File, string Args) Split(string command, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var s = command.Trim();
        (string File, string Args) result;

        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            result = end > 0 ? (s[1..end], s[(end + 1)..].Trim()) : (s.Trim('"'), "");
        }
        else
        {
            result = SplitUnquoted(s, fileExists);
        }

        return NormalizeMsi(result);
    }

    static (string, string) SplitUnquoted(string s, Func<string, bool> fileExists)
    {
        // Unquoted paths may contain spaces: "C:\Program Files\Foo\uninst.exe /S".
        // Prefer the shortest prefix that is an existing .exe, then any prefix ending in .exe.
        string? firstExe = null;
        int searchFrom = 0;
        while (true)
        {
            int space = s.IndexOf(' ', searchFrom);
            var candidate = space < 0 ? s : s[..space];
            if (candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                if (fileExists(candidate))
                    return (candidate, space < 0 ? "" : s[(space + 1)..].Trim());
                firstExe ??= candidate;
            }
            if (space < 0) break;
            searchFrom = space + 1;
        }

        if (firstExe is not null)
            return (firstExe, s[firstExe.Length..].Trim());

        int sp = s.IndexOf(' ');
        return sp < 0 ? (s, "") : (s[..sp], s[(sp + 1)..].Trim());
    }

    /// <summary>"MsiExec.exe /I{GUID}" opens a modify dialog; "/X{GUID}" actually uninstalls.</summary>
    static (string, string) NormalizeMsi((string File, string Args) cmd)
    {
        if (!Path.GetFileName(cmd.File).StartsWith("msiexec", StringComparison.OrdinalIgnoreCase))
            return cmd;
        return (cmd.File, MsiInstallSwitch().Replace(cmd.Args, "/X$1"));
    }

    [GeneratedRegex(@"(?i)/I\s*(\{[0-9A-F-]+\})")]
    private static partial Regex MsiInstallSwitch();
}
