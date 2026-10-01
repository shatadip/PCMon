using System.Net;
using System.Net.NetworkInformation;
using PCMon.Core;
using PCMon.Services;
using Xunit;

namespace PCMon.Tests;

public class ConnectionClassifierTests
{
    static TcpRow Row(string local, int lport, string remote, int rport, string state, string proto = "TCP", int pid = 100) =>
        new(proto, IPAddress.Parse(local), lport, IPAddress.Parse(remote), rport, state, pid);

    [Fact]
    public void ServerSideConnectionsAreIncomingEverythingElseOutgoing()
    {
        var rows = new[]
        {
            Row("0.0.0.0", 3389, "0.0.0.0", 0, "Listen"),
            Row("192.168.1.5", 3389, "203.0.113.9", 51000, "Established"), // someone RDP'd in
            Row("192.168.1.5", 52000, "142.250.1.1", 443, "Established"),  // browser going out
        };
        var c = ConnectionClassifier.Classify(rows);
        Assert.Equal([Direction.Listening, Direction.Incoming, Direction.Outgoing], c.Select(x => x.Direction));
        Assert.Equal("Internet", c[1].RemoteScope);
    }

    [Fact]
    public void ListenerOnSpecificAddressOnlyMatchesThatAddress()
    {
        var rows = new[]
        {
            Row("127.0.0.1", 8080, "0.0.0.0", 0, "Listen"),
            Row("192.168.1.5", 8080, "192.168.1.9", 40000, "Established"),
        };
        Assert.Equal(Direction.Outgoing, ConnectionClassifier.Classify(rows)[1].Direction);
    }

    [Fact]
    public void Ipv6ListenersDoNotMakeIpv4ConnectionsIncoming()
    {
        var rows = new[]
        {
            Row("::", 445, "::", 0, "Listen", "TCPv6"),
            Row("192.168.1.5", 445, "192.168.1.9", 40000, "Established"),
            Row("fe80::1", 445, "fe80::2", 40001, "Established", "TCPv6"),
        };
        var c = ConnectionClassifier.Classify(rows);
        Assert.Equal(Direction.Outgoing, c[1].Direction);
        Assert.Equal(Direction.Incoming, c[2].Direction);
    }

    [Theory]
    [InlineData("127.0.0.1", "Loopback")]
    [InlineData("::1", "Loopback")]
    [InlineData("10.1.2.3", "LAN")]
    [InlineData("172.20.0.1", "LAN")]
    [InlineData("172.32.0.1", "Internet")]
    [InlineData("192.168.29.1", "LAN")]
    [InlineData("169.254.10.10", "LAN")]
    [InlineData("100.100.1.1", "LAN")]
    [InlineData("fe80::1", "LAN")]
    [InlineData("fd12:3456::1", "LAN")]
    [InlineData("8.8.8.8", "Internet")]
    [InlineData("2606:4700::1111", "Internet")]
    [InlineData("::ffff:192.168.1.1", "LAN")]
    [InlineData("0.0.0.0", "—")]
    public void Scope(string address, string expected) => Assert.Equal(expected, ConnectionClassifier.Scope(IPAddress.Parse(address)));

    [Fact]
    public void Ipv6EndpointsAreBracketed()
    {
        var r = Row("::1", 443, "fe80::2", 50000, "Established", "TCPv6");
        Assert.Equal("[::1]:443", r.Local);
    }
}

public class VpnRuleTests
{
    [Theory]
    [InlineData("NordLynx", "NordLynx Tunnel", (NetworkInterfaceType)53, true)]
    [InlineData("Local Area Connection", "TAP-NordVPN Windows Adapter V9", (NetworkInterfaceType)53, true)]
    [InlineData("OpenVPN Data Channel Offload for NordVPN", "OpenVPN Data Channel Offload", (NetworkInterfaceType)53, true)]
    [InlineData("wg0", "WireGuard Tunnel", NetworkInterfaceType.Unknown, true)]
    [InlineData("Tailscale", "Tailscale Tunnel", NetworkInterfaceType.Unknown, true)]
    [InlineData("Work VPN", "WAN Miniport (IKEv2)", NetworkInterfaceType.Ppp, true)]
    [InlineData("Ethernet", "Realtek PCIe GbE Family Controller", NetworkInterfaceType.Ethernet, false)]
    [InlineData("Ethernet 2", "VirtualBox Host-Only Ethernet Adapter", NetworkInterfaceType.Ethernet, false)]
    [InlineData("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet, false)]
    [InlineData("Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz", NetworkInterfaceType.Wireless80211, false)]
    public void Detects(string name, string description, NetworkInterfaceType type, bool vpn) =>
        Assert.Equal(vpn, VpnRules.IsVpnAdapter(name, description, type));
}

public class SecurityAnalysisTests
{
    static readonly DateTime Now = new(2026, 10, 2, 12, 0, 0);
    static readonly FirewallStatus AllOn = new(true, true, true);
    static readonly AntivirusStatus Defender = new("Windows Defender", true, true, true);

    static SecurityPosture P(FirewallStatus? fw = null, AntivirusStatus[]? av = null, ThreatDetection[]? det = null, FailedSignIn[]? fails = null) =>
        new(fw ?? AllOn, av ?? [Defender], det ?? [], fails ?? [], null);

    [Fact]
    public void HealthyPostureHasNoFindings() => Assert.Empty(SecurityAnalysis.Posture(P(), Now));

    [Fact]
    public void FirewallOffIsWarned()
    {
        var f = Assert.Single(SecurityAnalysis.Posture(P(fw: new(true, true, false)), Now));
        Assert.Equal(Level.Warn, f.Level);
        Assert.Contains("Public", f.Title);
    }

    [Fact]
    public void NoActiveAntivirusIsCritical()
    {
        var f = SecurityAnalysis.Posture(P(av: [new("Windows Defender", false, true, false)]), Now);
        Assert.Contains(f, x => x.Level == Level.Critical && x.Title == "No antivirus is active");
    }

    [Fact]
    public void QuarantinedThreatThisWeekIsWarnOlderIsIgnored()
    {
        var f = SecurityAnalysis.Posture(P(det:
        [
            new(Now.AddDays(-5), "Trojan:Win32/FakeExt!AMTB", 5, 3, true, @"file:_X:\Games\setup.exe"),
            new(Now.AddDays(-30), "PUA:Win32/Presenoker", 1, 4, true, "file:_old.exe"),
        ]), Now);
        var only = Assert.Single(f);
        Assert.Equal(Level.Warn, only.Level);
        Assert.Equal("Threat quarantined: Trojan:Win32/FakeExt!AMTB", only.Title);
        Assert.Equal(@"X:\Games\setup.exe", only.Detail);
    }

    [Fact]
    public void ThreatThatWasNotRemovedIsCritical()
    {
        var f = Assert.Single(SecurityAnalysis.Posture(P(det: [new(Now.AddHours(-1), "Trojan:X", 5, 103, false, "file:_a.exe")]), Now));
        Assert.Equal(Level.Critical, f.Level);
        Assert.Contains("NOT removed", f.Title);
    }

    [Fact]
    public void RemoteBruteForceIsCriticalLocalTyposAreInformational()
    {
        var fails = Enumerable.Range(0, 12).Select(i => new FailedSignIn(Now.AddMinutes(-i), "administrator", 10, "203.0.113.50", "-"))
            .Append(new FailedSignIn(Now.AddMinutes(-3), "Admin", 2, "-", "chrome.exe"))
            .ToArray();
        var f = SecurityAnalysis.Posture(P(fails: fails), Now);
        Assert.Contains(f, x => x.Level == Level.Critical && x.Title == "Possible brute-force: 12 failed sign-ins from 203.0.113.50");
        Assert.Contains(f, x => x.Level == Level.Ok && x.Title.StartsWith("1 wrong-password prompt") && x.Detail.Contains("chrome.exe"));
    }

    [Fact]
    public void AFewRemoteFailuresAreAWarning()
    {
        var fails = new[] { new FailedSignIn(Now.AddMinutes(-1), "bob", 3, "192.168.1.20", "-") };
        Assert.Equal(Level.Warn, Assert.Single(SecurityAnalysis.Posture(P(fails: fails), Now)).Level);
    }

    [Theory]
    [InlineData(3, "10.0.0.5", true)]
    [InlineData(10, "203.0.113.1", true)]
    [InlineData(2, "-", false)]
    [InlineData(3, "127.0.0.1", false)]
    [InlineData(3, "-", false)]
    [InlineData(7, "10.0.0.5", false)]
    public void RemoteSignIn(int type, string ip, bool remote) =>
        Assert.Equal(remote, new FailedSignIn(Now, "u", type, ip, "").IsRemote);

    [Theory]
    [InlineData(0x061100u, true, true)]   // Defender on, current
    [InlineData(0x060100u, false, true)]  // off
    [InlineData(0x061110u, true, false)]  // on, out of date
    [InlineData(0x041000u, true, true)]
    public void SecurityCenterState(uint state, bool enabled, bool upToDate) =>
        Assert.Equal((enabled, upToDate), SecurityCenter.Decode(state));
}

public class AttackWatchTests
{
    static readonly DateTime T0 = new(2026, 10, 2, 12, 0, 0);

    static ConnectionInfo In(string remote, int localPort, int remotePort, string state = "Established") =>
        new(new TcpRow("TCP", IPAddress.Parse("192.168.1.5"), localPort, IPAddress.Parse(remote), remotePort, state, 4),
            Direction.Incoming, ConnectionClassifier.Scope(IPAddress.Parse(remote)));

    [Fact]
    public void QuietTableHasNoAlerts()
    {
        var w = new AttackWatch();
        Assert.DoesNotContain(w.Analyze([In("192.168.1.9", 445, 50000)], T0), f => f.Level >= Level.Warn);
    }

    [Fact]
    public void ManyHalfOpenFromOneAddressIsSynFlood()
    {
        var rows = Enumerable.Range(0, 25).Select(i => In("198.51.100.7", 80, 40000 + i, "SynReceived")).ToList();
        var f = new AttackWatch().Analyze(rows, T0);
        Assert.Contains(f, x => x.Level == Level.Critical && x.Title == "Possible SYN flood from 198.51.100.7");
    }

    [Fact]
    public void ManyDistinctPortsFromOneAddressIsPortScan()
    {
        var w = new AttackWatch();
        // the scan arrives across several samples
        for (int s = 0; s < 4; s++)
            w.Analyze(Enumerable.Range(0, 3).Select(i => In("198.51.100.8", 1000 + s * 3 + i, 60000 + s * 3 + i)).ToList(), T0.AddSeconds(s * 2));
        var f = w.Analyze([], T0.AddSeconds(10));
        Assert.Contains(f, x => x.Level == Level.Critical && x.Title == "Possible port scan from 198.51.100.8");
    }

    [Fact]
    public void ScanEvidenceExpiresAfterAMinute()
    {
        var w = new AttackWatch();
        w.Analyze(Enumerable.Range(0, 12).Select(i => In("198.51.100.8", 1000 + i, 60000 + i)).ToList(), T0);
        Assert.DoesNotContain(w.Analyze([], T0.AddSeconds(90)), x => x.Title.Contains("port scan"));
    }

    [Fact]
    public void HammeringRdpIsAWarning()
    {
        var rows = Enumerable.Range(0, 16).Select(i => In("203.0.113.77", 3389, 50000 + i)).ToList();
        var f = new AttackWatch().Analyze(rows, T0);
        Assert.Contains(f, x => x.Level == Level.Warn && x.Title == "Repeated RDP connections from 203.0.113.77");
    }

    [Fact]
    public void SameConnectionSeenTwiceCountsOnce()
    {
        var w = new AttackWatch();
        var rows = Enumerable.Range(0, 8).Select(i => In("203.0.113.77", 3389, 50000 + i)).ToList();
        w.Analyze(rows, T0);
        Assert.DoesNotContain(w.Analyze(rows, T0.AddSeconds(2)), x => x.Level >= Level.Warn);
    }

    [Fact]
    public void ExposedServicesAreInformational()
    {
        var listen = new ConnectionInfo(new TcpRow("TCP", IPAddress.Any, 3389, IPAddress.Any, 0, "Listen", 1000), Direction.Listening, "—");
        var f = Assert.Single(new AttackWatch().Analyze([listen], T0));
        Assert.Equal(Level.Ok, f.Level);
        Assert.Contains("RDP (3389)", f.Title);
    }
}

public class SecurityStatusMessageTests
{
    [Fact]
    public void CriticalSecurityBeatsEverything() =>
        Assert.Equal(Strings.SecurityCritical("Possible port scan from 1.2.3.4"),
            StatusMessenger.Pick(new(Level.Critical, Level.Ok, Level.Critical, Level.Ok, 0, Level.Critical, "Possible port scan from 1.2.3.4", 1)));

    [Fact]
    public void SecurityWarningAfterTemperatures()
    {
        Assert.Equal(Strings.Warming, StatusMessenger.Pick(new(Level.Warn, Level.Ok, Level.Ok, Level.Ok, 0, Level.Warn, "x", 3)));
        Assert.Equal(Strings.SecurityWarn(3), StatusMessenger.Pick(new(Level.Ok, Level.Ok, Level.Warn, Level.Warn, 0, Level.Warn, "x", 3)));
    }
}
