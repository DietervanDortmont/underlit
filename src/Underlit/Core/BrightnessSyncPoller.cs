using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using Underlit.Display;

namespace Underlit.Core;

/// <summary>
/// Polls Windows' native brightness (WMI for internal panels) on a low-priority
/// background tick. When the value differs from what Underlit last applied, we
/// forward it to the engine so Underlit resyncs — handling the case where the user
/// moved the Quick Settings slider or some other tool nudged brightness.
///
/// WMI reads are 20–80 ms each; we run them off the UI thread and only hop back
/// to the dispatcher with the result.
/// </summary>
public sealed class BrightnessSyncPoller : IDisposable
{
    private readonly UnderlitEngine _engine;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private bool _disposed;
    private bool _busy;

    public BrightnessSyncPoller(UnderlitEngine engine, Dispatcher dispatcher, int intervalMs)
    {
        _engine = engine;
        _dispatcher = dispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Clamp(intervalMs, 500, 30_000))
        };
        _timer.Tick += OnTick;
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    public void UpdateInterval(int intervalMs)
        => _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(intervalMs, 500, 30_000));

    private void OnTick(object? sender, EventArgs e)
    {
        if (_busy || _disposed) return;
        _busy = true;
        // Fire the WMI read on a thread-pool thread; hop back to UI to forward.
        Task.Run(() =>
        {
            try
            {
                var cur = WmiBrightness.TryGet();
                _dispatcher.BeginInvoke((Action)(() =>
                {
                    try
                    {
                        if (cur.HasValue) _engine.SyncFromExternalHardware(cur.Value);
                    }
                    finally { _busy = false; }
                }), DispatcherPriority.Background);
            }
            catch
            {
                _busy = false;
            }
        });
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
