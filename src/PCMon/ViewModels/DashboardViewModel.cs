using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCMon.Core;
using PCMon.Services;

namespace PCMon.ViewModels;

public sealed class TileViewModel(string title, string glyph, double maximum = 100, bool twoSeries = false) : ObservableObject
{
    readonly RollingBuffer _history = new(60);
    readonly RollingBuffer? _history2 = twoSeries ? new RollingBuffer(60) : null;

    public string Title { get; } = title;
    public string Glyph { get; } = glyph;

    /// <summary>Fixed chart ceiling, or NaN to auto-scale (network).</summary>
    public double Maximum { get; } = maximum;

    string _value = "—";
    public string Value { get => _value; set => SetProperty(ref _value, value); }

    string _subtitle = "";
    public string Subtitle { get => _subtitle; set => SetProperty(ref _subtitle, value); }

    Level _level = Level.Unknown;
    public Level Level { get => _level; set => SetProperty(ref _level, value); }

    double[] _points = [];
    public double[] Points { get => _points; private set => SetProperty(ref _points, value); }

    double[]? _points2;
    public double[]? Points2 { get => _points2; private set => SetProperty(ref _points2, value); }

    public void Push(double value, double? second = null)
    {
        _history.Add(value);
        Points = _history.ToArray();
        if (_history2 is not null && second is { } s)
        {
            _history2.Add(s);
            Points2 = _history2.ToArray();
        }
    }
}

public sealed class DiskRow(string letter) : ObservableObject
{
    public string Letter { get; } = letter;

    double _usedPercent;
    public double UsedPercent { get => _usedPercent; set => SetProperty(ref _usedPercent, value); }

    string _percentText = "", _detail = "";
    public string PercentText { get => _percentText; set => SetProperty(ref _percentText, value); }
    public string Detail { get => _detail; set => SetProperty(ref _detail, value); }

    Level _level = Level.Unknown;
    public Level Level { get => _level; set => SetProperty(ref _level, value); }
}

public sealed class DashboardViewModel : PageViewModel
{
    readonly TileViewModel _cpu = new("CPU", "");
    readonly TileViewModel _gpu = new("GPU", "");
    readonly TileViewModel _ram = new("Memory", "");
    readonly TileViewModel _net = new("Network", "", double.NaN, twoSeries: true);
    readonly Dictionary<string, DiskRow> _disks = [];

    public DashboardViewModel(Action<int> openProcess) : base("Dashboard", "")
    {
        Tiles = [_cpu, _gpu, _ram, _net];
        OpenProcessCommand = new RelayCommand<int>(openProcess);
    }

    public ObservableCollection<TileViewModel> Tiles { get; }
    public ObservableCollection<DiskRow> Disks { get; } = [];
    public ObservableCollection<ProcSample> TopCpu { get; } = [];
    public ObservableCollection<ProcSample> TopRam { get; } = [];
    public IRelayCommand<int> OpenProcessCommand { get; }

    public void Apply(Snapshot s)
    {
        _cpu.Value = Format.Percent(s.CpuLoad);
        var cpuTempText = s.CpuTemp is null ? "temp —" : Format.Celsius(s.CpuTemp) + (s.CpuTempIsAcpi ? " (ACPI)" : "");
        _cpu.Subtitle = $"{cpuTempText} · {Shorten(s.CpuName) ?? "CPU"}";
        _cpu.Level = Thresholds.ForTemp(s.CpuTemp) is var l and not Level.Unknown ? l : Level.Ok;
        _cpu.Push(s.CpuLoad ?? 0);

        if (s.GpuName is null)
        {
            _gpu.Value = "N/A";
            _gpu.Subtitle = "Integrated / N/A";
            _gpu.Level = Level.Unknown;
            _gpu.Push(0);
        }
        else
        {
            _gpu.Value = Format.Percent(s.GpuLoad);
            var vram = s.VramTotalMb is > 0 && s.VramUsedMb is { } used
                ? $" · VRAM {used / 1024:0.0}/{s.VramTotalMb / 1024:0.0} GB"
                : "";
            _gpu.Subtitle = $"{Format.Celsius(s.GpuTemp)}{vram} · {Shorten(s.GpuName)}";
            _gpu.Level = Thresholds.ForTemp(s.GpuTemp) is var g and not Level.Unknown ? g : Level.Ok;
            _gpu.Push(s.GpuLoad ?? 0);
        }

        _ram.Value = $"{s.RamUsed / 1073741824.0:0.0} / {s.RamTotal / 1073741824.0:0} GB";
        _ram.Subtitle = $"{s.RamPercent:0} % used";
        _ram.Level = Thresholds.ForRam(s.RamPercent);
        _ram.Push(s.RamPercent);

        _net.Value = "↓ " + Format.Rate(s.TotalDown);
        _net.Subtitle = "↑ " + Format.Rate(s.TotalUp) + $" · {s.Adapters.Count} adapter(s)";
        _net.Level = Level.Ok;
        _net.Push(s.TotalDown, s.TotalUp);

        // Disks get a usage bar each rather than a tile: "% used" barely moves, so a sparkline says nothing,
        // and machines with many drives would push everything else off screen.
        foreach (var (disk, temp) in s.Disks)
        {
            if (!_disks.TryGetValue(disk.Letter, out var row))
            {
                row = new DiskRow(disk.Letter);
                _disks[disk.Letter] = row;
                Disks.Add(row);
            }
            row.UsedPercent = disk.UsedPercent;
            row.PercentText = Format.Percent(disk.UsedPercent);
            var name = string.IsNullOrWhiteSpace(disk.Label) ? "" : disk.Label + " · ";
            var tempText = temp is null ? "" : " · " + Format.Celsius(temp);
            row.Detail = $"{name}{Format.Bytes(disk.FreeBytes)} free of {Format.Bytes(disk.TotalBytes)}{tempText}";
            row.Level = Thresholds.ForDisk(disk.UsedPercent);
        }
    }

    public void ApplyProcesses(IReadOnlyList<ProcSample> processes)
    {
        Replace(TopCpu, processes.OrderByDescending(p => p.Cpu).Take(5));
        Replace(TopRam, processes.OrderByDescending(p => p.RamMb).Take(5));
    }

    static void Replace(ObservableCollection<ProcSample> target, IEnumerable<ProcSample> items)
    {
        var list = items.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            if (i < target.Count) target[i] = list[i];
            else target.Add(list[i]);
        }
        while (target.Count > list.Count) target.RemoveAt(target.Count - 1);
    }

    /// <summary>"Intel Core i5-7400" rather than "Intel(R) Core(TM) i5-7400 CPU @ 3.00GHz".</summary>
    static string? Shorten(string? name) =>
        name?.Replace("(R)", "").Replace("(TM)", "").Replace(" CPU", "").Split('@')[0].Trim();
}
