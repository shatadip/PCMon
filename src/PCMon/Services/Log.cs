namespace PCMon.Services;

/// <summary>Tiny rolling log at %LOCALAPPDATA%\PCMon\pcmon.log (SRS NFR-4). Never throws.</summary>
static class Log
{
    static readonly object Gate = new();
    static readonly HashSet<string> Seen = [];
    const long MaxBytes = 1_000_000;

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCMon");

    public static string FilePath => Path.Combine(Folder, "pcmon.log");

    public static void Info(string message) => Write("INFO  " + message);

    /// <summary>Logs each distinct error once, so a broken sensor polled every second can't flood the file.</summary>
    public static void Error(string context, Exception ex)
    {
        var line = $"ERROR {context}: {ex.GetType().Name}: {ex.Message}";
        lock (Gate)
        {
            if (!Seen.Add(line)) return;
        }
        Write(line);
    }

    static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes)
                {
                    File.Copy(FilePath, FilePath + ".1", overwrite: true);
                    File.Delete(FilePath);
                }
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
