namespace PCMon.Core;

public enum Level { Unknown, Ok, Warn, Critical }

/// <summary>Green / amber / red rules from SRS FR-3.</summary>
public static class Thresholds
{
    public const double TempWarn = 80, TempCritical = 90;
    public const double RamWarn = 85, RamCritical = 95;
    public const double DiskWarn = 90;

    public static Level ForTemp(double? celsius) => celsius switch
    {
        null or double.NaN => Level.Unknown,
        >= TempCritical => Level.Critical,
        >= TempWarn => Level.Warn,
        _ => Level.Ok,
    };

    public static Level ForRam(double? percent) => percent switch
    {
        null or double.NaN => Level.Unknown,
        >= RamCritical => Level.Critical,
        >= RamWarn => Level.Warn,
        _ => Level.Ok,
    };

    public static Level ForDisk(double? percent) => percent switch
    {
        null or double.NaN => Level.Unknown,
        >= DiskWarn => Level.Warn,
        _ => Level.Ok,
    };

    public static Level Worst(params Level[] levels)
    {
        var worst = Level.Unknown;
        foreach (var l in levels)
            if (l > worst) worst = l;
        return worst;
    }
}
