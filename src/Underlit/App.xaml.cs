using System;
using System.Threading;
using System.Windows.Threading;
// Alias to disambiguate from System.Windows.Forms.Application — WinForms is referenced
// because the tray icon uses NotifyIcon, which pulls in its Application type too.
using Application = System.Windows.Application;
using StartupEventArgs = System.Windows.StartupEventArgs;
using ExitEventArgs = System.Windows.ExitEventArgs;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace Underlit;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private UnderlitHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single-instance guard. If another instance is already running, exit quietly.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, name: "Underlit.SingleInstance.C8D3F9B4", out var createdNew);
        if (!createdNew)
        {
            Shutdown(0);
            return;
        }

        // Catch-all so a background exception doesn't silently kill the app.
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            Logger.Error("AppDomain unhandled", ex.ExceptionObject as Exception);

        base.OnStartup(e);

        try
        {
            _host = new UnderlitHost();
            _host.Start();
        }
        catch (Exception ex)
        {
            Logger.Error("Startup failed", ex);
            MessageBox.Show("Underlit failed to start: " + ex.Message, "Underlit", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _host?.Dispose(); } catch { /* ignore on shutdown */ }
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* ignore */ }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("Dispatcher unhandled", e.Exception);
        // Don't crash on UI-thread exceptions — user can keep working, log the issue.
        e.Handled = true;
    }
}
