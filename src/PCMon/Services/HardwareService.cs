using LibreHardwareMonitor.Hardware;
using PCMon.Core;

namespace PCMon.Services;

/// <summary>
/// Wraps LibreHardwareMonitor. Only the sampler thread touches it; everything it returns is
/// a plain <see cref="SensorReading"/> copy.
/// </summary>
sealed class HardwareService : IDisposable
{
    // Published only after Open() completes, so a concurrent Update() sees either nothing or a ready instance.
    volatile Computer? _computer;

    public string? InitError { get; private set; }
    public bool IsOpen => _computer is not null;

    public void Open()
    {
        try
        {
            var computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsStorageEnabled = true,
                IsBatteryEnabled = true,
            };
            computer.Open();
            _computer = computer;
            Log.Info($"LibreHardwareMonitor opened: {computer.Hardware.Count} devices");
        }
        catch (Exception ex)
        {
            InitError = ex.Message;
            Log.Error("LibreHardwareMonitor open", ex);
        }
    }

    int _updates;

    public IReadOnlyList<SensorReading> Update()
    {
        var list = new List<SensorReading>();
        if (_computer is not { } computer) return list;

        // SMART reads cost ~10 ms per drive and disk temperatures crawl, so storage and battery refresh every 5 s.
        bool slowTick = _updates++ % 5 == 0;
        foreach (var hw in computer.Hardware)
            Visit(hw, null, list, refresh: slowTick || hw.HardwareType is not (HardwareType.Storage or HardwareType.Battery));
        return list;
    }

    static void Visit(IHardware hw, IHardware? parent, List<SensorReading> list, bool refresh = true)
    {
        try
        {
            if (refresh) hw.Update();
        }
        catch (Exception ex)
        {
            Log.Error("update " + hw.Name, ex);
            return;
        }

        var deviceName = parent is null ? hw.Name : $"{parent.Name} › {hw.Name}";
        var deviceType = parent?.HardwareType ?? hw.HardwareType;
        foreach (var s in hw.Sensors)
        {
            try
            {
                list.Add(new SensorReading(s.Identifier.ToString(), hw.Identifier.ToString(), deviceName,
                    deviceType, s.SensorType, s.Name, s.Value));
            }
            catch (Exception ex)
            {
                Log.Error("sensor " + hw.Name, ex);
            }
        }

        foreach (var sub in hw.SubHardware)
            Visit(sub, hw, list, refresh);
    }

    /// <summary>For --selftest profiling: milliseconds per top-level device update.</summary>
    public Dictionary<string, double> ProfileDevices()
    {
        var result = new Dictionary<string, double>();
        if (_computer is null) return result;
        foreach (var hw in _computer.Hardware)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { hw.Update(); foreach (var sub in hw.SubHardware) sub.Update(); }
            catch { }
            result[$"{hw.HardwareType}:{hw.Name}"] = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
        }
        return result;
    }

    public void Dispose()
    {
        try { _computer?.Close(); }
        catch (Exception ex) { Log.Error("LibreHardwareMonitor close", ex); }
        _computer = null;
    }
}
