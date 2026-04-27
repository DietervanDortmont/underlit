using System;
using System.Linq;
using System.Windows.Threading;
using Underlit.Core;
using Underlit.Display;
using Underlit.Input;
using Underlit.Settings;
using Underlit.Sys;
using Underlit.UI;
using Microsoft.Win32;
// WinForms is referenced (for NotifyIcon) so `Application` is ambiguous without a disambiguation.
using Application = System.Windows.Application;

namespace Underlit;

/// <summary>
/// Top-level "wire it all up" object. Owns the lifetimes of every subsystem.
/// Created from App.OnStartup after we know we're the single instance.
/// </summary>
public sealed class UnderlitHost : IDisposable
{
    private readonly Dispatcher _ui;
    private AppSettings _settings;

    private readonly GammaRampApplier _gamma = new();
    private readonly HardwareBrightness _hardware = new();
    private readonly OverlayManager _overlays = new();

    private UnderlitEngine? _engine;
    private Scheduler? _scheduler;
    private OsdWindow? _osd;
    private HotkeyManager? _hotkeys;
    private LowLevelKeyboardHook? _llHook;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private ForegroundAppWatcher? _fgWatcher;
    private BrightnessSyncPoller? _syncPoller;

    public UnderlitHost()
    {
        _ui = Application.Current.Dispatcher;
        _settings = AppSettings.Load();
    }

    public void Start()
    {
        Logger.Info("Underlit starting");

        // OSD first — it owns an HWND, which HotkeyManager pins RegisterHotKey against.
        _osd = new OsdWindow();
        _osd.Show();   // needed so SourceInitialized fires
        _osd.Hide();
        // Push initial visual settings (accent, transparency mode) down to the OSD.
        _osd.UpdateVisualSettings(
            _settings.FollowWindowsAccent,
            ParseColor(_settings.OsdAccentColor),
            _settings.TransparencyEffects,
            _settings.OsdBackdrop,
            GlassParamsFromSettings(_settings));

        // Engine
        _engine = new UnderlitEngine(_settings, _ui, _gamma, _overlays, _hardware);
        _engine.LevelChanged += OnEngineLevelChanged;
        _engine.RefreshDisplays(DisplayManager.Enumerate());

        // Seed from Windows' current brightness so our level matches the Quick Settings slider.
        // Only applies if a WMI-controllable panel exists and we're currently in (or above)
        // the positive range. If the user has us at a negative level (extended dim), leave it.
        if (_engine.CurrentBrightness >= 0)
        {
            var initial = WmiBrightness.TryGet();
            if (initial.HasValue) _engine.SyncFromExternalHardware(initial.Value);
        }

        // Poll Windows brightness periodically so the user's native Quick Settings
        // slider or Settings-app changes propagate into Underlit.
        _syncPoller = new BrightnessSyncPoller(_engine, _ui, _settings.ExternalSyncPollMs);
        _syncPoller.Start();

        // Scheduler
        _scheduler = new Scheduler(_settings, _ui);
        _scheduler.BaselineWarmthTick += (w) =>
        {
            // Schedule baselines only warmth now — brightness jumps from a schedule
            // felt disorienting. User overrides via hotkeys persist until the next tick.
            _engine.ApplyScheduleBaselineWarmth(w);
        };
        if (_settings.ScheduleEnabled) _scheduler.Start();

        // Hotkeys
        _hotkeys = new HotkeyManager(_osd.Source!);
        _hotkeys.Triggered += OnHotkeyTriggered;
        RegisterAllHotkeys();

        // Low-level hook (optional, per-settings)
        // When enabled, we take complete ownership of the Fn brightness keys:
        // we swallow the native key and call our own step logic, so Windows doesn't
        // pop its own OSD on top of ours and we don't get hardware write conflicts.
        if (_settings.HookNativeBrightnessKeys)
        {
            _llHook = new LowLevelKeyboardHook(_ui, swallowNativeKey: true);
            _llHook.BrightnessDown += OnNativeBrightnessDown;
            _llHook.BrightnessUp   += OnNativeBrightnessUp;
            _llHook.Install();
        }

        // Foreground watcher for exclusions
        _fgWatcher = new ForegroundAppWatcher(_ui,
            isEnabled: () => _settings.ExcludedProcessNames.Count > 0,
            isExcluded: name => _settings.ExcludedProcessNames.Any(x =>
                string.Equals(x, name, StringComparison.OrdinalIgnoreCase)));
        _fgWatcher.ExclusionStateChanged += paused =>
        {
            if (paused != (_engine?.Paused ?? false)) _engine?.TogglePaused();
        };
        _fgWatcher.Start();

        // Tray
        _tray = new TrayIcon();
        _tray.OpenSettings += OpenSettings;
        _tray.TogglePaused += () =>
        {
            _engine?.TogglePaused();
            _tray?.SetPausedLabel(_engine?.Paused == true);
            _osd?.ShowPaused(_engine?.Paused == true);
        };
        _tray.Quit += () => Application.Current.Shutdown();

        // Night Light
        if (_settings.DisableWindowsNightLight) NightLightControl.Disable();

        // Auto-start: the registry is the source of truth. If the installer wrote a Run
        // entry but our freshly-defaulted settings.json said StartWithWindows=false, we'd
        // otherwise delete the installer's entry on first launch. Sync the other way.
        bool registryHasAutoStart = AutoStart.IsEnabled();
        if (registryHasAutoStart != _settings.StartWithWindows)
        {
            _settings.StartWithWindows = registryHasAutoStart;
            _settings.Save();
        }

        // Listen for display changes (add/remove monitor, resolution change)
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;

        Logger.Info("Underlit started");
    }

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        _ui.BeginInvoke((Action)(() =>
        {
            try
            {
                _engine?.RefreshDisplays(DisplayManager.Enumerate());
            }
            catch (Exception ex)
            {
                Logger.Warn("Display-change refresh failed", ex);
            }
        }));
    }

    private void OnEngineLevelChanged(double brightness, int warmth)
    {
        _tray?.SetPausedLabel(_engine?.Paused == true);
    }

    // ---- Hotkey handling ----

    private void OnHotkeyTriggered(string name)
    {
        if (_engine == null) return;

        switch (name)
        {
            case "brDown":
                _engine.StepBrightness(-_settings.BrightnessStep);
                _osd?.ShowBrightness(_engine.CurrentBrightness);
                break;
            case "brUp":
                _engine.StepBrightness(+_settings.BrightnessStep);
                _osd?.ShowBrightness(_engine.CurrentBrightness);
                break;
            case "wrDown":
                _engine.StepWarmth(-_settings.WarmthStep);
                _osd?.ShowWarmth(_engine.CurrentWarmth);
                break;
            case "wrUp":
                _engine.StepWarmth(+_settings.WarmthStep);
                _osd?.ShowWarmth(_engine.CurrentWarmth);
                break;
            case "boost":
                _engine.Boost();
                _osd?.ShowBrightness(_engine.CurrentBrightness);
                break;
            case "toggle":
                _engine.TogglePaused();
                _osd?.ShowPaused(_engine.Paused);
                break;
        }
    }

    private void OnNativeBrightnessDown()
    {
        if (_engine == null) return;
        _engine.StepBrightness(-_settings.BrightnessStep);
        _osd?.ShowBrightness(_engine.CurrentBrightness);
    }

    private void OnNativeBrightnessUp()
    {
        if (_engine == null) return;
        _engine.StepBrightness(+_settings.BrightnessStep);
        _osd?.ShowBrightness(_engine.CurrentBrightness);
    }

    // ---- Settings UI ----

    private void OpenSettings()
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings, _engine?.Displays.ToList() ?? new(), _hardware);
        _settingsWindow.Applied += ApplySettings;
        _settingsWindow.Closed += (_, _) =>
        {
            _settings.Save();
            _settingsWindow = null;
        };
        _settingsWindow.Show();
    }

    private void ApplySettings(AppSettings next)
    {
        _settings = next;
        _engine?.UpdateSettings(next);
        _scheduler?.UpdateSettings(next);
        if (next.ScheduleEnabled) _scheduler?.Start(); else _scheduler?.Stop();
        _syncPoller?.UpdateInterval(next.ExternalSyncPollMs);

        // Hotkey re-registration
        if (_hotkeys != null) RegisterAllHotkeys();

        // Low-level hook on/off. When on we always swallow native brightness keys
        // so Windows' own OSD doesn't overlap ours and hardware writes don't collide.
        if (next.HookNativeBrightnessKeys && _llHook == null)
        {
            _llHook = new LowLevelKeyboardHook(_ui, swallowNativeKey: true);
            _llHook.BrightnessDown += OnNativeBrightnessDown;
            _llHook.BrightnessUp   += OnNativeBrightnessUp;
            _llHook.Install();
        }
        else if (!next.HookNativeBrightnessKeys && _llHook != null)
        {
            _llHook.Dispose();
            _llHook = null;
        }

        if (next.DisableWindowsNightLight) NightLightControl.Disable();

        AutoStart.Set(next.StartWithWindows);

        _osd?.UpdateVisualSettings(
            next.FollowWindowsAccent,
            ParseColor(next.OsdAccentColor),
            next.TransparencyEffects,
            next.OsdBackdrop,
            GlassParamsFromSettings(next));
    }

    private static Underlit.Sys.GlassParams GlassParamsFromSettings(AppSettings s) => new()
    {
        LightAngleDeg  = s.GlassLightAngleDeg,
        LightIntensity = s.GlassLightIntensity,
        Refraction     = s.GlassRefraction,
        Depth          = s.GlassDepth,
        Dispersion     = s.GlassDispersion,
        Frost          = s.GlassFrost,
        CornerRadius   = s.GlassCornerRadius,
        BevelWidth     = s.GlassBevelWidth,
        BodyCurvature  = s.GlassBodyCurvature,
    };

    /// <summary>Parse a "#AARRGGBB" or "#RRGGBB" hex string into a Color, or null.</summary>
    private static System.Windows.Media.Color? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex); }
        catch { return null; }
    }

    private void RegisterAllHotkeys()
    {
        if (_hotkeys == null) return;
        // Stepping hotkeys should repeat while held — holding brightness-down keeps dimming.
        TryRegister("brDown", _settings.HotkeyBrightnessDown, allowRepeat: true);
        TryRegister("brUp",   _settings.HotkeyBrightnessUp,   allowRepeat: true);
        TryRegister("wrDown", _settings.HotkeyWarmthDown,     allowRepeat: true);
        TryRegister("wrUp",   _settings.HotkeyWarmthUp,       allowRepeat: true);
        // Toggles must NOT repeat — a second fire within ~500 ms flips the state back, undoing the user's press.
        TryRegister("boost",  _settings.HotkeyBoost,          allowRepeat: false);
        TryRegister("toggle", _settings.HotkeyToggle,         allowRepeat: false);
    }

    private void TryRegister(string name, string hkStr, bool allowRepeat)
    {
        if (_hotkeys == null) return;
        try
        {
            var hk = Hotkey.Parse(hkStr);
            _hotkeys.Register(name, hk, allowRepeat);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Hotkey {name}={hkStr} invalid", ex);
        }
    }

    public void Dispose()
    {
        Logger.Info("Underlit shutting down");
        try { SystemEvents.DisplaySettingsChanged -= OnDisplayChanged; } catch { }
        _syncPoller?.Dispose();
        _fgWatcher?.Dispose();
        _llHook?.Dispose();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _scheduler?.Dispose();
        _engine?.Dispose();
        _overlays.Dispose();
        _gamma.Dispose();      // restores original gamma ramps
        _hardware.Dispose();
        _osd?.Close();
        try { _settings.Save(); } catch { }
    }
}
