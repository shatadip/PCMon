using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using PCMon.Core;
using PCMon.Services;

namespace PCMon.ViewModels;

public sealed class AdapterRow(string id) : ObservableObject
{
    public string Id { get; } = id;
    public RollingBuffer Down { get; } = new(120);
    public RollingBuffer Up { get; } = new(120);

    string _name = "", _kind = "", _ipv4 = "", _description = "";
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Description { get => _description; set => SetProperty(ref _description, value); }
    public string Kind { get => _kind; set => SetProperty(ref _kind, value); }
    public string Ipv4 { get => _ipv4; set => SetProperty(ref _ipv4, value); }

    string _downText = "", _upText = "", _totalDownText = "", _totalUpText = "";
    public string DownText { get => _downText; set => SetProperty(ref _downText, value); }
    public string UpText { get => _upText; set => SetProperty(ref _upText, value); }
    public string TotalDownText { get => _totalDownText; set => SetProperty(ref _totalDownText, value); }
    public string TotalUpText { get => _totalUpText; set => SetProperty(ref _totalUpText, value); }
}

public sealed class ConnectionRow(string key, ConnectionInfo c, string process) : ObservableObject
{
    public string Key { get; } = key;
    public Direction Direction { get; } = c.Direction;
    public string DirectionText { get; } = c.Direction switch
    {
        Direction.Incoming => "↓ In",
        Direction.Outgoing => "↑ Out",
        _ => "◉ Listen",
    };
    public string Process { get; } = process;
    public int Pid { get; } = c.Row.Pid;
    public string Protocol { get; } = c.Row.Protocol;
    public string LocalAddress { get; } = c.Row.LocalAddress.ToString();
    public int LocalPort { get; } = c.Row.LocalPort;
    public string RemoteAddress { get; } = c.Row.RemoteAddress.ToString();
    public int RemotePort { get; } = c.Row.RemotePort;
    public string Scope { get; } = c.RemoteScope;

    string _state = c.Row.State;
    public string State { get => _state; set => SetProperty(ref _state, value); }
}

public sealed class FindingRow(SecurityFinding f)
{
    public Level Level { get; } = f.Level;
    public string Glyph { get; } = f.Level switch { Level.Critical => "", Level.Warn => "", _ => "" };
    public string Title { get; } = f.Title;
    public string Detail { get; } = f.Detail;
    public string WhenText { get; } = f.When is { } w ? w.ToString("yyyy-MM-dd HH:mm") : "";
}

/// <summary>Status chip shown above every Network tab: VPN, firewall, antivirus, attack watch.</summary>
public sealed class Badge(string glyph) : ObservableObject
{
    public string Glyph { get; } = glyph;

    string _text = Strings.ChecksPending;
    public string Text { get => _text; set => SetProperty(ref _text, value); }

    string? _tooltip;
    public string? Tooltip { get => _tooltip; set => SetProperty(ref _tooltip, value); }

    Level _level = Level.Unknown;
    public Level Level { get => _level; set => SetProperty(ref _level, value); }
}

/// <summary>Traffic, connections (in/out, local/remote) and security (SRS FR-9..11, plus VPN and attack watch).</summary>
public sealed class NetworkViewModel : PageViewModel
{
    readonly Func<IReadOnlyDictionary<int, string>> _processNames;
    readonly Dictionary<string, ConnectionRow> _connections = [];
    IReadOnlyList<ConnectionInfo> _latestConnections = [];
    IReadOnlyList<SecurityFinding> _liveFindings = [], _postureFindings = [];
    SecurityPosture? _posture;
    string? _findingsSignature;

    public NetworkViewModel(Func<IReadOnlyDictionary<int, string>> processNames) : base("Network", "")
    {
        _processNames = processNames;
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(Connections);
        view.Filter = o => o is ConnectionRow r && Visible(r);
        view.SortDescriptions.Add(new SortDescription(nameof(ConnectionRow.Direction), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(ConnectionRow.Process), ListSortDirection.Ascending));
        ConnectionsView = view;
    }

    public ObservableCollection<AdapterRow> Adapters { get; } = [];
    public ObservableCollection<ConnectionRow> Connections { get; } = [];
    public ICollectionView ConnectionsView { get; }
    public ObservableCollection<FindingRow> Findings { get; } = [];

    public Badge VpnBadge { get; } = new("");
    public Badge FirewallBadge { get; } = new("");
    public Badge AntivirusBadge { get; } = new("");
    public Badge AttackBadge { get; } = new("");

    string _tab = "Traffic";
    public string Tab
    {
        get => _tab;
        set
        {
            if (SetProperty(ref _tab, value)) RefreshConnections();
        }
    }

    // ---- Traffic ----

    AdapterRow? _selected;
    public AdapterRow? SelectedAdapter
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value)) UpdateChart();
        }
    }

    double[] _chartDown = [], _chartUp = [];
    public double[] ChartDown { get => _chartDown; private set => SetProperty(ref _chartDown, value); }
    public double[] ChartUp { get => _chartUp; private set => SetProperty(ref _chartUp, value); }

    string _chartCaption = "";
    public string ChartCaption { get => _chartCaption; private set => SetProperty(ref _chartCaption, value); }

    // ---- Connections ----

    string _directionFilter = "All";
    public string DirectionFilter
    {
        get => _directionFilter;
        set
        {
            if (SetProperty(ref _directionFilter, value)) ConnectionsView.Refresh();
        }
    }

    bool _hideListening = true, _hideLoopback = true;
    public bool HideListening
    {
        get => _hideListening;
        set
        {
            if (SetProperty(ref _hideListening, value)) ConnectionsView.Refresh();
        }
    }

    public bool HideLoopback
    {
        get => _hideLoopback;
        set
        {
            if (SetProperty(ref _hideLoopback, value)) ConnectionsView.Refresh();
        }
    }

    string _connectionSummary = "";
    public string ConnectionSummary { get => _connectionSummary; private set => SetProperty(ref _connectionSummary, value); }

    bool Visible(ConnectionRow r) =>
        !(HideListening && r.Direction == Direction.Listening) &&
        !(HideLoopback && r.Scope == "Loopback") &&
        DirectionFilter switch
        {
            "Incoming" => r.Direction == Direction.Incoming,
            "Outgoing" => r.Direction == Direction.Outgoing,
            _ => true,
        };

    // ---- Security ----

    string _vpnDetail = Strings.ChecksPending, _firewallDetail = Strings.ChecksPending, _antivirusDetail = Strings.ChecksPending,
        _signInDetail = Strings.ChecksPending, _threatDetail = Strings.ChecksPending;
    public string VpnDetail { get => _vpnDetail; private set => SetProperty(ref _vpnDetail, value); }
    public string FirewallDetail { get => _firewallDetail; private set => SetProperty(ref _firewallDetail, value); }
    public string AntivirusDetail { get => _antivirusDetail; private set => SetProperty(ref _antivirusDetail, value); }
    public string SignInDetail { get => _signInDetail; private set => SetProperty(ref _signInDetail, value); }
    public string ThreatDetail { get => _threatDetail; private set => SetProperty(ref _threatDetail, value); }

    public bool NoAlerts => Findings.All(f => f.Level < Level.Warn);

    /// <summary>Worst alert level, its headline, and count, for the status bar and the dashboard tile.</summary>
    public Level SecurityLevel { get; private set; } = Level.Unknown;
    public string? SecurityHeadline { get; private set; }
    public int AlertCount { get; private set; }
    public VpnStatus Vpn { get; private set; } = VpnStatus.None;

    protected override void OnActivated() => RefreshConnections();

    public void Apply(Snapshot s)
    {
        var live = new HashSet<string>();
        foreach (var a in s.Adapters)
        {
            live.Add(a.Id);
            var row = Adapters.FirstOrDefault(r => r.Id == a.Id);
            if (row is null)
            {
                row = new AdapterRow(a.Id);
                Adapters.Add(row);
            }
            row.Name = a.Name;
            row.Description = a.Description;
            row.Kind = a.Kind;
            row.Ipv4 = a.Ipv4;
            row.DownText = Format.Rate(a.DownPerSec);
            row.UpText = Format.Rate(a.UpPerSec);
            row.TotalDownText = Format.Bytes(a.TotalDown);
            row.TotalUpText = Format.Bytes(a.TotalUp);
            row.Down.Add(a.DownPerSec);
            row.Up.Add(a.UpPerSec);
        }

        foreach (var gone in Adapters.Where(r => !live.Contains(r.Id)).ToList())
            Adapters.Remove(gone);

        // Default to the busiest adapter so the chart isn't a flat line on a virtual NIC.
        SelectedAdapter ??= Adapters.OrderByDescending(r => r.Down.Max() + r.Up.Max()).FirstOrDefault();
        UpdateChart();
        ApplyVpn(s.Vpn);
    }

    void ApplyVpn(VpnStatus v)
    {
        Vpn = v;
        (VpnBadge.Text, VpnBadge.Level) = v switch
        {
            { Active: true, FullTunnel: true } => ($"VPN on · {v.Adapter}", Level.Ok),
            { Active: true, FullTunnel: false } => ($"VPN on (split) · {v.Adapter}", Level.Ok),
            { Active: true } => ($"VPN on · {v.Adapter}", Level.Ok),
            { Installed: true } => ("VPN off", Level.Unknown),
            _ => ("No VPN", Level.Unknown),
        };
        VpnDetail = v switch
        {
            { Active: true, FullTunnel: true } => $"Connected through {v.Adapter}. All internet traffic goes through the VPN.",
            { Active: true, FullTunnel: false } => $"Connected through {v.Adapter}, but internet traffic still uses your normal connection (split tunnel).",
            { Active: true } => $"Connected through {v.Adapter}.",
            { Installed: true } => $"A VPN is installed ({v.Adapter}) but not connected. Traffic goes out directly.",
            _ => "No VPN adapter found. Traffic goes out directly through your ISP.",
        };
        VpnBadge.Tooltip = VpnDetail;
    }

    void UpdateChart()
    {
        if (SelectedAdapter is not { } a)
        {
            ChartDown = ChartUp = [];
            ChartCaption = "";
            return;
        }
        ChartDown = a.Down.ToArray();
        ChartUp = a.Up.ToArray();
        ChartCaption = $"{a.Name} · last 2 minutes · peak in ↓ {Format.Rate(a.Down.Max())}  out ↑ {Format.Rate(a.Up.Max())}";
    }

    public void ApplyConnections(IReadOnlyList<ConnectionInfo> connections, IReadOnlyList<SecurityFinding> liveFindings)
    {
        _latestConnections = connections;
        _liveFindings = liveFindings;
        RefreshFindings();
        RefreshConnections();
    }

    public void ApplySecurity(SecurityPosture p)
    {
        _posture = p;
        _postureFindings = SecurityAnalysis.Posture(p, DateTime.Now);

        var fw = p.Firewall;
        string On(bool? b) => b switch { true => "on", false => "OFF", null => "?" };
        FirewallDetail = $"Domain {On(fw.Domain)} · Private {On(fw.Private)} · Public {On(fw.Public)}";
        bool anyOff = fw.Domain == false || fw.Private == false || fw.Public == false;
        FirewallBadge.Text = anyOff ? "Firewall partly off" : fw.Public is null ? "Firewall ?" : "Firewall on";
        FirewallBadge.Level = anyOff ? Level.Warn : fw.Public is null ? Level.Unknown : Level.Ok;
        FirewallBadge.Tooltip = FirewallDetail;

        var active = p.Antivirus.FirstOrDefault(a => a.Enabled);
        AntivirusBadge.Text = active is null ? (p.Antivirus.Count == 0 ? "Antivirus ?" : "Antivirus OFF") : active.Name;
        AntivirusBadge.Level = active is null ? (p.Antivirus.Count == 0 ? Level.Unknown : Level.Critical)
            : active.RealTime == false || active.UpToDate == false ? Level.Warn : Level.Ok;
        AntivirusDetail = p.Antivirus.Count == 0
            ? "Security Center didn't report an antivirus."
            : string.Join("\n", p.Antivirus.Select(a =>
                $"{a.Name}: {(a.Enabled ? "on" : "off")}" +
                (a.RealTime is { } rt ? $", real-time {(rt ? "on" : "OFF")}" : "") +
                (a.UpToDate is { } u ? $", definitions {(u ? "up to date" : "OUT OF DATE")}" : "")));
        AntivirusBadge.Tooltip = AntivirusDetail;

        var now = DateTime.Now;
        int week = p.Detections.Count(d => now - d.When <= TimeSpan.FromDays(7));
        int unhandled = p.Detections.Count(d => now - d.When <= TimeSpan.FromDays(7) && !d.Handled);
        ThreatDetail = $"{week} detection(s) in the last 7 days ({unhandled} not removed) · {p.Detections.Count} in Defender's history";

        var day = p.FailedSignIns.Where(f => now - f.When <= TimeSpan.FromHours(24)).ToList();
        SignInDetail = p.SignInLogError is { } err
            ? "Couldn't read the Security log: " + err
            : $"{day.Count(f => f.IsRemote)} failed remote sign-in(s) · {day.Count(f => !f.IsRemote)} local wrong-password prompt(s) in 24 h";

        RefreshFindings();
    }

    void RefreshFindings()
    {
        var all = _postureFindings.Concat(_liveFindings)
            .OrderByDescending(f => f.Level)
            .ThenByDescending(f => f.When ?? DateTime.MinValue)
            .ToList();

        // Live findings are re-issued every 2 s; only rebuild the list when what it says changes.
        var signature = string.Join("\n", all.Select(f => $"{f.Level}|{f.Title}|{f.Detail}"));
        if (signature != _findingsSignature)
        {
            _findingsSignature = signature;
            Findings.Clear();
            foreach (var f in all) Findings.Add(new FindingRow(f));
            OnPropertyChanged(nameof(NoAlerts));
        }

        var alerts = all.Where(f => f.Level >= Level.Warn).ToList();
        AlertCount = alerts.Count;
        SecurityLevel = alerts.Count > 0 ? alerts[0].Level : _posture is null ? Level.Unknown : Level.Ok;
        SecurityHeadline = alerts.FirstOrDefault()?.Title;
        AttackBadge.Text = _posture is null && alerts.Count == 0 ? Strings.ChecksPending
            : alerts.Count == 0 ? "No signs of attack"
            : alerts.Count == 1 ? "1 alert"
            : $"{alerts.Count} alerts";
        AttackBadge.Level = SecurityLevel;
        AttackBadge.Tooltip = alerts.Count == 0 ? Strings.NoAttacks : string.Join("\n", alerts.Take(5).Select(a => "• " + a.Title));
    }

    void RefreshConnections()
    {
        if (!IsActive || Tab != "Connections") return; // the table only costs anything when it's on screen

        var names = _processNames();
        var live = new HashSet<string>();
        foreach (var c in _latestConnections)
        {
            var key = $"{c.Row.Protocol}|{c.Row.Pid}|{c.Row.Local}|{c.Row.Remote}";
            live.Add(key);
            if (!_connections.TryGetValue(key, out var row))
            {
                var process = names.TryGetValue(c.Row.Pid, out var n) ? n : c.Row.Pid == 0 ? "System Idle" : "?";
                row = new ConnectionRow(key, c, process);
                _connections[key] = row;
                Connections.Add(row);
            }
            row.State = c.Row.State;
        }

        foreach (var gone in Connections.Where(r => !live.Contains(r.Key)).ToList())
        {
            Connections.Remove(gone);
            _connections.Remove(gone.Key);
        }

        var shown = _latestConnections.Where(c => c.Direction != Direction.Listening && c.RemoteScope != "Loopback").ToList();
        ConnectionSummary = $"{shown.Count(c => c.Direction == Direction.Incoming)} incoming · " +
                            $"{shown.Count(c => c.Direction == Direction.Outgoing)} outgoing · " +
                            $"{shown.Count(c => c.RemoteScope == "Internet")} to the internet · " +
                            $"{_latestConnections.Count(c => c.Direction == Direction.Listening)} listening";
    }
}
