using System.Diagnostics.Eventing.Reader;
using System.Management;
using Microsoft.Win32;
using PCMon.Core;

namespace PCMon.Services;

/// <summary>
/// Security posture from local sources only: firewall registry, Security Center, Defender (WMI)
/// and the Security event log. Slow-ish (~1 s), so the sampler runs it every couple of minutes.
/// </summary>
static class SecurityService
{
    public static SecurityPosture Collect()
    {
        string? logError = null;
        IReadOnlyList<FailedSignIn> signIns = [];
        try { signIns = FailedSignIns(TimeSpan.FromDays(1)); }
        catch (Exception ex)
        {
            logError = ex.Message;
            Log.Error("security log", ex);
        }
        return new SecurityPosture(Firewall(), Antivirus(), Detections(), signIns, logError);
    }

    static FirewallStatus Firewall()
    {
        bool? Read(string profile, string policyProfile)
        {
            try
            {
                // Group Policy wins over the local setting when present.
                using var policy = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Policies\Microsoft\WindowsFirewall\{policyProfile}");
                if (policy?.GetValue("EnableFirewall") is int p) return p != 0;
                using var local = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\{profile}");
                return local?.GetValue("EnableFirewall") is int v ? v != 0 : null;
            }
            catch (Exception ex)
            {
                Log.Error("firewall " + profile, ex);
                return null;
            }
        }

        return new FirewallStatus(Read("DomainProfile", "DomainProfile"), Read("StandardProfile", "PrivateProfile"),
            Read("PublicProfile", "PublicProfile"));
    }

    static IReadOnlyList<AntivirusStatus> Antivirus()
    {
        var list = new List<AntivirusStatus>();
        bool? defenderRealTime = null;
        try
        {
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Defender",
                "SELECT RealTimeProtectionEnabled FROM MSFT_MpComputerStatus");
            foreach (ManagementObject o in s.Get())
                defenderRealTime = o["RealTimeProtectionEnabled"] as bool?;
        }
        catch (Exception ex)
        {
            Log.Error("defender status", ex);
        }

        try
        {
            using var s = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct");
            foreach (ManagementObject o in s.Get())
            {
                var name = o["displayName"]?.ToString() ?? "Antivirus";
                var (enabled, upToDate) = SecurityCenter.Decode(Convert.ToUInt32(o["productState"] ?? 0u));
                bool? realTime = name.Contains("Defender", StringComparison.OrdinalIgnoreCase) ? defenderRealTime : null;
                list.Add(new AntivirusStatus(name, enabled, upToDate, realTime));
            }
        }
        catch (Exception ex)
        {
            Log.Error("security center", ex);
        }
        return list;
    }

    static IReadOnlyList<ThreatDetection> Detections()
    {
        var list = new List<ThreatDetection>();
        try
        {
            const string ns = @"root\Microsoft\Windows\Defender";
            var names = new Dictionary<long, (string Name, int Severity)>();
            using (var threats = new ManagementObjectSearcher(ns, "SELECT ThreatID, ThreatName, SeverityID FROM MSFT_MpThreat"))
                foreach (ManagementObject t in threats.Get())
                    names[Convert.ToInt64(t["ThreatID"])] = (t["ThreatName"]?.ToString() ?? "?", Convert.ToInt32(t["SeverityID"] ?? 0));

            using var detections = new ManagementObjectSearcher(ns,
                "SELECT ThreatID, InitialDetectionTime, ThreatStatusID, ActionSuccess, Resources FROM MSFT_MpThreatDetection");
            foreach (ManagementObject d in detections.Get())
            {
                long id = Convert.ToInt64(d["ThreatID"]);
                var when = d["InitialDetectionTime"] is string dmtf ? ManagementDateTimeConverter.ToDateTime(dmtf) : DateTime.MinValue;
                var resource = (d["Resources"] as string[])?.FirstOrDefault() ?? "";
                var (name, severity) = names.TryGetValue(id, out var n) ? n : ($"Threat {id}", 0);
                list.Add(new ThreatDetection(when, name, severity, Convert.ToInt32(d["ThreatStatusID"] ?? 0),
                    d["ActionSuccess"] as bool? ?? false, resource));
            }
        }
        catch (Exception ex)
        {
            Log.Error("defender detections", ex); // no Defender (third-party AV) is fine
        }
        return list.OrderByDescending(d => d.When).ToList();
    }

    /// <summary>Event 4625 (failed logon) from the Security log. Needs admin; capped at 2,000 events.</summary>
    static IReadOnlyList<FailedSignIn> FailedSignIns(TimeSpan window)
    {
        var list = new List<FailedSignIn>();
        var query = new EventLogQuery("Security", PathType.LogName,
            $"*[System[(EventID=4625) and TimeCreated[timediff(@SystemTime) <= {(long)window.TotalMilliseconds}]]]")
        {
            ReverseDirection = true,
        };
        using var reader = new EventLogReader(query);
        for (int i = 0; i < 2000 && reader.ReadEvent() is { } e; i++)
        {
            using (e)
            {
                var p = e.Properties;
                string Get(int index) => index < p.Count ? p[index].Value?.ToString() ?? "" : "";
                int.TryParse(Get(10), out var logonType);
                list.Add(new FailedSignIn(e.TimeCreated ?? DateTime.MinValue, Get(5), logonType, Get(19),
                    Path.GetFileName(Get(18))));
            }
        }
        return list;
    }
}

public static class SecurityCenter
{
    /// <summary>
    /// Security Center's productState packs flags into hex nibbles: 0x0n1000 = enabled, 0x0000n0 = signatures out of date.
    /// e.g. Defender on and current = 0x061100.
    /// </summary>
    public static (bool Enabled, bool UpToDate) Decode(uint productState) =>
        (((productState >> 12) & 0xF) == 1, ((productState >> 4) & 0xF) == 0);
}
