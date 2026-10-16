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
    private Underlit.Hue.HueController? _hueController;

    /// <summary>v0.6.55: ticks once a minute, fires the wind-down toast
    /// if the configured trigger time falls inside the just-elapsed
    /// minute. One-minute resolution is plenty for this use case and
    /// avoids burning CPU.</summary>
    private DispatcherTimer? _notifTimer;
    /// <summary>The minute we last fired the wind-down toast for, so a
    /// minute-spanning trigger window doesn't fire twice.</summary>
    private DateTime _lastWindDownFired = DateTime.MinValue;

    public UnderlitHost()
    {
        _ui = Application.Current.Dispatcher;
        _settings = AppSettings.Load();
    }

    public void Start()
    {
        Logger.Info("Underlit starting");

        // ============================================================
        // STEP 0 (sub-millisecond, runs FIRST): theme + wallpaper snap.
        //
        // v0.6.64: previously the Windows light/dark theme + wallpaper
        // didn't get applied until line ~173 of this method, after the
        // engine, Hue controller, sync poller, foreground watcher, tray,
        // notification timer, and AutoStart self-healing. On a cold
        // morning launch that meant the user stared at last night's
        // dark-red desktop for 5+ seconds while everything spun up.
        //
        // We compute the target kelvin straight from the schedule (no
        // engine needed) and call into the registry/SPI directly. The
        // engine + scheduler will reaffirm this once they exist; this
        // is just a "snap to the right mode immediately" pass.
        // ============================================================
        EarlyApplyThemeFromSchedule();

        // ============================================================
        // STEP 1 (critical path): OSD + engine + scheduler + hotkeys.
        //
        // Everything required for the user's first hotkey press to
        // actually do something. Tray, Hue, sync poller, AutoStart
        // self-heal, foreground watcher, and the notification timer
        // are all deferred to STEP 2 so the critical path stays lean.
        // ============================================================
        InitCriticalPath();

        // ============================================================
        // STEP 2 (deferred via Background priority): everything else.
        //
        // Posting at DispatcherPriority.Background lets the WPF render
        // thread paint the OSD's first frame before we start spinning
        // up Hue, polling WMI, and shelling out to schtasks.exe.
        // ============================================================
        _ui.BeginInvoke(new Action(InitDeferred), DispatcherPriority.Background);

        Logger.Info("Underlit started (critical path)");
    }

    /// <summary>v0.6.64: critical-path init. Anything required for the
    /// OSD to render and for hotkeys to fire goes here. Stays under
    /// ~50 ms on a warm SSD.</summary>
    private void InitCriticalPath()
    {
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
            _settings.OsdBrightnessHighColor,
            _settings.OsdGapAboveTaskbarDip);
        _osd.SetOsdShadow(_settings.OsdShadowOpacity, _settings.OsdShadowBlur,
                           _settings.OsdShadowOffset, _settings.OsdShadowAngle,
                           _settings.OsdShadowSpread);
        // v0.6.69: user-pickable sun (brightness) icon colour.
        var sunColor = ParseColor(_settings.OsdSunIconColor)
                       ?? System.Windows.Media.Color.FromArgb(0xFF, 0xFE, 0xC8, 0x0F);
        _osd.SetSunIconColor(sunColor);
        // v0.6.71: user-controlled bar right inset (the wider this is,
        // the narrower the brightness/warmth bar inside the pill).
        _osd.SetBarRightInset(_settings.OsdBarRightInsetDip);

        // Engine
        _engine = new UnderlitEngine(_settings, _ui, _gamma, _overlays, _hardware);
        _engine.LevelChanged += OnEngineLevelChanged;
        // v0.6.78: forward engine overlay opacity to the OSD so its
        // glass interior dims in lockstep with the desktop overlay.
        _engine.OverlayOpacityChanged += op => _osd?.SetDimOverlayLevel(op);
        // Seed the initial value so the OSD picks up an already-applied
        // overlay (e.g. extended dim that was active on startup) before
        // the engine fires its next ramp tick.
        _osd?.SetDimOverlayLevel(_engine.CurrentOverlayOpacity);
        _engine.RefreshDisplays(DisplayManager.Enumerate());

        // Mouse-drag from the OSD: the OSD raises BrightnessSetRequested with a
        // signed level (-100..+100) computed from the mouse X position; the engine
        // jumps to that value and the LevelChanged event flows back to update the
        // OSD bar in the same render pass.
        _osd.BrightnessSetRequested += signed => _engine!.SetSignedBrightness(signed);

        // Scheduler
        _scheduler = new Scheduler(_settings, _ui);
        _scheduler.BaselineWarmthTick += (w) =>
        {
            // Schedule baselines only warmth now — brightness jumps from a schedule
            // felt disorienting. User overrides via hotkeys persist until the next tick.
            _engine!.ApplyScheduleBaselineWarmth(w);
        };
        if (_settings.ScheduleEnabled) _scheduler.Start();

        // Hotkeys — register before any Show()/Hide() side-effects so the user
        // can press a key the instant Underlit's tray icon appears.
        _hotkeys = new HotkeyManager(_osd.Source!);
        _hotkeys.Triggered += OnHotkeyTriggered;
        RegisterAllHotkeys();
    }

    /// <summary>v0.6.64: deferred init. Hue controller, brightness sync,
    /// foreground watcher, tray icon, notification timer, AutoStart
    /// self-heal, and the first-run intro dialog. None of this is
    /// required for the first hotkey press to feel responsive.</summary>
    private void InitDeferred()
    {
        try
        {
            // Philips Hue controller — listens to engine.LevelChanged and pushes the
            // screen warmth (+ user offset, +/- boost handling) to the user's
            // selected Hue groups. Only does anything once a bridge is paired.
            _hueController = new Underlit.Hue.HueController(_engine!, _settings);
            // v0.7.0: reflect "N lights left alone" in the tray menu.
            _hueController.LightTakenOver += _ => _ui.BeginInvoke(new Action(UpdateTrayLightsState));

            // Seed from Windows' current brightness so our level matches the Quick Settings slider.
            // Only applies if a WMI-controllable panel exists and we're currently in (or above)
            // the positive range. If the user has us at a negative level (extended dim), leave it.
            if (_engine!.CurrentBrightness >= 0)
            {
                var initial = WmiBrightness.TryGet();
                if (initial.HasValue) _engine.SyncFromExternalHardware(initial.Value);
            }

            // Poll Windows brightness periodically so the user's native Quick Settings
            // slider or Settings-app changes propagate into Underlit.
            _syncPoller = new BrightnessSyncPoller(_engine, _ui, _settings.ExternalSyncPollMs);
            _syncPoller.Start();

            // v0.6.12: LowLevelKeyboardHook is intentionally not installed.
            // (See history note in the previous version of Start().) Setting
            // kept on disk for back-compat but ignored.
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
            _tray.ResumeLights += () =>
            {
                _hueController?.ResumeAll();
                UpdateTrayLightsState();
            };
            UpdateTrayLightsState();

            // v0.6.55: wind-down notification ticker. Cheap minute-resolution
            // poll - cheaper than wiring a calendar/cron and good enough for
            // a once-a-day reminder.
            _notifTimer = new DispatcherTimer(DispatcherPriority.Background, _ui)
            {
                Interval = TimeSpan.FromSeconds(30)
            };
            _notifTimer.Tick += (_, _) =>
            {
                CheckWindDownNotification();
                CheckRoutines();
                UpdateTrayLightsState();
                // v0.6.56: hammer the Windows theme on every tick so other
                // tools / Windows quirks can't outlast our preference.
                ApplyAutoThemeIfEnabled();
            };
            _notifTimer.Start();
            // v0.6.64: STEP 0 already snapped the theme before we got here,
            // but re-apply now that the engine exists so DoApplyTheme can
            // use the engine's actual TargetWarmth (which may differ from
            // the schedule by a few hundred K once user offsets are
            // factored in).
            ApplyAutoThemeIfEnabled();

            // Night Light
            if (_settings.DisableWindowsNightLight) NightLightControl.Disable();

            // Auto-start reconciliation. Two-step:
            //   1. INHERIT: if registry says yes but settings says no, settings wins
            //      false locally. So if they disagree, settings inherits from registry.
            //   2. REFRESH: rewrite the Run entry to the current ProcessPath, fixing
            //      the "Task Manager Enabled but boot doesn't launch" failure mode.
            bool registryHasAutoStart = AutoStart.IsEnabled();
            if (registryHasAutoStart != _settings.StartWithWindows)
            {
                _settings.StartWithWindows = registryHasAutoStart;
                _settings.Save();
            }
            Logger.Info("AutoStart diagnostics:\n" + AutoStart.GetDiagnostics());
            // v0.6.64: AutoStart.EnsureValid spawns schtasks.exe synchronously
            // (~250 ms hit). Bumping it into the deferred init means the user's
            // first hotkey doesn't wait on schtasks IPC.
            AutoStart.EnsureValid(_settings.StartWithWindows);

            // Listen for display changes (add/remove monitor, resolution change)
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;

            // v0.6.48: re-apply gamma when the user wakes from sleep. Otherwise
            // Windows resets the gamma ramp to neutral on resume, leaving the
            // screen at default warmth even though Underlit thinks it's
            // mid-schedule. v0.6.64: also re-applies the auto-theme.
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            // v0.6.48: first-run welcome dialog. Blocking-show so the user
            // sees the hotkey hints before they minimise the settings
            // window and forget Underlit exists. HasSeenIntro is persisted
            // on dismiss so this never appears twice for the same user.
            if (!_settings.HasSeenIntro)
            {
                try
                {
                    var intro = new UI.IntroWindow();
                    intro.ShowDialog();
                }
                catch (Exception ex) { Logger.Warn("Intro dialog failed to show", ex); }
                _settings.HasSeenIntro = true;
                _settings.Save();
            }

            Logger.Info("Underlit started (deferred init complete)");
        }
        catch (Exception ex) { Logger.Warn("InitDeferred failed", ex); }
    }

    /// <summary>v0.6.64: snap the Windows theme + desktop background to
    /// the schedule's current target kelvin BEFORE the engine exists.
    ///
    /// This is the very first thing Start() does so a cold-boot launch
    /// in the morning lands the user on the light-mode theme + bright
    /// wallpaper instantly, instead of staring at last night's dark-red
    /// desktop while the engine, Hue controller, and AutoStart self-heal
    /// each take their turn on the dispatcher.
    ///
    /// Computes warmth via <see cref="Scheduler.ComputeWarmth"/>, which
    /// is a pure function of the settings — no engine state required.
    /// If the schedule is disabled, falls back to the user's manual
    /// WarmthKelvin so we still pick a reasonable mode.</summary>
    private void EarlyApplyThemeFromSchedule()
    {
        if (!_settings.AutoSwitchWindowsTheme) return;
        try
        {
            int kelvin = _settings.ScheduleEnabled
                ? Underlit.Core.Scheduler.ComputeWarmth(DateTime.Now, _settings)
                : _settings.WarmthKelvin;
            kelvin = Math.Clamp(kelvin, 1500, 6500);
            int threshold = Math.Clamp(_settings.AutoThemeKelvinThreshold, 1500, 6500);
            bool light = kelvin >= threshold;
            var theme = light
                ? Underlit.Sys.WindowsThemeSwitcher.WindowsTheme.Light
                : Underlit.Sys.WindowsThemeSwitcher.WindowsTheme.Dark;
            Underlit.Sys.WindowsThemeSwitcher.Set(theme);
            ApplyBackgroundForMode(light);
            Logger.Info($"EarlyApplyThemeFromSchedule: kelvin={kelvin}, threshold={threshold}, mode={(light ? "Light" : "Dark")}");
        }
        catch (Exception ex) { Logger.Warn("EarlyApplyThemeFromSchedule failed", ex); }
    }

    /// <summary>
    /// v0.6.48: Windows resets the gamma ramp on resume from sleep, so we
    /// re-apply our current target on wake. Without this the screen
    /// reverts to neutral warmth even though Underlit thinks the schedule
    /// is mid-ramp.
    /// </summary>
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        _ui.BeginInvoke((Action)(() =>
        {
            try
            {
                _engine?.ReapplyAfterResume();
                // Re-tick the scheduler so the warmth catches up to the
                // current time-of-day if the laptop slept across a ramp.
                _scheduler?.Pulse();
                // v0.6.64: re-apply the Windows theme + wallpaper on wake.
                // Without this, a laptop that slept overnight wakes up
                // still in last-night's dark mode for up to 30 s (until
                // the next notification timer tick). Using the
                // non-debounced path so the snap is immediate.
                ApplyAutoThemeIfEnabled();
            }
            catch (Exception ex) { Logger.Warn("Resume re-apply failed", ex); }
        }));
    }

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        _ui.BeginInvoke((Action)(() =>
        {
            try
            {
                _engine?.RefreshDisplays(DisplayManager.Enumerate());
                // v0.6.48: tear down the OSD's live-glass capture too.
                // Its WGC item is bound to a specific HMONITOR; if that
                // monitor disconnected, the capture goes stale and the
                // glass shows a frozen frame from the dead display.
                // The next OSD show will lazy-recreate it against the
                // new primary monitor.
                _osd?.NotifyDisplaysChanged();
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
        // v0.6.57: was firing on every LevelChanged. While the user
        // dragged a schedule anchor that's 60+ Hz, which made Windows
        // visibly thrash between light and dark themes. Now debounced -
        // theme switch fires only after warmth is stable for 5 s.
        ApplyAutoThemeIfEnabled(debounce: true);
    }

    // ---- Hotkey handling ----

    private void OnHotkeyTriggered(string name)
    {
        if (_engine == null) return;

        switch (name)
        {
            case "brDown":
            {
                // v0.6.69: capture target before stepping so we can detect
                // a bounds hit (StepBrightness clamps internally; unchanged
                // target == we were already at the min/max). When we hit a
                // bound, fire the OSD's rubber-band pop in the direction
                // of pressure instead of a plain show.
                double beforeDown = _engine.TargetBrightness;
                _engine.StepBrightness(-_settings.BrightnessStep);
                // v0.6.48: show TARGET, not the mid-ramp rendered value,
                // so the OSD bar moves the moment the hotkey fires
                // instead of waiting for the gamma ramp to catch up.
                if (_engine.TargetBrightness < beforeDown)
                    _osd?.ShowBrightness(_engine.TargetBrightness);
                else
                    _osd?.ShowBrightnessAtBound(_engine.TargetBrightness, direction: -1);
                break;
            }
            case "brUp":
            {
                double beforeUp = _engine.TargetBrightness;
                _engine.StepBrightness(+_settings.BrightnessStep);
                if (_engine.TargetBrightness > beforeUp)
                    _osd?.ShowBrightness(_engine.TargetBrightness);
                else
                    _osd?.ShowBrightnessAtBound(_engine.TargetBrightness, direction: +1);
                break;
            }
            case "wrDown":
                _engine.StepWarmth(-_settings.WarmthStep);
                _osd?.ShowWarmth(_engine.TargetWarmth);
                break;
            case "wrUp":
                _engine.StepWarmth(+_settings.WarmthStep);
                _osd?.ShowWarmth(_engine.TargetWarmth);
                break;
            case "boost":
                _engine.Boost();
                _osd?.ShowBrightness(_engine.TargetBrightness);
                break;
            case "toggle":
                _engine.TogglePaused();
                _osd?.ShowPaused(_engine.Paused);
                break;

            // ---- Hue-specific (Phase 3+) ----
            // These adjust the Hue settings only — the screen state is unchanged.
            // After the setting tweak we PushNow() so the bridge update doesn't
            // wait for the next engine LevelChanged.
            case "hueBrDown":
                _settings.HueBrightness = Math.Clamp(_settings.HueBrightness - _settings.HueBrightnessStep, 1, 254);
                _hueController?.UpdateSettings(_settings);
                // v0.7.0: using Underlit's own Hue keys takes back any
                // bulb Google / the Hue app had taken over.
                _hueController?.ResumeAll();
                UpdateTrayLightsState();
                ScheduleSave();
                _settingsWindow?.SyncHueValuesFromHost(_settings);
                break;
            case "hueBrUp":
                _settings.HueBrightness = Math.Clamp(_settings.HueBrightness + _settings.HueBrightnessStep, 1, 254);
                _hueController?.UpdateSettings(_settings);
                // v0.7.0: using Underlit's own Hue keys takes back any
                // bulb Google / the Hue app had taken over.
                _hueController?.ResumeAll();
                UpdateTrayLightsState();
                ScheduleSave();
                _settingsWindow?.SyncHueValuesFromHost(_settings);
                break;
            case "hueWrDown":
                // "Down" = warmer (lower kelvin) — match the screen warmth convention.
                _settings.HueWarmthOffsetKelvin = Math.Clamp(
                    _settings.HueWarmthOffsetKelvin - _settings.HueWarmthStep, -3000, 3000);
                _hueController?.UpdateSettings(_settings);
                // v0.7.0: using Underlit's own Hue keys takes back any
                // bulb Google / the Hue app had taken over.
                _hueController?.ResumeAll();
                UpdateTrayLightsState();
                ScheduleSave();
                _settingsWindow?.SyncHueValuesFromHost(_settings);
                break;
            case "hueWrUp":
                _settings.HueWarmthOffsetKelvin = Math.Clamp(
                    _settings.HueWarmthOffsetKelvin + _settings.HueWarmthStep, -3000, 3000);
                _hueController?.UpdateSettings(_settings);
                // v0.7.0: using Underlit's own Hue keys takes back any
                // bulb Google / the Hue app had taken over.
                _hueController?.ResumeAll();
                UpdateTrayLightsState();
                ScheduleSave();
                _settingsWindow?.SyncHueValuesFromHost(_settings);
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
        // Live-preview while the user drags an anchor in the schedule graph.
        // Engine.PreviewWarmth ramps the screen toward the dragged point's
        // kelvin without persisting; EndWarmthPreview hands control back to
        // the saved warmth (and the scheduler reasserts on its next tick).
        _settingsWindow.WarmthPreviewRequested += k => _engine?.PreviewWarmth(k);
        _settingsWindow.WarmthPreviewEnded     += () => _engine?.EndWarmthPreview();
        // Live-preview the OSD whenever the user nudges a glass slider — push
        // the OSD back into view (or restart its hide-timer if already up) so
        // the effect of the slider change is immediately visible.
        _settingsWindow.GlassPreviewRequested  += () =>
            _osd?.ShowBrightness(_engine?.CurrentBrightness ?? 100);
        // v0.6.56: live OSD preview pin. While the user is interacting
        // with an OSD-affecting control in Settings, keep the OSD on
        // screen so they can see the live effect of slider tweaks.
        _settingsWindow.OsdPreviewPinChanged += pinned =>
        {
            _osd?.SetPinned(pinned);
            if (pinned) _osd?.ShowBrightness(_engine?.CurrentBrightness ?? 100);
        };
        // v0.6.53: 24h cycle simulator. SettingsWindow drives the timer
        // (so it can also animate the now-line on the schedule graph);
        // we just suspend the regular Scheduler while it runs and apply
        // each synthesized warmth value through the engine's normal
        // baseline-warmth path so it fans out to gamma + Hue exactly
        // like a real schedule tick would.
        _settingsWindow.SimulationStarted    += () =>
        {
            _scheduler?.Stop();
            _suppressAutoThemeForSimulation = true;
            // v0.6.60: tell the Hue controller it's in sim mode so per-
            // profile slices use the engine's synthesized warmth instead
            // of real wall-clock time. Otherwise profile bulbs sit
            // frozen at the real-time kelvin while default bulbs sweep
            // through the sim - jarring.
            if (_hueController != null) _hueController.IsSimulating = true;
        };
        _settingsWindow.SimulationWarmthAt   += w  => _engine?.ApplyScheduleBaselineWarmth(w);
        // v0.7.0: time-of-day light stops follow the simulated clock.
        _settingsWindow.SimulationTimeAt     += t  =>
        {
            if (_hueController == null) return;
            _hueController.SimulatedNow = t;
            if (_settings.HueColorSource == HueColorSource.TimeOfDay) _hueController.PushNow();
        };
        _settingsWindow.HuePreviewActiveChanged += active =>
        {
            if (_hueController != null) _hueController.PausedForPreview = active;
            UpdateTrayLightsState();
        };
        _settingsWindow.HueTakeBackRequested += () =>
        {
            _hueController?.ResumeAll();
            UpdateTrayLightsState();
        };
        _settingsWindow.HueResumeRequested   += () =>
        {
            _hueController?.ResumeAll(turnOn: true);
            UpdateTrayLightsState();
        };
        _settingsWindow.SimulationEnded      += () =>
        {
            if (_settings.ScheduleEnabled)
            {
                _scheduler?.Start();
                _scheduler?.Pulse();
            }
            if (_hueController != null)
            {
                _hueController.IsSimulating = false;
                _hueController.SimulatedNow = null;
                _hueController.PushNow();
            }
            // v0.6.62: extend the suppression for ~10 s after the sim
            // ends. The post-sim Pulse() fires LevelChanged with the
            // real-time warmth, which arms the 5 s debounce. Without
            // this grace, the theme can flip "randomly" several seconds
            // after the simulation is visibly done. Letting the engine
            // settle before unsuppressing avoids that.
            var graceTimer = new DispatcherTimer(DispatcherPriority.Background, _ui)
            {
                Interval = TimeSpan.FromSeconds(10)
            };
            graceTimer.Tick += (_, _) =>
            {
                graceTimer.Stop();
                _suppressAutoThemeForSimulation = false;
                ApplyAutoThemeIfEnabled();
            };
            graceTimer.Start();
        };
        _settingsWindow.Closed += (_, _) =>
        {
            // v0.6.56: ensure OSD pin is released even if the user closes
            // Settings without un-focusing the slider they were tweaking.
            _osd?.SetPinned(false);
        };
        _settingsWindow.Closed += (_, _) =>
        {
            _settings.Save();
            _settingsWindow = null;
        };
        _settingsWindow.Show();
    }

    /// <summary>v0.7.0: settings used to be written only when the Settings
    /// window closed or the app shut down cleanly, so a crash, a forced
    /// kill or a dev-watch rebuild lost every change. Now they're saved
    /// ~1 s after the last change.</summary>
    private DispatcherTimer? _saveDebounce;

    private void ScheduleSave()
    {
        if (_saveDebounce == null)
        {
            _saveDebounce = new DispatcherTimer(DispatcherPriority.Background, _ui)
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            _saveDebounce.Tick += (_, _) =>
            {
                _saveDebounce!.Stop();
                _settings.Save();
            };
        }
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private void ApplySettings(AppSettings next)
    {
        bool wasScheduled = _settings.ScheduleEnabled;
        _settings = next;
        ScheduleSave();
        _engine?.UpdateSettings(next);
        _scheduler?.UpdateSettings(next);
        if (next.ScheduleEnabled) _scheduler?.Start(); else _scheduler?.Stop();
        // v0.6.43: when the user TURNS OFF the schedule, drop any
        // accumulated warmth offset — manual mode owns the warmth
        // outright from here on, so a stale offset would just cause a
        // surprising jump if they later re-enabled the schedule.
        if (wasScheduled && !next.ScheduleEnabled)
        {
            _engine?.ResetUserWarmthOffset();
        }
        _syncPoller?.UpdateInterval(next.ExternalSyncPollMs);
        _hueController?.UpdateSettings(next);

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

        // v0.6.50: write through both Run-key and Startup-folder shortcut.
        AutoStart.EnsureValid(next.StartWithWindows);

        _osd?.UpdateVisualSettings(
            next.FollowWindowsAccent,
            ParseColor(next.OsdAccentColor),
            next.TransparencyEffects,
            next.OsdBackdrop,
            GlassParamsFromSettings(next),
            next.GlassLiveCapture,
            next.OsdBarStyle,
            next.OsdBrightnessHighColor,
            next.OsdGapAboveTaskbarDip);
        // v0.6.57: universal OSD shadow. v0.6.64: + spread.
        _osd?.SetOsdShadow(next.OsdShadowOpacity, next.OsdShadowBlur,
                            next.OsdShadowOffset, next.OsdShadowAngle,
                            next.OsdShadowSpread);
        // v0.6.69: sun icon colour.
        var sunColor = ParseColor(next.OsdSunIconColor)
                       ?? System.Windows.Media.Color.FromArgb(0xFF, 0xFE, 0xC8, 0x0F);
        _osd?.SetSunIconColor(sunColor);
        // v0.6.71: bar right inset.
        _osd?.SetBarRightInset(next.OsdBarRightInsetDip);
    }

    /// <summary>v0.6.57: debounce timer for auto-theme. Restarted on each
    /// LevelChanged so the theme only flips after the warmth has been
    /// stable for the configured quiet window. Keeps the user from
    /// seeing dark/light flicker while dragging schedule anchors.</summary>
    private DispatcherTimer? _autoThemeDebounce;
    /// <summary>v0.6.57: how long the warmth must be stable before the
    /// theme switch fires from a LevelChanged event. The 30 s timer tick
    /// continues to apply unconditionally (so a missed theme set or an
    /// out-of-band change still gets corrected within 30 s).</summary>
    private const int AutoThemeDebounceMs = 5000;

    /// <summary>v0.6.56: re-apply the Windows theme based on the current
    /// schedule warmth. Called from the notification timer tick AND from
    /// the engine's LevelChanged callback so the theme switches the
    /// instant the schedule crosses the threshold rather than waiting
    /// up to 30 s. v0.6.57: debounced when called from LevelChanged so
    /// schedule-graph dragging doesn't repeatedly flip the theme.</summary>
    /// <summary>v0.6.58: short-circuit auto-theme during the schedule
    /// simulator (a 24h playback in 10 s would otherwise rapidly flip
    /// the shell light/dark theme).</summary>
    private bool _suppressAutoThemeForSimulation;

    private void ApplyAutoThemeIfEnabled(bool debounce = false)
    {
        if (!_settings.AutoSwitchWindowsTheme) return;
        if (_engine == null) return;
        if (_suppressAutoThemeForSimulation) return;

        if (debounce)
        {
            if (_autoThemeDebounce == null)
            {
                _autoThemeDebounce = new DispatcherTimer(DispatcherPriority.Background, _ui)
                {
                    Interval = TimeSpan.FromMilliseconds(AutoThemeDebounceMs)
                };
                _autoThemeDebounce.Tick += (_, _) =>
                {
                    _autoThemeDebounce!.Stop();
                    DoApplyTheme();
                };
            }
            _autoThemeDebounce.Stop();
            _autoThemeDebounce.Start();
            return;
        }
        DoApplyTheme();
    }

    private void DoApplyTheme()
    {
        try
        {
            int kelvin = _engine?.TargetWarmth ?? 6500;
            int threshold = Math.Clamp(_settings.AutoThemeKelvinThreshold, 1500, 6500);
            bool light = kelvin >= threshold;
            var desired = light
                ? Underlit.Sys.WindowsThemeSwitcher.WindowsTheme.Light
                : Underlit.Sys.WindowsThemeSwitcher.WindowsTheme.Dark;
            Underlit.Sys.WindowsThemeSwitcher.Set(desired);

            // v0.6.63: matching wallpaper / solid-colour swap.
            ApplyBackgroundForMode(light);
        }
        catch (Exception ex) { Logger.Warn("DoApplyTheme failed", ex); }
    }

    /// <summary>v0.6.63: change the desktop background according to the
    /// settings for the active mode. Driven from DoApplyTheme so wallpaper
    /// swaps live alongside the light/dark theme transition.</summary>
    private void ApplyBackgroundForMode(bool light)
    {
        var choice = light ? _settings.LightBackgroundChoice : _settings.DarkBackgroundChoice;
        if (choice == BackgroundChoice.None) return;

        if (choice == BackgroundChoice.Image)
        {
            string path = light ? _settings.LightBackgroundImagePath : _settings.DarkBackgroundImagePath;
            if (!string.IsNullOrWhiteSpace(path))
                Underlit.Sys.WindowsWallpaperSwitcher.SetImage(path);
            return;
        }
        // SolidColor
        string hex = light ? _settings.LightBackgroundColor : _settings.DarkBackgroundColor;
        var (r, g, b) = ParseHexRgb(hex);
        Underlit.Sys.WindowsWallpaperSwitcher.SetSolidColor(r, g, b);
    }

    private static (byte r, byte g, byte b) ParseHexRgb(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#') return (0, 0, 0);
        try
        {
            int off = hex.Length == 9 ? 3 : 1;
            byte r = Convert.ToByte(hex.Substring(off, 2), 16);
            byte g = Convert.ToByte(hex.Substring(off + 2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(off + 4, 2), 16);
            return (r, g, b);
        }
        catch { return (0, 0, 0); }
    }

    /// <summary>v0.6.55: per-routine "last fired" date so we don't
    /// double-fire if the timer ticks twice in the trigger window.</summary>
    private readonly System.Collections.Generic.Dictionary<string, DateTime> _routineLastFired
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>v0.6.55: pending TurnOnFor expirations. Key = routine
    /// id, value = wall-clock time at which we should send "off" to
    /// that routine's targets. Cleared when the off fires.</summary>
    private readonly System.Collections.Generic.Dictionary<string, DateTime> _routinePendingOff
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>v0.6.55: minute-resolution scan of the user's routines.
    /// Same minute-tolerance + per-day dedupe trick as the wind-down
    /// notification; reuses the host's existing timer tick.</summary>
    /// <summary>v0.7.0: tray label for lights Google / the Hue app took over.</summary>
    private void UpdateTrayLightsState()
    {
        try { _tray?.SetLightsOverridden(_hueController?.OverriddenCount ?? 0); }
        catch (Exception ex) { Logger.Warn("UpdateTrayLightsState failed", ex); }
    }

    private void CheckRoutines()
    {
        if (_settings.Routines == null || _settings.Routines.Count == 0) return;
        DateTime now = DateTime.Now;

        foreach (var r in _settings.Routines)
        {
            if (!r.Enabled) continue;
            if (!RoutineMatchesDay(r, now.DayOfWeek)) continue;

            DateTime trigger = ComputeRoutineTrigger(r, now);
            // Past or upcoming, with a window to absorb timer jitter.
            double seconds = (now - trigger).TotalSeconds;
            if (seconds < -15 || seconds > 45) continue;

            // Dedupe per calendar date.
            if (_routineLastFired.TryGetValue(r.Id, out var last) && last.Date == now.Date)
                continue;

            _routineLastFired[r.Id] = now;
            _ = FireRoutineAsync(r);
        }

        // Pending TurnOnFor expirations.
        foreach (var kv in _routinePendingOff.ToArray())
        {
            if (now < kv.Value) continue;
            _routinePendingOff.Remove(kv.Key);
            var r = _settings.Routines.FirstOrDefault(x => x.Id == kv.Key);
            if (r != null) _ = FireRoutineOffAsync(r);
        }
    }

    private static bool RoutineMatchesDay(Routine r, DayOfWeek dow)
    {
        var bit = dow switch
        {
            DayOfWeek.Sunday    => RoutineDays.Sunday,
            DayOfWeek.Monday    => RoutineDays.Monday,
            DayOfWeek.Tuesday   => RoutineDays.Tuesday,
            DayOfWeek.Wednesday => RoutineDays.Wednesday,
            DayOfWeek.Thursday  => RoutineDays.Thursday,
            DayOfWeek.Friday    => RoutineDays.Friday,
            DayOfWeek.Saturday  => RoutineDays.Saturday,
            _                   => RoutineDays.None,
        };
        return (r.Days & bit) != 0;
    }

    private DateTime ComputeRoutineTrigger(Routine r, DateTime now)
    {
        DateTime baseTime;
        var profile = _settings.ActiveProfile();
        switch (r.TriggerKind)
        {
            case RoutineTriggerKind.RelativeToWakeup:
                baseTime = DateTime.Today.AddHours(profile.WakeupStart.Hour).AddMinutes(profile.WakeupStart.Minute);
                return baseTime.AddMinutes(r.TriggerOffsetMinutes);
            case RoutineTriggerKind.RelativeToBedtime:
                baseTime = DateTime.Today.AddHours(profile.Bedtime.Hour).AddMinutes(profile.Bedtime.Minute);
                return baseTime.AddMinutes(r.TriggerOffsetMinutes);
            default: // AbsoluteTime
                return DateTime.Today.AddHours(r.TriggerHour).AddMinutes(r.TriggerMinute);
        }
    }

    private async System.Threading.Tasks.Task FireRoutineAsync(Routine r)
    {
        try
        {
            Logger.Info($"Routine: firing '{r.Name}' (action={r.Action}, lights={r.TargetLightIds.Count}, groups={r.TargetGroupIds.Count})");
            switch (r.Action)
            {
                case RoutineAction.TurnOn:
                    await PushRoutineState(r, on: true);
                    break;
                case RoutineAction.TurnOff:
                    await PushRoutineState(r, on: false);
                    break;
                case RoutineAction.TurnOnFor:
                    await PushRoutineState(r, on: true);
                    _routinePendingOff[r.Id] = DateTime.Now.AddMinutes(Math.Max(1, r.DurationMinutes));
                    break;
            }
        }
        catch (Exception ex) { Logger.Warn($"Routine '{r.Name}' fire failed", ex); }
    }

    private async System.Threading.Tasks.Task FireRoutineOffAsync(Routine r)
    {
        try
        {
            Logger.Info($"Routine: TurnOnFor expiry for '{r.Name}' - sending off");
            await PushRoutineState(r, on: false);
        }
        catch (Exception ex) { Logger.Warn($"Routine '{r.Name}' off-expiry failed", ex); }
    }

    /// <summary>Apply a routine's on/off state (with optional CT/bri) to
    /// every selected target. Routines bypass the per-bulb fade-out
    /// logic - they're imperative actions, not schedule-following.</summary>
    private async System.Threading.Tasks.Task PushRoutineState(Routine r, bool on)
    {
        // Need bridge access. Borrow the controller's client via the
        // host's settings if available; otherwise no-op.
        if (string.IsNullOrEmpty(_settings.HueBridgeIp) || string.IsNullOrEmpty(_settings.HueBridgeUsername))
            return;
        using var client = new Underlit.Hue.HueBridgeClient(_settings.HueBridgeIp!, _settings.HueBridgeUsername);

        int? mireds = on && r.Kelvin.HasValue
            ? Underlit.Hue.HueBridgeClient.KelvinToMireds(r.Kelvin.Value)
            : (int?)null;
        int? bri = on && r.BrightnessPercent.HasValue
            ? Math.Clamp((int)Math.Round(r.BrightnessPercent.Value * 254.0 / 100.0), 1, 254)
            : (int?)null;

        var tasks = new System.Collections.Generic.List<System.Threading.Tasks.Task>();
        foreach (var lid in r.TargetLightIds)
            tasks.Add(SafeBoolTask(client.SetLightStateAsync(lid, on: on, mireds: mireds, brightness254: bri)));
        foreach (var gid in r.TargetGroupIds)
            tasks.Add(SafeBoolTask(client.SetGroupStateAsync(gid, on: on, mireds: mireds, brightness254: bri)));
        await System.Threading.Tasks.Task.WhenAll(tasks);

        static async System.Threading.Tasks.Task SafeBoolTask(System.Threading.Tasks.Task<bool> t)
        {
            try { await t; } catch { /* swallow per-bulb errors */ }
        }
    }

    /// <summary>v0.6.55: minute-resolution check for the wind-down toast.
    /// Computes the trigger time as (active profile bedtime - configured
    /// minutes), and if "now" is within +/-30s of it AND we haven't fired
    /// today, surfaces a tray balloon. Uses calendar dates to dedupe so
    /// rebooting your PC at the right time doesn't double-fire.</summary>
    private void CheckWindDownNotification()
    {
        if (!_settings.WindDownNotificationEnabled) return;
        if (_tray == null) return;

        try
        {
            var profile = _settings.ActiveProfile();
            int minutesBefore = Math.Clamp(_settings.WindDownNotificationMinutesBeforeBedtime, 0, 240);

            DateTime now = DateTime.Now;
            DateTime bedtimeToday = DateTime.Today
                .AddHours(profile.Bedtime.Hour)
                .AddMinutes(profile.Bedtime.Minute);
            DateTime trigger = bedtimeToday.AddMinutes(-minutesBefore);
            // If the bedtime is "early in the morning" relative to now
            // (e.g. user set bedtime to 01:00 and it's currently 23:00),
            // shift to tomorrow so the trigger doesn't fire 22 hours late.
            if (trigger < now.AddHours(-12)) trigger = trigger.AddDays(1);

            // Only fire when we cross into the trigger minute. Tolerance
            // window of [-15s, +45s] covers the 30s timer tick boundary.
            double seconds = (now - trigger).TotalSeconds;
            if (seconds < -15 || seconds > 45) return;

            // Already fired in this calendar minute? Skip.
            if (_lastWindDownFired.Date == now.Date
                && _lastWindDownFired.Hour == trigger.Hour
                && _lastWindDownFired.Minute == trigger.Minute)
                return;

            _lastWindDownFired = trigger;
            string body = string.IsNullOrWhiteSpace(_settings.WindDownNotificationMessage)
                ? "Wind-down time."
                : _settings.WindDownNotificationMessage;
            _tray.ShowBalloon("Underlit", body, timeoutMs: 8000);
            Logger.Info($"WindDown: balloon fired at {now:HH:mm} (trigger={trigger:HH:mm})");
        }
        catch (Exception ex) { Logger.Warn("CheckWindDownNotification failed", ex); }
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
        // Hue-specific hotkeys — adjust HueBrightness or HueWarmthOffsetKelvin
        // independently of the screen.
        TryRegister("hueBrDown", _settings.HotkeyHueBrightnessDown, allowRepeat: true);
        TryRegister("hueBrUp",   _settings.HotkeyHueBrightnessUp,   allowRepeat: true);
        TryRegister("hueWrDown", _settings.HotkeyHueWarmthDown,     allowRepeat: true);
        TryRegister("hueWrUp",   _settings.HotkeyHueWarmthUp,       allowRepeat: true);
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
        try { SystemEvents.PowerModeChanged       -= OnPowerModeChanged; } catch { }
        _syncPoller?.Dispose();
        _fgWatcher?.Dispose();
        _llHook?.Dispose();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _scheduler?.Dispose();
        _hueController?.Dispose();
        _engine?.Dispose();
        _overlays.Dispose();
        _gamma.Dispose();      // restores original gamma ramps
        _hardware.Dispose();
        _osd?.Close();
        try { _settings.Save(); } catch { }
    }
}
