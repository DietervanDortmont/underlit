using System;
using System.Diagnostics;
using System.Windows.Threading;
using Underlit.Display;

namespace Underlit.Sys;

/// <summary>
/// Polls the foreground window every 500ms to check its process name.
/// When it enters the exclusion list we raise Excluded(true); when it leaves, Excluded(false).
///
/// Polling is simple and doesn't require SetWinEventHook + marshalling. The tradeoff
/// is up to 500ms latency on focus change — fine for brightness transitions.
/// </summary>
public sealed class ForegroundAppWatcher : IDisposable
{
    public event Action<bool>? ExclusionStateChanged;
    public string? CurrentProcessName { get; private set; }
    public bool CurrentlyExcluded { get; private set; }

    private readonly DispatcherTimer _timer;
    private readonly Func<bool> _isEnabled;
    private readonly Func<string, bool> _isExcluded;

    public ForegroundAppWatcher(Dispatcher dispatcher, Func<bool> isEnabled, Func<string, bool> isExcluded)
    {
        _isEnabled = isEnabled;
        _isExcluded = isExcluded;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _timer.Tick += OnTick;
    }

    public void Start() => _timer.Start();
    public void Stop()  => _timer.Stop();

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_isEnabled())
        {
            if (CurrentlyExcluded) { CurrentlyExcluded = false; ExclusionStateChanged?.Invoke(false); }
            return;
        }

        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return;
            string? name;
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                name = proc.ProcessName; // no .exe suffix
            }
            catch { return; }

            CurrentProcessName = name;
            bool excluded = _isExcluded(name);
            if (excluded != CurrentlyExcluded)
            {
                CurrentlyExcluded = excluded;
                ExclusionStateChanged?.Invoke(excluded);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("ForegroundAppWatcher tick failed", ex);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
