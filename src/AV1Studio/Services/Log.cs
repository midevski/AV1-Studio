using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace AV1Studio.Services;

public enum LogLevel { Info, Success, Warning, Error, Command, Tool }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message, string? File)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public string Display => File is null ? Message : $"[{File}] {Message}";
}

/// <summary>Application log: raised as events for the UI and appended to a daily file on disk.
/// File writes happen on a background queue so the UI never blocks on I/O.</summary>
public static class Log
{
    public static event Action<LogEntry>? Entry;

    private static readonly BlockingCollection<string> Pending = new(new ConcurrentQueue<string>(), 10000);
    private static Thread? _writer;

    public static string CurrentFile => Path.Combine(AppPaths.Logs, $"av1-studio_{DateTime.Now:yyyy-MM-dd}.log");

    public static void Start()
    {
        if (_writer != null) return;
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "log-writer", Priority = ThreadPriority.BelowNormal };
        _writer.Start();
    }

    public static void Info(string msg, string? file = null) => Write(LogLevel.Info, msg, file);
    public static void Success(string msg, string? file = null) => Write(LogLevel.Success, msg, file);
    public static void Warn(string msg, string? file = null) => Write(LogLevel.Warning, msg, file);
    public static void Error(string msg, string? file = null) => Write(LogLevel.Error, msg, file);
    public static void Command(string msg, string? file = null) => Write(LogLevel.Command, msg, file);

    /// <summary>Raw ab-av1 / FFmpeg output: always in the log file; shown in the Logs page on demand.</summary>
    public static void Tool(string msg, string? file = null) => Write(LogLevel.Tool, msg, file);

    /// <summary>Only to the log file (verbose child process output).</summary>
    public static void FileOnly(string msg) => Pending.TryAdd($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}   {msg}");

    public static void Write(LogLevel level, string msg, string? file = null)
    {
        var e = new LogEntry(DateTime.Now, level, msg, file);
        Pending.TryAdd($"{e.Time:yyyy-MM-dd HH:mm:ss} {level.ToString().ToUpperInvariant(),-7} {e.Display}");
        try { Entry?.Invoke(e); } catch { /* UI listener must never break logging */ }
    }

    private static void WriteLoop()
    {
        foreach (var line in Pending.GetConsumingEnumerable())
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Logs);
                var sb = new StringBuilder(line).AppendLine();
                while (Pending.TryTake(out var more)) sb.AppendLine(more);
                File.AppendAllText(CurrentFile, sb.ToString(), Encoding.UTF8);
            }
            catch { /* disk full / locked: drop, never crash */ }
        }
    }

    /// <summary>Delete daily log files older than the retention period.</summary>
    public static void Cleanup(int keepDays)
    {
        if (keepDays <= 0) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppPaths.Logs, "av1-studio_*.log"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-keepDays)) File.Delete(f);
        }
        catch { /* best effort */ }
    }

    public static void Flush()
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (Pending.Count > 0 && DateTime.UtcNow < deadline) Thread.Sleep(20);
    }
}
