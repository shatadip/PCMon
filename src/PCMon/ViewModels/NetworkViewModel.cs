using System.Collections.ObjectModel;
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

public sealed class ConnectionRow(string key, int pid, string process, string local, string remote) : ObservableObject
{
    public string Key { get; } = key;
    public int Pid { get; } = pid;
    public string Process { get; } = process;
    public string Local { get; } = local;
    public string Remote { get; } = remote;

    string _state = "";
    public string State { get => _state; set => SetProperty(ref _state, value); }
}

/// <summary>Per-adapter speeds, a 2-minute chart and the TCP table (SRS FR-9..11).</summary>
public sealed class NetworkViewModel(Func<IReadOnlyDictionary<int, string>> processNames)
    : PageViewModel("Network", "")
{
    readonly Dictionary<string, ConnectionRow> _connections = [];
    int _tick;

    public ObservableCollection<AdapterRow> Adapters { get; } = [];
    public ObservableCollection<ConnectionRow> Connections { get; } = [];

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

    bool _hideListening = true;
    public bool HideListening
    {
        get => _hideListening;
        set
        {
            if (SetProperty(ref _hideListening, value)) RefreshConnections();
        }
    }

    string _connectionSummary = "";
    public string ConnectionSummary { get => _connectionSummary; private set => SetProperty(ref _connectionSummary, value); }

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

        if (IsActive && _tick++ % 2 == 0) RefreshConnections();
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
        ChartCaption = $"{a.Name} · last 2 minutes · peak ↓ {Format.Rate(a.Down.Max())}  ↑ {Format.Rate(a.Up.Max())}";
    }

    void RefreshConnections()
    {
        IReadOnlyList<TcpConnection> rows;
        try
        {
            rows = NetworkService.TcpConnections();
        }
        catch (Exception ex)
        {
            Log.Error("tcp table", ex);
            return;
        }

        var names = processNames();
        var live = new HashSet<string>();
        foreach (var c in rows)
        {
            if (HideListening && c.State == "Listen") continue;
            var key = $"{c.Pid}|{c.Local}|{c.Remote}";
            live.Add(key);
            if (!_connections.TryGetValue(key, out var row))
            {
                row = new ConnectionRow(key, c.Pid, names.TryGetValue(c.Pid, out var n) ? n : c.Pid == 0 ? "System Idle" : "?",
                    c.Local, c.Remote);
                _connections[key] = row;
                Connections.Add(row);
            }
            row.State = c.State;
        }

        foreach (var gone in Connections.Where(r => !live.Contains(r.Key)).ToList())
        {
            Connections.Remove(gone);
            _connections.Remove(gone.Key);
        }

        var established = Connections.Count(r => r.State == "Established");
        ConnectionSummary = $"{Connections.Count} connections · {established} established";
    }
}
