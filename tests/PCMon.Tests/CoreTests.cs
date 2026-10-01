using LibreHardwareMonitor.Hardware;
using PCMon.Core;
using Xunit;

namespace PCMon.Tests;

public class ThresholdTests
{
    [Theory]
    [InlineData(null, Level.Unknown)]
    [InlineData(double.NaN, Level.Unknown)]
    [InlineData(45.0, Level.Ok)]
    [InlineData(79.9, Level.Ok)]
    [InlineData(80.0, Level.Warn)]
    [InlineData(89.9, Level.Warn)]
    [InlineData(90.0, Level.Critical)]
    public void Temperature(double? celsius, Level expected) => Assert.Equal(expected, Thresholds.ForTemp(celsius));

    [Theory]
    [InlineData(50.0, Level.Ok)]
    [InlineData(85.0, Level.Warn)]
    [InlineData(95.0, Level.Critical)]
    public void Ram(double percent, Level expected) => Assert.Equal(expected, Thresholds.ForRam(percent));

    [Theory]
    [InlineData(89.9, Level.Ok)]
    [InlineData(90.0, Level.Warn)]
    [InlineData(100.0, Level.Warn)]
    public void DiskHasNoRedLevel(double percent, Level expected) => Assert.Equal(expected, Thresholds.ForDisk(percent));

    [Fact]
    public void WorstPicksHighestSeverity() =>
        Assert.Equal(Level.Critical, Thresholds.Worst(Level.Ok, Level.Critical, Level.Warn, Level.Unknown));

    [Fact]
    public void WorstOfNothingIsUnknown() => Assert.Equal(Level.Unknown, Thresholds.Worst());
}

public class FormatTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(2.4 * 1024 * 1024, "2.4 MB")]
    [InlineData(150.0 * 1024 * 1024 * 1024, "150 GB")]
    [InlineData(-1, "—")]
    public void Bytes(double value, string expected) => Assert.Equal(expected, Format.Bytes(value));

    [Fact]
    public void RateAppendsPerSecond() => Assert.Equal("180 KB/s", Format.Rate(180 * 1024));

    [Fact]
    public void MissingValuesShowDash()
    {
        Assert.Equal("—", Format.Percent(null));
        Assert.Equal("—", Format.Celsius(double.NaN));
        Assert.Equal("61 °C", Format.Celsius(61.2));
    }

    [Fact]
    public void DaysAgo()
    {
        var now = new DateTime(2026, 10, 2, 12, 0, 0);
        Assert.Equal("today", Format.DaysAgo(now.AddHours(-3), now));
        Assert.Equal("1 day ago", Format.DaysAgo(now.AddDays(-1), now));
        Assert.Equal("123 days ago", Format.DaysAgo(now.AddDays(-123), now));
    }

    [Theory]
    [InlineData(1000, 3000, 2.0, 1000)]
    [InlineData(5000, 1000, 1.0, 0)] // counter reset
    [InlineData(0, 1000, 0.0, 0)]    // no elapsed time
    public void NetworkRate(long previous, long current, double seconds, double expected) =>
        Assert.Equal(expected, Rate.BytesPerSecond(previous, current, seconds));
}

public class RollingBufferTests
{
    [Fact]
    public void KeepsNewestValuesInOrder()
    {
        var b = new RollingBuffer(3);
        foreach (var v in new double[] { 1, 2, 3, 4, 5 }) b.Add(v);
        Assert.Equal(new double[] { 3, 4, 5 }, b.ToArray());
        Assert.Equal(5, b.Max());
        Assert.Equal(3, b.Count);
    }

    [Fact]
    public void PartiallyFilled()
    {
        var b = new RollingBuffer(60);
        b.Add(7);
        Assert.Equal(new double[] { 7 }, b.ToArray());
    }
}

public class AppHeuristicTests
{
    [Fact]
    public void Rot13RoundTrips()
    {
        Assert.Equal("UEME_CTLSESSION", Rot13.Decode("HRZR_PGYFRFFVBA"));
        Assert.Equal(@"C:\Tools\App.exe", Rot13.Decode(Rot13.Decode(@"C:\Tools\App.exe")));
    }

    [Theory]
    [InlineData("CHROME.EXE-1A2B3C4D.pf", "CHROME.EXE")]
    [InlineData("MY-APP.EXE-0F00BA12.pf", "MY-APP.EXE")]
    [InlineData("Layout.ini", null)]
    [InlineData("NODASH.pf", null)]
    public void PrefetchName(string file, string? expected) => Assert.Equal(expected, UsageEvidence.ExeFromPrefetch(file));

    [Fact]
    public void UserAssistReadsFiletimeAtOffset60()
    {
        var when = new DateTime(2025, 3, 14, 15, 9, 26, DateTimeKind.Utc);
        var data = new byte[72];
        BitConverter.GetBytes(when.ToFileTimeUtc()).CopyTo(data, 60);
        Assert.Equal(when.ToLocalTime(), UsageEvidence.LastRunFromUserAssist(data));
    }

    [Fact]
    public void UserAssistRejectsShortOrEmptyData()
    {
        Assert.Null(UsageEvidence.LastRunFromUserAssist(null));
        Assert.Null(UsageEvidence.LastRunFromUserAssist(new byte[16]));
        Assert.Null(UsageEvidence.LastRunFromUserAssist(new byte[72]));
    }

    [Theory]
    [InlineData("unins000.exe", true)]
    [InlineData("Uninstall.exe", true)]
    [InlineData("Update.exe", true)]
    [InlineData("crashpad_handler.exe", true)]
    [InlineData("Code.exe", false)]
    [InlineData("obs64.exe", false)]
    public void NoiseExes(string exe, bool noise) => Assert.Equal(noise, UsageEvidence.IsNoiseExe(exe));

    [Theory]
    [InlineData("App", 0, null, "u.exe", null, true)]
    [InlineData("", 0, null, "u.exe", null, false)]
    [InlineData("Component", 1, null, "u.exe", null, false)]
    [InlineData("Patch", 0, "Office", "u.exe", null, false)]
    [InlineData("NoUninstaller", 0, null, null, null, false)]
    [InlineData("KB123", 0, null, "u.exe", "Security Update", false)]
    [InlineData("Hotfix", 0, null, "u.exe", "Hotfix", false)]
    public void InstalledFilter(string name, int sysComponent, string? parent, string? uninstall, string? release, bool include) =>
        Assert.Equal(include, InstalledAppFilter.Include(name, sysComponent, parent, uninstall, release));

    [Fact]
    public void InstallDate()
    {
        Assert.Equal(new DateTime(2024, 1, 15), InstalledAppFilter.ParseInstallDate("20240115"));
        Assert.Null(InstalledAppFilter.ParseInstallDate("15/01/2024"));
        Assert.Null(InstalledAppFilter.ParseInstallDate(null));
    }

    [Fact]
    public void UnusedRuleRespectsDaysAndUnknowns()
    {
        var now = new DateTime(2026, 10, 2);
        Assert.True(UnusedRule.IsUnused(now.AddDays(-91), 90, now, includeUnknown: false));
        Assert.False(UnusedRule.IsUnused(now.AddDays(-10), 90, now, includeUnknown: false));
        Assert.True(UnusedRule.IsUnused(null, 90, now, includeUnknown: true));
        Assert.False(UnusedRule.IsUnused(null, 90, now, includeUnknown: false));
        Assert.True(UnusedRule.IsUnused(now, 0, now, includeUnknown: false)); // "Show all"
    }
}

public class UninstallCommandTests
{
    [Fact]
    public void QuotedPathWithArguments()
    {
        var (file, args) = UninstallCommand.Split("\"C:\\Program Files\\Foo\\unins000.exe\" /SILENT");
        Assert.Equal(@"C:\Program Files\Foo\unins000.exe", file);
        Assert.Equal("/SILENT", args);
    }

    [Fact]
    public void UnquotedPathWithSpacesUsesExistingFile()
    {
        var exists = (string p) => p == @"C:\Program Files\My App\uninstall.exe";
        var (file, args) = UninstallCommand.Split(@"C:\Program Files\My App\uninstall.exe /user", exists);
        Assert.Equal(@"C:\Program Files\My App\uninstall.exe", file);
        Assert.Equal("/user", args);
    }

    [Fact]
    public void UnquotedPathFallsBackToFirstExe()
    {
        var (file, args) = UninstallCommand.Split(@"C:\Apps\x\remove.exe --all", _ => false);
        Assert.Equal(@"C:\Apps\x\remove.exe", file);
        Assert.Equal("--all", args);
    }

    [Theory]
    [InlineData("MsiExec.exe /I{12345678-ABCD-1234-ABCD-1234567890AB}", "/X{12345678-ABCD-1234-ABCD-1234567890AB}")]
    [InlineData("MsiExec.exe /X{12345678-ABCD-1234-ABCD-1234567890AB}", "/X{12345678-ABCD-1234-ABCD-1234567890AB}")]
    [InlineData("msiexec /i {12345678-ABCD-1234-ABCD-1234567890AB} /qb", "/X{12345678-ABCD-1234-ABCD-1234567890AB} /qb")]
    public void MsiModifyBecomesUninstall(string command, string expectedArgs)
    {
        var (_, args) = UninstallCommand.Split(command, _ => false);
        Assert.Equal(expectedArgs, args);
    }

    [Fact]
    public void NonMsiArgumentsAreUntouched()
    {
        var (_, args) = UninstallCommand.Split("\"C:\\x\\setup.exe\" /I{ABC}");
        Assert.Equal("/I{ABC}", args);
    }
}

public class ProcessRuleTests
{
    [Theory]
    [InlineData("lsass", 700, true)]
    [InlineData("CSRSS", 600, true)]
    [InlineData("System", 4, true)]
    [InlineData("anything", 0, true)]
    [InlineData("notepad", 1234, false)]
    [InlineData("chrome", 4321, false)]
    public void Critical(string name, int pid, bool critical) => Assert.Equal(critical, CriticalProcesses.IsCritical(name, pid));

    [Fact]
    public void StatusText()
    {
        Assert.Equal("Not Responding", ProcStatusText.Of(ProcStatus.NotResponding));
        Assert.Equal("Running", ProcStatusText.Of(ProcStatus.Running));
    }

    [Theory]
    [InlineData(1, "Disabled")]
    [InlineData(3, "Ready")]
    [InlineData(4, "Running")]
    [InlineData(9, "Unknown")]
    public void TaskStates(int state, string text) => Assert.Equal(text, TaskText.State(state));

    [Theory]
    [InlineData(0, "Success")]
    [InlineData(0x41303, "Never run")]
    [InlineData(unchecked((int)0x80070005), "0x80070005")]
    public void TaskResults(int code, string text) => Assert.Equal(text, TaskText.Result(code));

    [Theory]
    [InlineData(0x5000u, 80)]  // 80 stored big-endian in the low word
    [InlineData(0xBB01u, 443)]
    public void TcpPort(uint raw, int port) => Assert.Equal(port, TcpText.Port(raw));

    [Fact]
    public void TcpState() => Assert.Equal("Established", TcpText.State(5));
}

public class StatusMessengerTests
{
    static StatusInput Input(Level cpu = Level.Ok, Level gpu = Level.Ok, Level ram = Level.Ok, Level disk = Level.Ok, double idle = 0) =>
        new(cpu, gpu, ram, disk, idle);

    [Fact] public void AllGreen() => Assert.Equal(Strings.AllGreen, StatusMessenger.Pick(Input()));
    [Fact] public void HotCpuWins() => Assert.Equal(Strings.CpuHot, StatusMessenger.Pick(Input(cpu: Level.Critical, ram: Level.Critical)));
    [Fact] public void FullRam() => Assert.Equal(Strings.RamFull, StatusMessenger.Pick(Input(ram: Level.Critical, disk: Level.Warn)));
    [Fact] public void WarmTemps() => Assert.Equal(Strings.Warming, StatusMessenger.Pick(Input(gpu: Level.Warn)));
    [Fact] public void BusyRam() => Assert.Equal(Strings.RamBusy, StatusMessenger.Pick(Input(ram: Level.Warn, disk: Level.Warn)));
    [Fact] public void FullDisk() => Assert.Equal(Strings.DiskFull, StatusMessenger.Pick(Input(disk: Level.Warn, idle: 120)));
    [Fact] public void QuietNetwork() => Assert.Equal(Strings.NetIdle, StatusMessenger.Pick(Input(idle: 60)));
    [Fact] public void UnknownLevelsAreNotWarnings() => Assert.Equal(Strings.AllGreen, StatusMessenger.Pick(Input(Level.Unknown, Level.Unknown)));
}

public class SensorTests
{
    static SensorReading R(string device, HardwareType dt, SensorType t, string name, float? v) =>
        new($"/{device}/{t}/{name}", "/" + device, device, dt, t, name, v);

    [Fact]
    public void CpuPackagePreferredOverCores()
    {
        var s = new[]
        {
            R("cpu", HardwareType.Cpu, SensorType.Temperature, "CPU Core #1", 70),
            R("cpu", HardwareType.Cpu, SensorType.Temperature, "CPU Package", 64),
        };
        Assert.Equal(64, SensorPicker.CpuTemperature(s));
    }

    [Fact]
    public void CpuTemperatureFallsBackToHottestAndIgnoresMissing()
    {
        var s = new[]
        {
            R("cpu", HardwareType.Cpu, SensorType.Temperature, "CPU Core #1", 55),
            R("cpu", HardwareType.Cpu, SensorType.Temperature, "CPU Core #2", 58),
            R("cpu", HardwareType.Cpu, SensorType.Temperature, "CPU Package", null),
        };
        Assert.Equal(58, SensorPicker.CpuTemperature(s));
        Assert.Null(SensorPicker.CpuTemperature([]));
    }

    [Fact]
    public void DiscreteGpuBeatsIntegrated()
    {
        var s = new[]
        {
            R("igpu", HardwareType.GpuIntel, SensorType.Load, "D3D 3D", 5),
            R("nv", HardwareType.GpuNvidia, SensorType.Load, "GPU Core", 42),
            R("nv", HardwareType.GpuNvidia, SensorType.Temperature, "GPU Core", 51),
            R("nv", HardwareType.GpuNvidia, SensorType.SmallData, "GPU Memory Used", 540),
            R("nv", HardwareType.GpuNvidia, SensorType.SmallData, "GPU Memory Total", 4096),
        };
        var id = SensorPicker.PrimaryGpuId(s);
        Assert.Equal("/nv", id);
        Assert.Equal(42, SensorPicker.GpuLoad(s, id!));
        Assert.Equal(51, SensorPicker.GpuTemperature(s, id!));
        Assert.Equal((540f, 4096f), SensorPicker.GpuMemory(s, id!));
    }

    [Fact]
    public void NoGpu() => Assert.Null(SensorPicker.PrimaryGpuId([R("cpu", HardwareType.Cpu, SensorType.Load, "CPU Total", 3)]));

    [Theory]
    [InlineData(SensorType.Temperature, 61.24f, "61.2 °C")]
    [InlineData(SensorType.Clock, 3000f, "3,000 MHz")]
    [InlineData(SensorType.Fan, 1200f, "1,200 RPM")]
    [InlineData(SensorType.Voltage, 1.2154f, "1.215 V")]
    [InlineData(SensorType.Load, 34.21f, "34.2 %")]
    [InlineData(SensorType.SmallData, 1024f, "1,024 MB")]
    [InlineData(SensorType.Power, float.NaN, "—")]
    public void SensorValues(SensorType type, float value, string expected) => Assert.Equal(expected, SensorFormat.Format(type, value));

    [Fact]
    public void MissingSensorIsDash() => Assert.Equal("—", SensorFormat.Format(SensorType.Temperature, null));

    [Fact]
    public void GroupNames()
    {
        Assert.Equal("Temperatures", SensorFormat.Group(SensorType.Temperature));
        Assert.Equal("Data", SensorFormat.Group(SensorType.SmallData));
    }
}

public class StringsTests
{
    [Fact]
    public void ConfirmationsNameTheTarget()
    {
        Assert.Contains("notepad", Strings.EndTaskConfirm("notepad"));
        Assert.Contains("Unsaved work will be lost", Strings.EndTaskConfirm("notepad"));
        Assert.Contains("lsass", Strings.KillBlocked("lsass"));
        var u = Strings.UninstallConfirm(2, ["Foo", "Bar"]);
        Assert.Contains("2 app(s)", u);
        Assert.Contains("• Foo", u);
    }

    [Fact]
    public void InAppStringsAvoidEmojiWpfCannotDraw()
    {
        var all = typeof(Strings).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!);
        foreach (var s in all)
            Assert.DoesNotContain(s, c => char.IsSurrogate(c));
    }
}
