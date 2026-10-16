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
        RotateIfLarge();
    }

    /// <summary>Keep the log from growing forever (it reached 229 MB
    /// before this existed). When the file passes <see cref="MaxBytes"/>
    /// it is moved to underlit.old.log (replacing any previous one), so
    /// at most ~2x MaxBytes is kept on disk. Checked at startup and every
    /// <see cref="RotateCheckEvery"/> writes.</summary>
    private const long MaxBytes = 5L * 1024 * 1024;
    private const int RotateCheckEvery = 500;
    private static int _writesSinceCheck;

    private static void RotateIfLarge()
    {
        try
        {
            var fi = new FileInfo(_path);
            if (!fi.Exists || fi.Length < MaxBytes) return;
            var old = Path.Combine(fi.DirectoryName!, "underlit.old.log");
            File.Move(_path, old, overwrite: true);
        }
        catch
        {
            // Rotation is best-effort.
        }
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
                if (++_writesSinceCheck >= RotateCheckEvery)
                {
                    _writesSinceCheck = 0;
                    RotateIfLarge();
                }
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
