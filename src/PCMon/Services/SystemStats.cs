using System.Management;

namespace PCMon.Services;

/// <summary>Total CPU load from GetSystemTimes. Works without any driver.</summary>
sealed class CpuUsage
{
    long _prevIdle, _prevTotal;

    public double? Sample()
    {
        if (!Native.GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        long total = kernel + user; // kernel time already includes idle time
        long dTotal = total - _prevTotal, dIdle = idle - _prevIdle;
        bool first = _prevTotal == 0;
        _prevIdle = idle;
        _prevTotal = total;
        if (first || dTotal <= 0) return null;
        return Math.Clamp((dTotal - dIdle) * 100.0 / dTotal, 0, 100);
    }
}

public sealed record DiskUsage(string Letter, string Label, long TotalBytes, long FreeBytes, string? Model)
{
    public double UsedPercent => TotalBytes > 0 ? (TotalBytes - FreeBytes) * 100.0 / TotalBytes : 0;
}

static class SystemStats
{
    public static (ulong Used, ulong Total)? Memory()
    {
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (!Native.GlobalMemoryStatusEx(ref m)) return null;
        return (m.ullTotalPhys - m.ullAvailPhys, m.ullTotalPhys);
    }

    public static IReadOnlyList<DiskUsage> Disks(IReadOnlyDictionary<string, string> modelByLetter)
    {
        var list = new List<DiskUsage>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                var letter = d.Name.TrimEnd('\\');
                modelByLetter.TryGetValue(letter, out var model);
                list.Add(new DiskUsage(letter, d.VolumeLabel, d.TotalSize, d.TotalFreeSpace, model));
            }
            catch (Exception ex)
            {
                Log.Error("drive " + d.Name, ex);
            }
        }
        return list;
    }

    /// <summary>"C:" → physical disk model, so LHM storage temperatures can be put on the right tile.</summary>
    public static Dictionary<string, string> DiskModelsByLetter()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var drives = new ManagementObjectSearcher("SELECT DeviceID, Model FROM Win32_DiskDrive");
            foreach (ManagementObject drive in drives.Get())
            {
                var model = drive["Model"]?.ToString()?.Trim();
                if (string.IsNullOrEmpty(model)) continue;
                foreach (ManagementObject partition in drive.GetRelated("Win32_DiskPartition"))
                    foreach (ManagementObject logical in partition.GetRelated("Win32_LogicalDisk"))
                        if (logical["DeviceID"]?.ToString() is { } letter)
                            map[letter] = model;
            }
        }
        catch (Exception ex)
        {
            Log.Error("disk model map", ex);
        }
        return map;
    }

    /// <summary>
    /// ACPI thermal zone, used when LHM has no CPU temperature (e.g. no PawnIO driver).
    /// Often a motherboard zone rather than the die, so the UI labels it "ACPI".
    /// </summary>
    public static double? AcpiTemperature()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            double? max = null;
            foreach (ManagementObject zone in searcher.Get())
            {
                if (zone["CurrentTemperature"] is not { } raw) continue;
                var celsius = Convert.ToDouble(raw) / 10.0 - 273.15;
                if (celsius is > 0 and < 150) max = Math.Max(max ?? double.MinValue, celsius);
            }
            return max;
        }
        catch (Exception ex)
        {
            Log.Error("acpi temperature", ex);
            return null;
        }
    }
}
