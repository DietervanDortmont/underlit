using System;
using System.IO;

namespace Underlit;

/// <summary>
/// Tiny file logger. Writes to %LOCALAPPDATA%\Underlit\underlit.log. Safe from any thread.
/// </summary>
internal static class Logger
{
    private static readonly object _lock = new();
    private static readonly string _path;

    static Logger()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Underlit");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "underlit.log");
    }

    public static void Info(string message) => Write("INFO ", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN ", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (_lock)
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}";
                if (ex != null) line += Environment.NewLine + ex;
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Never let logging take down the app.
        }
    }
}
