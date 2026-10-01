using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibreHardwareMonitor.Hardware;
using PCMon.Core;
using PCMon.Services;

namespace PCMon.ViewModels;

public sealed class SensorNode(string name, SensorType type) : ObservableObject
{
    float? _min, _max, _value;

    public string Name { get; } = name;
    public SensorType Type { get; } = type;

    public string ValueText => SensorFormat.Format(Type, _value);
    public string MinText => SensorFormat.Format(Type, _min);
    public string MaxText => SensorFormat.Format(Type, _max);
    public bool IsMissing => _value is null || float.IsNaN(_value.Value);
    public string? Tooltip => IsMissing ? Strings.SensorShy : null;

    public void Update(float? value)
    {
        _value = value;
        if (value is { } v && !float.IsNaN(v))
        {
            _min = _min is null ? v : Math.Min(_min.Value, v);
            _max = _max is null ? v : Math.Max(_max.Value, v);
        }
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(MinText));
        OnPropertyChanged(nameof(MaxText));
        OnPropertyChanged(nameof(IsMissing));
        OnPropertyChanged(nameof(Tooltip));
    }

    public void ResetMinMax()
    {
        _min = _max = null;
        Update(_value);
    }
}

public sealed class GroupNode(string name) : ObservableObject
{
    public string Name { get; } = name;
    public ObservableCollection<SensorNode> Children { get; } = [];

    bool _isExpanded = true;
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
}

public sealed class DeviceNode(string name, string glyph) : ObservableObject
{
    public string Name { get; } = name;
    public string Glyph { get; } = glyph;
    public ObservableCollection<GroupNode> Children { get; } = [];

    bool _isExpanded = true;
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
}

/// <summary>The HWMonitor page (SRS FR-5..8): Device → Sensor type → Sensor, with min/max since start.</summary>
public sealed class HardwareViewModel : PageViewModel
{
    readonly Dictionary<string, SensorNode> _sensors = [];
    readonly Dictionary<string, DeviceNode> _devices = [];
    readonly Dictionary<(string, string), GroupNode> _groups = [];

    public HardwareViewModel() : base("Hardware", "")
    {
        ResetMinMaxCommand = new RelayCommand(() =>
        {
            foreach (var s in _sensors.Values) s.ResetMinMax();
        });
    }

    public ObservableCollection<DeviceNode> Devices { get; } = [];
    public IRelayCommand ResetMinMaxCommand { get; }

    string? _banner;
    public string? Banner { get => _banner; private set => SetProperty(ref _banner, value); }

    string _summary = Strings.Starting;
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public void Apply(Snapshot s)
    {
        foreach (var r in s.Sensors)
        {
            if (!_sensors.TryGetValue(r.Id, out var node))
            {
                node = new SensorNode(r.Name, r.Type);
                _sensors[r.Id] = node;
                GroupFor(r).Children.Add(node);
            }
            node.Update(r.Value);
        }

        if (!s.HardwareReady)
        {
            Summary = Strings.Starting;
            return;
        }
        Banner = s.HardwareError is not null
            ? "Hardware sensors could not start: " + s.HardwareError
            : s.HasLhmCpuTemp ? null : Strings.CpuSensorsNeedDriver;
        Summary = $"{_devices.Count} devices · {_sensors.Count} sensors · min/max since start";
    }

    GroupNode GroupFor(SensorReading r)
    {
        if (!_devices.TryGetValue(r.DeviceId, out var device))
        {
            device = new DeviceNode(r.DeviceName, SensorFormat.DeviceGlyph(r.DeviceType));
            _devices[r.DeviceId] = device;
            Devices.Add(device);
        }

        var groupName = SensorFormat.Group(r.Type);
        if (!_groups.TryGetValue((r.DeviceId, groupName), out var group))
        {
            group = new GroupNode(groupName);
            _groups[(r.DeviceId, groupName)] = group;
            device.Children.Add(group);
        }
        return group;
    }
}
