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
            GlassParamsFromSettings(_settings),
            _settings.GlassLiveCapture,
            _settings.OsdBarStyle,
            _settings.OsdBrightnessHighColor);

        // Engine
        _engine = new UnderlitEngine(_settings, _ui, _gamma, _overlays, _hardware);
        _engine.LevelChanged += OnEngineLevelChanged;
        _engine.RefreshDisplays(DisplayManager.Enumerate());

        // Mouse-drag from the OSD: the OSD raises BrightnessSetRequested with a
        // signed level (-100..+100) computed from the mouse X position; the engine
        // jumps to that value and the LevelChanged event flows back to update the
        // OSD bar in the same render pass.
        _osd.BrightnessSetRequested += signed => _engine.SetSignedBrightness(signed);

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

        // v0.6.12: the LowLevelKeyboardHook is no longer installed at start, even
        // if the saved setting is true. The hook used to match VK 0xAF/0xB0 thinking
        // they were brightness up/down — they're actually VK_VOLUME_UP and
        // VK_MEDIA_NEXT_TRACK, so the hook was stealing the user's volume keys and
        // remapping them to brightness changes. The hook itself is now a passthrough
        // (see LowLevelKeyboardHook.HookCallback) and there's no benefit to installing
        // it. Use the configurable RegisterHotKey hotkeys (Ctrl+Alt+Up/Down) instead.
        // Setting kept on disk for back-compat but ignored.
        _ = _settings.HookNativeBrightnessKeys;

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

        // Auto-start: two-step reconciliation between settings.json and the
        // HKCU\...\Run registry value.
        //
        //   1. INHERIT: if the installer just wrote a Run entry (typical first
        //      install) but our defaulted settings.json says StartWithWindows=false,
        //      we'd otherwise delete the installer's entry on first launch. So if
        //      registry and settings disagree, settings inherits the registry's
        //      truth and persists.
        //
        //   2. REFRESH: rewrite the registry to the CURRENT Environment.ProcessPath
        //      when enabled. This fixes the "Task Manager says enabled but boot
        //      doesn't actually launch the app" failure mode — caused by the
        //      registry value pointing at an old install location that no longer
        //      exists. AutoStart.Set logs the before/after so the user can confirm
        //      via %LOCALAPPDATA%\Underlit\underlit.log.
        bool registryHasAutoStart = AutoStart.IsEnabled();
        if (registryHasAutoStart != _settings.StartWithWindows)
        {
            _settings.StartWithWindows = registryHasAutoStart;
            _settings.Save();
        }
        Logger.Info($"AutoStart: settings={_settings.StartWithWindows}, registered={AutoStart.GetRegisteredCommand() ?? "<absent>"}, currentExe={AutoStart.GetCurrentExePath() ?? "<unknown>"}");
        AutoStart.Set(_settings.StartWithWindows);

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

    // OnNativeBrightnessDown/Up handlers removed in v0.6.12 — the LowLevelKeyboardHook
    // that called them was matching the wrong VK codes (volume + media instead of
    // brightness) and isn't installed anymore. Brightness control now flows entirely
    // through the configurable RegisterHotKey hotkeys (see RegisterAllHotkeys).

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

        // v0.6.12: setting ignored. See note in Start(). If a hook from before this
        // version is somehow still alive, dispose it now.
        if (_llHook != null)
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
            GlassParamsFromSettings(next),
            next.GlassLiveCapture,
            next.OsdBarStyle,
            next.OsdBrightnessHighColor);
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
        BevelDepthSliderValue = s.GlassBevelDepth,
        RimBrightness  = s.GlassRimBrightness,
        RimWidth       = s.GlassRimWidth,
        RimSecondary   = s.GlassRimSecondary,
        TintStrength   = s.GlassTintStrength,
        TintR          = ParseTintByte(s.GlassTintColor, 1, 255),
        TintG          = ParseTintByte(s.GlassTintColor, 2, 255),
        TintB          = ParseTintByte(s.GlassTintColor, 3, 255),
    };

    /// <summary>Pull a single channel byte (R=1, G=2, B=3) out of a "#AARRGGBB" string.</summary>
    private static byte ParseTintByte(string? hex, int channelIndex, byte fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            return channelIndex switch
            {
                1 => c.R,
                2 => c.G,
                3 => c.B,
                _ => fallback,
            };
        }
        catch { return fallback; }
    }

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
