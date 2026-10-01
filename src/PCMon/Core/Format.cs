using System.Globalization;

namespace PCMon.Core;

public static class Format
{
    static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB", "PB"];
    static readonly CultureInfo C = CultureInfo.InvariantCulture;

    /// <summary>1536 → "1.5 KB". Binary (1024) units, like Task Manager.</summary>
    public static string Bytes(double bytes)
    {
        if (double.IsNaN(bytes) || bytes < 0) return "—";
        int unit = 0;
        while (bytes >= 1024 && unit < ByteUnits.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes.ToString("0", C)} {ByteUnits[unit]}"
            : $"{bytes.ToString(bytes >= 100 ? "0" : "0.0", C)} {ByteUnits[unit]}";
    }

    public static string Rate(double bytesPerSecond) => Bytes(bytesPerSecond) + "/s";

    public static string Percent(double? value) =>
        value is null or double.NaN ? "—" : value.Value.ToString("0", C) + " %";

    public static string Celsius(double? value) =>
        value is null or double.NaN ? "—" : value.Value.ToString("0", C) + " °C";

    /// <summary>"today", "1 day ago", "123 days ago".</summary>
    public static string DaysAgo(DateTime when, DateTime now)
    {
        var days = (int)Math.Floor((now - when).TotalDays);
        return days switch
        {
            <= 0 => "today",
            1 => "1 day ago",
            _ => $"{days} days ago",
        };
    }
}

/// <summary>Network counter maths. Counters can wrap or reset when an adapter reconnects.</summary>
public static class Rate
{
    public static double BytesPerSecond(long previous, long current, double seconds) =>
        seconds <= 0 || current < previous ? 0 : (current - previous) / seconds;
}
