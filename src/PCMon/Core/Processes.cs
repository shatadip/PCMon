namespace PCMon.Core;

public enum ProcStatus { Running, NotResponding, Suspended }

/// <summary>Processes PCMon refuses to kill (SRS FR-13).</summary>
public static class CriticalProcesses
{
    static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression", "Secure System",
        "csrss", "wininit", "winlogon", "lsass", "smss", "services", "LsaIso",
    };

    public static bool IsCritical(string name, int pid) => pid is 0 or 4 || Names.Contains(name);
}

public static class ProcStatusText
{
    public static string Of(ProcStatus s) => s switch
    {
        ProcStatus.NotResponding => "Not Responding",
        ProcStatus.Suspended => "Suspended",
        _ => "Running",
    };
}

/// <summary>Task Scheduler states and result codes (SRS FR-14).</summary>
public static class TaskText
{
    public static string State(int state) => state switch
    {
        1 => "Disabled",
        2 => "Queued",
        3 => "Ready",
        4 => "Running",
        _ => "Unknown",
    };

    public static string Result(int code) => unchecked((uint)code) switch
    {
        0 => "Success",
        0x41300 => "Ready",
        0x41301 => "Running",
        0x41302 => "Disabled",
        0x41303 => "Never run",
        0x41306 => "Terminated",
        0x8004131F => "Already running",
        0x800710E0 => "Refused",
        var c => $"0x{c:X8}",
    };
}

/// <summary>Helpers for the raw MIB_TCPROW_OWNER_PID table (SRS FR-11).</summary>
public static class TcpText
{
    /// <summary>Ports are stored in network byte order in the low 16 bits.</summary>
    public static int Port(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));

    public static string State(uint state) => state switch
    {
        1 => "Closed",
        2 => "Listen",
        3 => "SynSent",
        4 => "SynReceived",
        5 => "Established",
        6 => "FinWait1",
        7 => "FinWait2",
        8 => "CloseWait",
        9 => "Closing",
        10 => "LastAck",
        11 => "TimeWait",
        12 => "DeleteTcb",
        _ => "Unknown",
    };
}
