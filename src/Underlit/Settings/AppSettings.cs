using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Underlit.Settings;

/// <summary>
/// Everything that survives an app restart. Stored as JSON under
/// %APPDATA%\Underlit\settings.json.
/// </summary>
public sealed class AppSettings
{
    // ---- Range configuration ----

    /// <summary>
    /// Signed brightness level, -100..100.
    ///   +100 = Windows' native maximum (hardware brightness 100%).
    ///      0 = Windows' native minimum (hardware brightness 0%).
    ///   -100 = Underlit's extended floor (software overlay + gamma at their max dimming).
    /// Above zero, this maps 1:1 to Windows' WMI/DDC-CI brightness. Below zero the
    /// hardware stays at its minimum and we dim further via gamma and overlay.
    /// </summary>
    public double BrightnessLevel { get; set; } = 100;

    /// <summary>Color temperature in Kelvin. 6500 = neutral, 3400 = warm evening, 1800 = deep night.</summary>
    public int WarmthKelvin { get; set; } = 6500;

    // ---- Hotkeys (string-format per Hotkey.ToString()) ----

    public string HotkeyBrightnessDown { get; set; } = "Ctrl+Alt+Down";
    public string HotkeyBrightnessUp   { get; set; } = "Ctrl+Alt+Up";
    public string HotkeyWarmthDown     { get; set; } = "Ctrl+Alt+Left";
    public string HotkeyWarmthUp       { get; set; } = "Ctrl+Alt+Right";
    public string HotkeyBoost          { get; set; } = "Ctrl+Alt+B";
    public string HotkeyToggle         { get; set; } = "Ctrl+Alt+Shift+D";

    /// <summary>Step size per hotkey press. Bigger = fewer keypresses to go min→max.</summary>
    public double BrightnessStep { get; set; } = 5;
    public int WarmthStep { get; set; } = 200;

    /// <summary>
    /// Try to intercept the laptop's Fn brightness keys via WH_KEYBOARD_LL.
    ///
    /// In practice this no longer does anything useful — modern laptop brightness
    /// keys go through HID consumer-page input that doesn't surface as WM_KEYDOWN,
    /// and our previous implementation matched VK 0xAF/0xB0 which are really volume
    /// up and media-next-track (see LowLevelKeyboardHook). Default is now FALSE so
    /// new installs don't even register the hook. Brightness should be controlled
    /// via the configurable RegisterHotKey hotkeys instead (Ctrl+Alt+Up/Down by
    /// default).
    /// </summary>
    public bool HookNativeBrightnessKeys { get; set; } = false;

    // ---- Behavior toggles ----

    /// <summary>
    /// If true, the *software* dim (overlay + gamma) fades smoothly between targets.
    /// Hardware brightness (WMI/DDC-CI) always snaps — Windows itself doesn't animate
    /// brightness and we want the same feel.
    /// </summary>
    public bool SmoothRamping { get; set; } = true;

    /// <summary>Ramp duration for software layers only. Hardware always snaps.</summary>
    public int RampDurationMs { get; set; } = 80;

    public bool DisableWindowsNightLight { get; set; } = true;

    public bool StartWithWindows { get; set; } = false;

    /// <summary>
    /// How often to poll Windows' native brightness and resync Underlit's level,
    /// so external changes (Quick Settings slider, Settings app, etc.) are picked up.
    /// </summary>
    public int ExternalSyncPollMs { get; set; } = 2000;

    // ---- Visual: accent color + transparency ----

    /// <summary>
    /// If true, the OSD's positive-fill (and similar accent surfaces) use Windows'
    /// current accent color, updating live as the user changes it. If false,
    /// <see cref="OsdAccentColor"/> overrides.
    /// </summary>
    public bool FollowWindowsAccent { get; set; } = true;

    /// <summary>Hex string like "#FF005FB8". Used only when <see cref="FollowWindowsAccent"/> is false.</summary>
    public string OsdAccentColor { get; set; } = "#FF005FB8";

    /// <summary>
    /// Whether transparency effects are allowed at all.
    ///   Auto = follow Windows' "Transparency effects" toggle (default).
    ///   On   = always on regardless of Windows setting.
    ///   Off  = always off, opaque-tinted background only.
    /// When Off, <see cref="OsdBackdrop"/> is ignored.
    /// </summary>
    public TransparencyMode TransparencyEffects { get; set; } = TransparencyMode.Auto;

    /// <summary>The visual style of the OSD background.</summary>
    public BackdropStyle OsdBackdrop { get; set; } = BackdropStyle.Subtle;

    /// <summary>
    /// Color the brightness fill transitions toward as the level approaches +100.
    ///   "auto"  — derive automatically from the accent (~55% RGB multiplier).
    ///   "#AARRGGBB" or "#RRGGBB" — specific user-chosen color.
    ///
    /// Below the transition midpoint (brightness ≤ 50) the fill is the accent
    /// color; above 50 it lerps from accent to this color, reaching it fully at
    /// brightness 100. Gives a "more brightness = deeper colour" cue.
    /// </summary>
    public string OsdBrightnessHighColor { get; set; } = "auto";

    /// <summary>
    /// How the brightness / warmth indicator inside the OSD pill is drawn.
    ///   Bar       — current default. Thin 4px slider with a track + small partial
    ///                fill that grows from the centre outward.
    ///   SolidFill — fills the OSD's bar column with a tall pill-shaped block whose
    ///                width grows with the level. No track, no mid-marker — the
    ///                fill IS the value indicator. Applies to both brightness and
    ///                warmth bars.
    /// </summary>
    public OsdBarStyle OsdBarStyle { get; set; } = OsdBarStyle.Bar;

    // ---- Liquid Glass tunable parameters (v0.3.2) ----
    // Slider conventions match the Figma "Glass" plugin so the user can hit values
    // they've already tuned visually elsewhere.
    public double GlassLightAngleDeg  { get; set; } = -45;
    public double GlassLightIntensity { get; set; } = 100;
    public double GlassRefraction     { get; set; } = 16;
    public double GlassDepth          { get; set; } = 50;
    public double GlassDispersion     { get; set; } = 0;
    public double GlassFrost          { get; set; } = 10;
    public double GlassCornerRadius   { get; set; } = 100;   // 0..100 (% of pillH/2)
    public double GlassBevelWidth     { get; set; } = 25;    // 0..100 (% of pillH/2 — bevel zone width)
    public double GlassBodyCurvature  { get; set; } = 50;    // legacy, unused in v0.5+
    public double GlassBevelDepth     { get; set; } = 35;    // 0..100 px (rim spike depth)
    public double GlassRimBrightness  { get; set; } = 250;   // 0..300 — thin rim-highlight brightness
    public double GlassRimWidth       { get; set; } = 50;    // 0..100 — thin rim-highlight band width
    public double GlassRimSecondary   { get; set; } = 50;    // 0..100 — opposing-corner rim highlight strength
    public string GlassTintColor      { get; set; } = "#FFFFFFFF"; // #AARRGGBB — tint hue (white = neutral)
    public double GlassTintStrength   { get; set; } = 6;     // 0..100 — how strong the tint mix is

    /// <summary>
    /// Glass capture mode.
    ///   true  (default): WGC live capture — glass refracts whatever moves behind the
    ///                    OSD in real time. On Win11 22H2/23H2 this triggers a brief
    ///                    yellow capture-border indicator while the OSD is visible.
    ///                    Win11 24H2+ supports a no-border mode.
    ///   false:           BitBlt one-shot capture per Show(). Glass freezes at the
    ///                    snapshot taken when the OSD appeared. No yellow border on
    ///                    any Windows build.
    /// </summary>
    public bool GlassLiveCapture { get; set; } = true;

    // ---- Schedule (optional baseline curve) ----

    public bool ScheduleEnabled { get; set; } = false;

    /// <summary>
    /// New (v0.6.23) simplified schedule input — the user gives us the two
    /// times that matter: when they go to bed and when they wake up. The
    /// circadian-derived ramp shapes (BedtimeStart, BedtimeEnd, WakeupStart,
    /// WakeupEnd below) are computed from these two values plus a typical
    /// physiology model. The legacy four-field set is kept derivable so
    /// <see cref="Underlit.Core.Scheduler.ComputeWarmth"/> keeps working
    /// unchanged.
    /// </summary>
    public TimeOfDay Bedtime  { get; set; } = new(23, 30);
    public TimeOfDay WakeTime { get; set; } = new(7, 30);

    /// <summary>
    /// Named warmth schedule profiles. One built-in "Recommended" profile is
    /// guaranteed; the user can add more (e.g. "Weekday", "Weekend") via the
    /// + button in Settings. Each profile holds its own Bedtime/WakeTime/Night
    /// values so swapping profiles instantly retargets the schedule curve.
    /// <see cref="ActiveProfileName"/> selects which one is in effect.
    /// </summary>
    public List<WarmthProfile> WarmthProfiles { get; set; } = new();
    public string ActiveProfileName { get; set; } = "Recommended";

    // Legacy 4-field schedule curve. Derived from Bedtime/WakeTime via
    // EnsureScheduleCurveDerived(); persisted for back-compat with existing
    // settings.json files and to keep Scheduler.ComputeWarmth's logic intact.
    public TimeOfDay BedtimeStart { get; set; } = new(21, 0);   // bed - 2.5 h
    public TimeOfDay BedtimeEnd   { get; set; } = new(23, 30);  // == Bedtime
    public TimeOfDay WakeupStart  { get; set; } = new(6, 45);   // wake - 0.75 h
    public TimeOfDay WakeupEnd    { get; set; } = new(7, 30);   // == WakeTime
    /// <summary>Night-floor warmth in K. Brightness is intentionally NOT on a schedule —
    /// sudden brightness changes during the day felt disorienting.</summary>
    public int NightWarmthKelvin { get; set; } = 2700;

    // ---- Schedule helpers ----

    /// <summary>
    /// Mirror the active profile's four ramp anchor times into the top-level
    /// fields the <see cref="Underlit.Core.Scheduler"/> reads. Each profile
    /// independently stores all four times — they're no longer derived from
    /// Bedtime/WakeTime alone, so the user can drag the onset points
    /// (BedtimeStart / WakeupStart) on the graph to widen or narrow the ramps.
    /// Call this after any profile-altering change.
    /// </summary>
    public void EnsureScheduleCurveDerived()
    {
        var p = ActiveProfile();
        BedtimeStart = p.BedtimeStart;
        BedtimeEnd   = p.Bedtime;
        WakeupStart  = p.WakeupStart;
        WakeupEnd    = p.WakeTime;
        Bedtime      = p.Bedtime;
        WakeTime     = p.WakeTime;
        NightWarmthKelvin = p.NightWarmthKelvin;
    }

    private static TimeOfDay ShiftHours(TimeOfDay t, double hours)
    {
        double h = t.AsHourFractional + hours;
        while (h < 0)  h += 24;
        while (h >= 24) h -= 24;
        int hh = (int)Math.Floor(h);
        int mm = (int)Math.Round((h - hh) * 60);
        if (mm >= 60) { hh = (hh + 1) % 24; mm = 0; }
        return new TimeOfDay(hh, mm);
    }

    /// <summary>
    /// Make sure the profile list contains the built-in "Recommended" entry and
    /// that <see cref="ActiveProfileName"/> resolves to a profile that exists.
    /// Safe to call repeatedly — idempotent.
    ///
    /// On first migration from a pre-profile version (Profile list empty), the
    /// Recommended profile is seeded from the user's existing top-level
    /// Bedtime / WakeTime / NightWarmthKelvin so their previous schedule is
    /// preserved instead of being replaced by hard-coded defaults.
    /// </summary>
    public void EnsureProfilesInitialized()
    {
        if (WarmthProfiles.Count == 0)
        {
            WarmthProfiles.Add(new WarmthProfile
            {
                Name = "Recommended",
                IsBuiltIn = true,
                Bedtime      = Bedtime,
                WakeTime     = WakeTime,
                BedtimeStart = ShiftHours(Bedtime, -2.5),
                WakeupStart  = ShiftHours(WakeTime, -0.75),
                NightWarmthKelvin = NightWarmthKelvin,
            });
        }
        else if (!WarmthProfiles.Any(p => p.IsBuiltIn && p.Name == "Recommended"))
        {
            // Existing profiles list but no built-in — add Recommended at index 0
            // with the science defaults.
            WarmthProfiles.Insert(0, new WarmthProfile
            {
                Name = "Recommended",
                IsBuiltIn = true,
                Bedtime      = new TimeOfDay(23, 30),
                WakeTime     = new TimeOfDay(7, 30),
                BedtimeStart = new TimeOfDay(21, 0),
                WakeupStart  = new TimeOfDay(6, 45),
                NightWarmthKelvin = 2700,
            });
        }

        // Validate / repair each profile's onset times so they sit before the
        // corresponding ramp end. If the JSON deserialised them as TimeOfDay()
        // (the all-zero struct default) the user got 00:00 which is nonsensical
        // for either onset; reset to the canonical 2.5h / 0.75h offsets.
        foreach (var p in WarmthProfiles)
        {
            if (p.BedtimeStart == default) p.BedtimeStart = ShiftHours(p.Bedtime, -2.5);
            if (p.WakeupStart  == default) p.WakeupStart  = ShiftHours(p.WakeTime, -0.75);
        }

        if (string.IsNullOrEmpty(ActiveProfileName)
            || WarmthProfiles.All(p => p.Name != ActiveProfileName))
        {
            ActiveProfileName = WarmthProfiles[0].Name;
        }
    }

    /// <summary>The currently-active profile, never null after EnsureProfilesInitialized.</summary>
    public WarmthProfile ActiveProfile()
    {
        EnsureProfilesInitialized();
        return WarmthProfiles.First(p => p.Name == ActiveProfileName);
    }

    // ---- Per-monitor offsets ----

    /// <summary>
    /// Optional per-monitor tweaks applied ON TOP of the global level.
    /// Keyed by DisplayInfo.StableId (\\.\DISPLAY1 etc).
    /// Offsets are additive (-30 = 30 points underlit than global).
    /// </summary>
    public Dictionary<string, PerMonitor> PerMonitor { get; set; } = new();

    // ---- Exclusion list ----

    /// <summary>Process executable names (case-insensitive, no path). When any is foreground, dimming is suspended.</summary>
    public List<string> ExcludedProcessNames { get; set; } = new() { };

    // ---- Persistence ----

    private static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Underlit", "settings.json");

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, _jsonOpts);
                if (loaded != null)
                {
                    // Migration v0.6.22 → v0.6.23: pre-upgrade settings only stored
                    // the legacy four-field ramp window (BedtimeStart/End,
                    // WakeupStart/End). The new model exposes Bedtime + WakeTime
                    // as the user-facing inputs and derives the ramp from them.
                    // If the legacy fields disagree with the (initializer-default)
                    // new fields, treat that as a sign the user customised their
                    // schedule and migrate Bedtime ← BedtimeEnd, WakeTime ← WakeupEnd.
                    if (Math.Abs(loaded.Bedtime.AsHourFractional - loaded.BedtimeEnd.AsHourFractional) > 0.01)
                        loaded.Bedtime = loaded.BedtimeEnd;
                    if (Math.Abs(loaded.WakeTime.AsHourFractional - loaded.WakeupEnd.AsHourFractional) > 0.01)
                        loaded.WakeTime = loaded.WakeupEnd;

                    loaded.EnsureProfilesInitialized();
                    loaded.EnsureScheduleCurveDerived();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Settings load failed, using defaults", ex);
        }
        var fresh = new AppSettings();
        fresh.EnsureProfilesInitialized();
        fresh.EnsureScheduleCurveDerived();
        return fresh;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, _jsonOpts));
        }
        catch (Exception ex)
        {
            Logger.Warn("Settings save failed", ex);
        }
    }
}

public sealed class PerMonitor
{
    public double BrightnessOffset { get; set; }
    public int WarmthOffsetKelvin { get; set; }
}

public readonly record struct TimeOfDay(int Hour, int Minute)
{
    public double AsHourFractional => Hour + Minute / 60.0;
}

/// <summary>
/// A named warmth-schedule profile. The user picks one from the dropdown in
/// Settings → Schedule; that profile's Bedtime/WakeTime/NightK gets pushed to
/// the active <see cref="AppSettings"/> fields and the curve is recomputed.
///
/// "Recommended" is a built-in profile that ships with the app and can't be
/// deleted (only modified). Subsequent profiles are user-added — clones from
/// whichever profile was active when the user clicked the + button.
/// </summary>
public sealed class WarmthProfile
{
    public string Name { get; set; } = "Profile";

    /// <summary>Start of the evening warming ramp (when warmth begins moving from
    /// neutral toward NightWarmthKelvin). Defaults to Bedtime − 2.5h.</summary>
    public TimeOfDay BedtimeStart { get; set; } = new(21, 0);

    /// <summary>End of the evening ramp — fully at NightWarmthKelvin.</summary>
    public TimeOfDay Bedtime  { get; set; } = new(23, 30);

    /// <summary>Start of the morning ramp (warmth begins moving from
    /// NightWarmthKelvin back toward neutral). Defaults to WakeTime − 0.75h.</summary>
    public TimeOfDay WakeupStart { get; set; } = new(6, 45);

    /// <summary>End of the morning ramp — fully at neutral 6500K.</summary>
    public TimeOfDay WakeTime { get; set; } = new(7, 30);

    public int NightWarmthKelvin { get; set; } = 2700;

    /// <summary>
    /// True for the "Recommended" preset that ships with the app — it can't be
    /// deleted via the − button. The user can still modify its values; if they
    /// ever want a clean slate they can clone it (+) and reset.
    /// </summary>
    public bool IsBuiltIn { get; set; }
}

public enum TransparencyMode
{
    Auto = 0,
    On   = 1,
    Off  = 2,
}

/// <summary>
/// Visual style for the OSD's background.
///   Solid        — opaque tinted background, dark/light theme aware.
///   Subtle       — Windows-style live frosted blur, dark/light tint, like Quick Settings.
///   LiquidGlass  — Apple-style multi-layer glass: live blur + specular highlight + edge sheen,
///                  theme-neutral (looks the same in dark and light).
/// </summary>
public enum BackdropStyle
{
    Solid       = 0,
    Subtle      = 1,
    LiquidGlass = 2,
}

/// <summary>
/// How the brightness/warmth bar inside the OSD pill is drawn. See AppSettings.OsdBarStyle.
/// </summary>
public enum OsdBarStyle
{
    Bar       = 0,
    SolidFill = 1,
}
