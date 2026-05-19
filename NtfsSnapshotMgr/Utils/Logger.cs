using System.Diagnostics;

namespace NtfsSnapshotMgr.Utils;

/// <summary>
/// Thread-safe file logger. Writes to a .log file next to the executable.
/// </summary>
public static class Logger
{
    private static readonly string LogPath;
    private static readonly object LockObj = new();

    static Logger()
    {
        var dir = AppContext.BaseDirectory;
        var name = Process.GetCurrentProcess().ProcessName;
        LogPath = Path.Combine(dir, $"{name}.log");
    }

    public static void Info(string message)    => Write("INFO", message);
    public static void Warn(string message)    => Write("WARN", message);
    public static void Error(string message)   => Write("ERROR", message);
    public static void Error(string message, Exception ex)
        => Write("ERROR", $"{message} | Exception: {ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";

        lock (LockObj)
        {
            try { File.AppendAllText(LogPath, line + Environment.NewLine); }
            catch { /* can't log — nothing we can do */ }
        }
    }
}
