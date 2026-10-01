using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace PCMon.Core;

public enum Direction { Listening, Incoming, Outgoing }

/// <summary>One row of the TCP table (IPv4 or IPv6), with ports already in host order.</summary>
public sealed record TcpRow(string Protocol, IPAddress LocalAddress, int LocalPort, IPAddress RemoteAddress, int RemotePort, string State, int Pid)
{
    public string Local => Endpoint(LocalAddress, LocalPort);
    public string Remote => Endpoint(RemoteAddress, RemotePort);

    static string Endpoint(IPAddress a, int port) =>
        a.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{a}]:{port}" : $"{a}:{port}";
}

public sealed record ConnectionInfo(TcpRow Row, Direction Direction, string RemoteScope);

/// <summary>Incoming vs outgoing, and where the other end lives (SRS FR-11).</summary>
public static class ConnectionClassifier
{
    /// <summary>
    /// A connection is incoming when its local port is one this machine listens on
    /// (on that address or on all addresses). Everything else was opened from here.
    /// </summary>
    public static IReadOnlyList<ConnectionInfo> Classify(IReadOnlyList<TcpRow> rows)
    {
        var listening = new Dictionary<(string, int), List<IPAddress>>();
        foreach (var r in rows.Where(r => r.State == "Listen"))
        {
            if (!listening.TryGetValue((r.Protocol, r.LocalPort), out var addrs))
                listening[(r.Protocol, r.LocalPort)] = addrs = [];
            addrs.Add(r.LocalAddress);
        }

        bool IsServerSide(TcpRow r) =>
            listening.TryGetValue((r.Protocol, r.LocalPort), out var addrs) &&
            addrs.Any(a => a.Equals(r.LocalAddress) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any));

        return rows.Select(r => new ConnectionInfo(r,
                r.State == "Listen" ? Direction.Listening : IsServerSide(r) ? Direction.Incoming : Direction.Outgoing,
                Scope(r.RemoteAddress)))
            .ToList();
    }

    /// <summary>"Loopback", "LAN" (private ranges) or "Internet". Offline: no lookups, no traffic.</summary>
    public static string Scope(IPAddress a)
    {
        if (a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any)) return "—";
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a)) return "Loopback";

        var b = a.GetAddressBytes();
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            bool isPrivate = b[0] == 10 ||
                             (b[0] == 172 && b[1] is >= 16 and <= 31) ||
                             (b[0] == 192 && b[1] == 168) ||
                             (b[0] == 169 && b[1] == 254) ||
                             (b[0] == 100 && b[1] is >= 64 and <= 127); // carrier-grade NAT
            return isPrivate ? "LAN" : "Internet";
        }

        bool isLocal6 = a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || (b[0] & 0xFE) == 0xFC;
        return isLocal6 ? "LAN" : "Internet";
    }
}

public sealed record VpnStatus(bool Installed, bool Active, string? Adapter, bool? FullTunnel)
{
    public static VpnStatus None { get; } = new(false, false, null, null);
}

public static partial class VpnRules
{
    [GeneratedRegex(@"vpn|wireguard|wintun|nordlynx|\btap\b|tap-|openvpn|anyconnect|forti|globalprotect|pangp|juniper|pulse secure|zscaler|proton|expressvpn|surfshark|mullvad|windscribe|\bwarp\b|tailscale|zerotier|hamachi|softether|\bsstp\b|l2tp|ikev2", RegexOptions.IgnoreCase)]
    private static partial Regex VpnPattern();

    [GeneratedRegex(@"virtualbox|vmware|hyper-v|vethernet|loopback", RegexOptions.IgnoreCase)]
    private static partial Regex NotVpnPattern();

    /// <summary>Windows dial-up VPNs are PPP adapters; everything else is recognised by driver name.</summary>
    public static bool IsVpnAdapter(string name, string description, NetworkInterfaceType type)
    {
        if (NotVpnPattern().IsMatch(name) || NotVpnPattern().IsMatch(description)) return false;
        return type == NetworkInterfaceType.Ppp || VpnPattern().IsMatch(name) || VpnPattern().IsMatch(description);
    }
}

// ---- Security posture (collected every couple of minutes by SecurityService) ----

public sealed record FirewallStatus(bool? Domain, bool? Private, bool? Public);

public sealed record AntivirusStatus(string Name, bool Enabled, bool? UpToDate, bool? RealTime);

public sealed record ThreatDetection(DateTime When, string Name, int Severity, int StatusId, bool ActionSuccess, string Resource)
{
    /// <summary>Defender's ThreatStatusID: cleaned, quarantined, removed, allowed or blocked all count as dealt with.</summary>
    public bool Handled => ActionSuccess && StatusId is 2 or 3 or 4 or 5 or 6;

    public string StatusText => StatusId switch
    {
        1 => "detected",
        2 => "cleaned",
        3 => "quarantined",
        4 => "removed",
        5 => "allowed",
        6 => "blocked",
        102 => "quarantine failed",
        103 => "remove failed",
        104 => "allow failed",
        105 => "abandoned",
        107 => "block failed",
        _ => $"status {StatusId}",
    };
}

public sealed record FailedSignIn(DateTime When, string User, int LogonType, string Ip, string Process)
{
    /// <summary>Network (3), cleartext network (8) or RDP (10) from another machine: what brute-force looks like.</summary>
    public bool IsRemote => LogonType is 3 or 8 or 10 && !string.IsNullOrWhiteSpace(Ip) && Ip != "-" &&
                            !(IPAddress.TryParse(Ip, out var a) && IPAddress.IsLoopback(a));
}

public sealed record SecurityPosture(
    FirewallStatus Firewall,
    IReadOnlyList<AntivirusStatus> Antivirus,
    IReadOnlyList<ThreatDetection> Detections,
    IReadOnlyList<FailedSignIn> FailedSignIns,
    string? SignInLogError);

public sealed record SecurityFinding(Level Level, string Title, string Detail, DateTime? When);

/// <summary>Turns posture data into findings. "Level.Ok" findings are informational.</summary>
public static class SecurityAnalysis
{
    public static IReadOnlyList<SecurityFinding> Posture(SecurityPosture p, DateTime now)
    {
        var findings = new List<SecurityFinding>();

        var off = new List<string>();
        if (p.Firewall.Domain == false) off.Add("Domain");
        if (p.Firewall.Private == false) off.Add("Private");
        if (p.Firewall.Public == false) off.Add("Public");
        if (off.Count > 0)
            findings.Add(new(Level.Warn, $"Firewall is off for {string.Join(", ", off)} networks",
                "Windows Defender Firewall protects against unsolicited inbound connections.", null));

        if (p.Antivirus.Count > 0 && !p.Antivirus.Any(a => a.Enabled))
            findings.Add(new(Level.Critical, "No antivirus is active",
                string.Join(", ", p.Antivirus.Select(a => a.Name)) + " reported as off.", null));
        foreach (var av in p.Antivirus.Where(a => a.Enabled && a.RealTime == false))
            findings.Add(new(Level.Warn, $"{av.Name}: real-time protection is off", "Files aren't scanned as they're opened.", null));
        foreach (var av in p.Antivirus.Where(a => a.Enabled && a.UpToDate == false))
            findings.Add(new(Level.Warn, $"{av.Name}: definitions are out of date", "New threats may go unrecognised.", null));

        foreach (var d in p.Detections.Where(d => now - d.When <= TimeSpan.FromDays(7)))
        {
            findings.Add(d.Handled
                ? new(Level.Warn, $"Threat {d.StatusText}: {d.Name}", Trim(d.Resource), d.When)
                : new(Level.Critical, $"Threat NOT removed: {d.Name} ({d.StatusText})", Trim(d.Resource), d.When));
        }

        var recent = p.FailedSignIns.Where(f => now - f.When <= TimeSpan.FromHours(24)).ToList();
        foreach (var g in recent.Where(f => f.IsRemote).GroupBy(f => f.Ip))
        {
            var users = string.Join(", ", g.Select(f => f.User).Distinct().Take(3));
            findings.Add(g.Count() >= 10
                ? new(Level.Critical, $"Possible brute-force: {g.Count()} failed sign-ins from {g.Key}", $"Accounts tried: {users}", g.Max(f => f.When))
                : new(Level.Warn, $"{g.Count()} failed remote sign-in(s) from {g.Key}", $"Accounts tried: {users}", g.Max(f => f.When)));
        }

        var local = recent.Where(f => !f.IsRemote).ToList();
        if (local.Count > 0)
        {
            var apps = string.Join(", ", local.Select(f => f.Process).Where(s => s != "-" && s != "").Distinct().Take(3));
            findings.Add(new(Level.Ok, $"{local.Count} wrong-password prompt(s) on this PC in 24 h",
                $"Local, not remote{(apps.Length > 0 ? $" (from {apps})" : "")}. Usually a typo, not an attack.",
                local.Max(f => f.When)));
        }

        return findings;
    }

    static string Trim(string resource)
    {
        var s = resource.StartsWith("file:_") ? resource[6..] : resource.StartsWith("containerfile:_") ? resource[15..] : resource;
        return s.Length > 140 ? "…" + s[^139..] : s;
    }
}

/// <summary>
/// Live heuristics over successive TCP tables: half-open floods, port scans, hammering on
/// remote-access ports, and risky services listening on every network. "Possible", never "definite".
/// </summary>
public sealed class AttackWatch
{
    static readonly Dictionary<int, string> SensitivePorts = new()
    {
        [21] = "FTP", [22] = "SSH", [23] = "Telnet", [135] = "RPC", [139] = "NetBIOS",
        [445] = "SMB", [3389] = "RDP", [5900] = "VNC", [5985] = "WinRM",
    };

    readonly Dictionary<string, List<(int Port, DateTime When)>> _inbound = [];
    readonly HashSet<string> _seen = [];
    static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    public IReadOnlyList<SecurityFinding> Analyze(IReadOnlyList<ConnectionInfo> connections, DateTime now)
    {
        var findings = new List<SecurityFinding>();

        // Half-open (SYN received) connections piling up = SYN flood or a scan in progress.
        var halfOpen = connections.Where(c => c.Row.State == "SynReceived").ToList();
        foreach (var g in halfOpen.GroupBy(c => c.Row.RemoteAddress.ToString()).Where(g => g.Count() >= 20))
            findings.Add(new(Level.Critical, $"Possible SYN flood from {g.Key}", $"{g.Count()} half-open connections right now.", now));
        if (halfOpen.Count >= 100 && findings.Count == 0)
            findings.Add(new(Level.Critical, "Possible SYN flood", $"{halfOpen.Count} half-open connections from many addresses.", now));

        // Remember each new inbound connection from another machine for 60 s.
        foreach (var c in connections.Where(c => c.Direction == Direction.Incoming && c.RemoteScope is "LAN" or "Internet"))
        {
            if (!_seen.Add($"{c.Row.Protocol}|{c.Row.Local}|{c.Row.Remote}")) continue;
            var ip = c.Row.RemoteAddress.ToString();
            if (!_inbound.TryGetValue(ip, out var list)) _inbound[ip] = list = [];
            list.Add((c.Row.LocalPort, now));
        }
        foreach (var ip in _inbound.Keys.ToList())
        {
            _inbound[ip].RemoveAll(e => now - e.When > Window);
            if (_inbound[ip].Count == 0) _inbound.Remove(ip);
        }
        if (_seen.Count > 20_000) _seen.Clear(); // bounded memory on very busy servers

        foreach (var (ip, events) in _inbound)
        {
            int ports = events.Select(e => e.Port).Distinct().Count();
            if (ports >= 10)
            {
                findings.Add(new(Level.Critical, $"Possible port scan from {ip}", $"Touched {ports} different local ports in the last minute.", now));
                continue;
            }
            foreach (var g in events.GroupBy(e => e.Port).Where(g => SensitivePorts.ContainsKey(g.Key) && g.Count() >= 15))
                findings.Add(new(Level.Warn, $"Repeated {SensitivePorts[g.Key]} connections from {ip}",
                    $"{g.Count()} new connections to port {g.Key} in the last minute. Could be someone guessing passwords.", now));
        }

        // Informational: who is connected in from the internet, and what's exposed.
        var fromInternet = connections.Where(c => c.Direction == Direction.Incoming && c.RemoteScope == "Internet" && c.Row.State == "Established").ToList();
        if (fromInternet.Count > 0)
            findings.Add(new(Level.Ok, $"{fromInternet.Count} incoming connection(s) from the internet",
                "To local ports " + string.Join(", ", fromInternet.Select(c => c.Row.LocalPort).Distinct().Take(8)), now));

        var exposed = connections
            .Where(c => c.Direction == Direction.Listening && SensitivePorts.ContainsKey(c.Row.LocalPort) &&
                        (c.Row.LocalAddress.Equals(IPAddress.Any) || c.Row.LocalAddress.Equals(IPAddress.IPv6Any)))
            .Select(c => c.Row.LocalPort).Distinct().OrderBy(p => p).ToList();
        if (exposed.Count > 0)
            findings.Add(new(Level.Ok, "Listening on all networks: " + string.Join(", ", exposed.Select(p => $"{SensitivePorts[p]} ({p})")),
                "Normal for Windows file sharing and RPC. The firewall decides who can actually reach them.", null));

        return findings;
    }
}
