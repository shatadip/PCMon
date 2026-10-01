using System.Globalization;
using LibreHardwareMonitor.Hardware;

namespace PCMon.Core;

/// <summary>One sensor value, detached from LibreHardwareMonitor so the UI thread never touches LHM.</summary>
public sealed record SensorReading(
    string Id,
    string DeviceId,
    string DeviceName,
    HardwareType DeviceType,
    SensorType Type,
    string Name,
    float? Value);

/// <summary>Finds the headline numbers for the dashboard tiles among hundreds of sensors.</summary>
public static class SensorPicker
{
    static bool IsGpu(HardwareType t) => t is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    static float? Pick(IEnumerable<SensorReading> sensors, params string[] preferredNames)
    {
        var list = sensors.Where(s => s.Value is { } v && !float.IsNaN(v)).ToList();
        foreach (var name in preferredNames)
        {
            var hit = list.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit.Value;
        }
        return list.Count > 0 ? list.Max(s => s.Value) : null;
    }

    public static float? CpuTemperature(IReadOnlyList<SensorReading> sensors) =>
        Pick(sensors.Where(s => s.DeviceType == HardwareType.Cpu && s.Type == SensorType.Temperature),
            "CPU Package", "Core (Tctl/Tdie)", "Core Average", "Core Max");

    /// <summary>Prefer a discrete GPU (NVIDIA/AMD) over integrated Intel graphics.</summary>
    public static string? PrimaryGpuId(IReadOnlyList<SensorReading> sensors)
    {
        var gpus = sensors.Where(s => IsGpu(s.DeviceType)).Select(s => (s.DeviceId, s.DeviceType)).Distinct().ToList();
        if (gpus.Count == 0) return null;
        return gpus.OrderBy(g => g.DeviceType == HardwareType.GpuIntel ? 1 : 0).First().DeviceId;
    }

    public static string? GpuName(IReadOnlyList<SensorReading> sensors, string gpuId) =>
        sensors.FirstOrDefault(s => s.DeviceId == gpuId)?.DeviceName;

    public static float? GpuLoad(IReadOnlyList<SensorReading> sensors, string gpuId) =>
        Pick(sensors.Where(s => s.DeviceId == gpuId && s.Type == SensorType.Load), "GPU Core", "D3D 3D");

    public static float? GpuTemperature(IReadOnlyList<SensorReading> sensors, string gpuId) =>
        Pick(sensors.Where(s => s.DeviceId == gpuId && s.Type == SensorType.Temperature), "GPU Core", "GPU Hot Spot");

    /// <summary>VRAM used / total in MB, when the driver reports it.</summary>
    public static (float? Used, float? Total) GpuMemory(IReadOnlyList<SensorReading> sensors, string gpuId)
    {
        var mem = sensors.Where(s => s.DeviceId == gpuId && s.Type == SensorType.SmallData).ToList();
        float? Find(string name) => mem.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
        return (Find("GPU Memory Used") ?? Find("D3D Dedicated Memory Used"),
                Find("GPU Memory Total") ?? Find("D3D Dedicated Memory Total"));
    }

    public static float? StorageTemperature(IReadOnlyList<SensorReading> sensors, string deviceId) =>
        Pick(sensors.Where(s => s.DeviceId == deviceId && s.Type == SensorType.Temperature), "Temperature", "Composite Temperature");
}

public static class SensorFormat
{
    static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public static string Format(SensorType type, float? value)
    {
        if (value is not { } v || float.IsNaN(v) || float.IsInfinity(v)) return "—";
        return type switch
        {
            SensorType.Temperature => v.ToString("0.0", C) + " °C",
            SensorType.Load or SensorType.Control or SensorType.Level => v.ToString("0.0", C) + " %",
            SensorType.Clock => v.ToString("#,0", C) + " MHz",
            SensorType.Fan => v.ToString("#,0", C) + " RPM",
            SensorType.Voltage => v.ToString("0.000", C) + " V",
            SensorType.Current => v.ToString("0.000", C) + " A",
            SensorType.Power => v.ToString("0.0", C) + " W",
            SensorType.Frequency => v.ToString("#,0", C) + " Hz",
            SensorType.Flow => v.ToString("0.0", C) + " L/h",
            SensorType.Data => v.ToString("0.0", C) + " GB",
            SensorType.SmallData => v.ToString("#,0", C) + " MB",
            SensorType.Throughput => Core.Format.Rate(v),
            SensorType.TimeSpan => TimeSpan.FromSeconds(v).ToString(@"d\.hh\:mm\:ss", C),
            SensorType.Energy => v.ToString("#,0", C) + " mWh",
            SensorType.Noise => v.ToString("0.0", C) + " dBA",
            _ => v.ToString("0.###", C),
        };
    }

    public static string Group(SensorType type) => type switch
    {
        SensorType.Temperature => "Temperatures",
        SensorType.Load => "Loads",
        SensorType.Clock => "Clocks",
        SensorType.Fan => "Fans",
        SensorType.Voltage => "Voltages",
        SensorType.Current => "Currents",
        SensorType.Power => "Powers",
        SensorType.Data or SensorType.SmallData => "Data",
        SensorType.Throughput => "Throughput",
        SensorType.Control => "Controls",
        SensorType.Level => "Levels",
        _ => type.ToString(),
    };

    /// <summary>Segoe MDL2 glyph per device type, for the tree.</summary>
    public static string DeviceGlyph(HardwareType type) => type switch
    {
        HardwareType.Cpu => "",
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "",
        HardwareType.Memory => "",
        HardwareType.Storage => "",
        HardwareType.Battery => "",
        HardwareType.Network => "",
        _ => "",
    };
}
