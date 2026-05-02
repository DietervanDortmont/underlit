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

    /// <summary>
    /// Vertical distance, in DIP, between the bottom of the OSD pill and the
    /// top edge of the taskbar. Default 30 dip — slightly higher than the
    /// v0.6.34 hard-coded 10 dip so the pill doesn't visually crowd the
    /// taskbar. Range 0..200 via the Settings slider.
    /// </summary>
    public int OsdGapAboveTaskbarDip { get; set; } = 30;

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

    // Legacy 4-field schedule curve. Mirrored from the active profile by
    // EnsureScheduleCurveDerived(); persisted for back-compat with existing
    // settings.json files and so Scheduler.ComputeWarmth can read the curve
    // directly off AppSettings without a profile lookup per call.
    public TimeOfDay BedtimeStart { get; set; } = new(21, 0);   // bed - 2.5 h
    public TimeOfDay BedtimeEnd   { get; set; } = new(23, 30);  // == Bedtime
    public TimeOfDay WakeupStart  { get; set; } = new(6, 45);   // wake - 0.75 h
    public TimeOfDay WakeupEnd    { get; set; } = new(7, 30);   // == WakeTime

    // Per-anchor kelvin values, also mirrored from the active profile. Each
    // point on the schedule curve has its own kelvin so the user can drag any
    // anchor up/down on the graph to retarget warmth at that boundary.
    public int BedtimeStartKelvin { get; set; } = 6500;
    public int BedtimeEndKelvin   { get; set; } = 2700;
    public int WakeupStartKelvin  { get; set; } = 2700;
    public int WakeupEndKelvin    { get; set; } = 6500;

    /// <summary>Legacy "warmth at night" value — the v0.6.24 single-kelvin field.
    /// Still exposed because the Settings UI's "Warmth at night" slider keeps
    /// both deep-warmth anchors (BedtimeEndKelvin + WakeupStartKelvin) in
    /// lockstep through this setter.</summary>
    public int NightWarmthKelvin { get; set; } = 2700;

    // ---- Philips Hue integration (v0.6.27) ----

    /// <summary>LAN IP of the user's Hue Bridge, set after a successful pairing
    /// in Settings → Lights. Null when no bridge is paired.</summary>
    public string? HueBridgeIp { get; set; }

    /// <summary>The username/key returned by the bridge during pairing. Acts as
    /// the bearer token for every subsequent API call. Treat as a secret.</summary>
    public string? HueBridgeUsername { get; set; }

    /// <summary>Group ids selected for Underlit to control. Groups not in this
    /// list are left alone — their state is never touched. Empty by default
    /// (opt-in per-group via the Lights settings page).</summary>
    public List<string> HueSelectedGroupIds { get; set; } = new();

    /// <summary>How Underlit maps the schedule warmth to Hue colour. Default is
    /// circadian (kelvin → mireds) which keeps the bulbs in their native colour-
    /// temperature space. Other options drive the bulbs out into RGB land for
    /// stronger evening cues.</summary>
    public HueColorRangeMode HueColorRange { get; set; } = HueColorRangeMode.Circadian;

    /// <summary>v0.6.45: gradient endpoints for the WhiteToRed color range.
    /// Stored as ARGB hex strings; defaults match the previous hard-coded
    /// behaviour (D65 white at 6500 K, deep red at 1500 K). Users can pick
    /// their own endpoints from the Lights settings page when WhiteToRed
    /// is selected.</summary>
    public string HueGradientCoolColor { get; set; } = "#FFFFFFFF";
    public string HueGradientWarmColor { get; set; } = "#FFB00010";

    /// <summary>Hue brightness in the bridge's native 1..254 range. Independent
    /// from the screen brightness — the user adjusts it via the Lights slider
    /// or the dedicated Hue brightness hotkeys.</summary>
    public int HueBrightness { get; set; } = 200;

    /// <summary>Kelvin offset added to the screen's current warmth before
    /// sending to Hue. Negative = Hue warmer than screen; positive = cooler.
    /// Useful for "screen at neutral, lamps slightly warmer" workflows.</summary>
    public int HueWarmthOffsetKelvin { get; set; } = 0;

    /// <summary>When true, Hue ignores temporary boost (when the screen jumps
    /// to full neutral white) so the room lights don't flash bright every
    /// time the user presses Boost. Default true — matches the user's stated
    /// preference. They can opt in to "follow boost" by turning it off.</summary>
    public bool HueIgnoreBoost { get; set; } = true;

    public string HotkeyHueBrightnessDown { get; set; } = "";
    public string HotkeyHueBrightnessUp   { get; set; } = "";
    public string HotkeyHueWarmthDown     { get; set; } = "";
    public string HotkeyHueWarmthUp       { get; set; } = "";

    /// <summary>How much HueBrightness changes per hotkey press (1..254 range,
    /// default 25 ≈ 10% per press). Independent of the screen brightness step.</summary>
    public int HueBrightnessStep { get; set; } = 25;
    /// <summary>How much HueWarmthOffsetKelvin changes per hotkey press.
    /// Defaults to 200 K to match the screen warmth step.</summary>
    public int HueWarmthStep { get; set; } = 200;

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
        BedtimeStartKelvin = p.BedtimeStartKelvin;
        BedtimeEndKelvin   = p.BedtimeKelvin;
        WakeupStartKelvin  = p.WakeupStartKelvin;
        WakeupEndKelvin    = p.WakeTimeKelvin;
        NightWarmthKelvin  = p.NightWarmthKelvin;
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

            // v0.6.45: the built-in Recommended profile is fully science-
            // locked. The ONLY user-editable fields are Bedtime, WakeTime,
            // and NightWarmthKelvin. The ramp onset times and per-anchor
            // kelvins are always derived from those three so the curve
            // shape is the curated circadian one regardless of any drift
            // from earlier versions or hand-edits to settings.json.
            if (p.IsBuiltIn)
            {
                p.BedtimeStart        = ShiftHours(p.Bedtime,  -2.5);
                p.WakeupStart         = ShiftHours(p.WakeTime, -0.75);
                p.BedtimeStartKelvin  = 6500;
                p.WakeTimeKelvin      = 6500;
                int deepClamped = Math.Clamp(p.NightWarmthKelvin, 1500, 6500);
                p.BedtimeKelvin       = deepClamped;
                p.WakeupStartKelvin   = deepClamped;
                p.NightWarmthKelvin   = deepClamped;
            }

            // Per-point kelvin migration (v0.6.24 → v0.6.25).
            // Older profiles only had NightWarmthKelvin; the four per-anchor
            // kelvins didn't exist. If a profile loaded from JSON has zero or
            // out-of-range values for the per-point kelvins (likely default-
            // initialised because the JSON didn't contain the field), seed them
            // from NightWarmthKelvin (for the deep points) or 6500 (for the
            // neutral points).
            if (p.BedtimeKelvin     <= 0) p.BedtimeKelvin     = p.NightWarmthKelvin;
            if (p.WakeupStartKelvin <= 0) p.WakeupStartKelvin = p.NightWarmthKelvin;
            if (p.BedtimeStartKelvin <= 0) p.BedtimeStartKelvin = 6500;
            if (p.WakeTimeKelvin     <= 0) p.WakeTimeKelvin     = 6500;

            // Clamp to the engine's accepted kelvin range.
            p.BedtimeStartKelvin  = Math.Clamp(p.BedtimeStartKelvin,  1500, 6500);
            p.BedtimeKelvin       = Math.Clamp(p.BedtimeKelvin,       1500, 6500);
            p.WakeupStartKelvin   = Math.Clamp(p.WakeupStartKelvin,   1500, 6500);
            p.WakeTimeKelvin      = Math.Clamp(p.WakeTimeKelvin,      1500, 6500);

            // v0.6.38: the night plateau is a single warmth — keep the two
            // deep anchors (Bedtime's "after the evening ramp" and Wakeup's
            // "before the morning ramp") equal. If a settings.json from
            // before this constraint shipped with drifted values, converge
            // them on load to whichever was deeper, so the curve is flat
            // through the night going forward.
            int deep = Math.Min(p.BedtimeKelvin, p.WakeupStartKelvin);
            p.BedtimeKelvin     = deep;
            p.WakeupStartKelvin = deep;
            // NightWarmthKelvin tracks the same value for the legacy slider.
            p.NightWarmthKelvin = deep;
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

    // Each anchor point on the warmth curve has its OWN kelvin (v0.6.25+),
    // so the user can drag any point on the schedule graph vertically to
    // adjust the warmth at that boundary. The schedule curve interpolates
    // smoothly between the four points using smoothstep — see
    // Scheduler.ComputeWarmth.

    /// <summary>Start of the evening warming ramp. Defaults to Bedtime − 2.5h.</summary>
    public TimeOfDay BedtimeStart { get; set; } = new(21, 0);
    /// <summary>Kelvin at BedtimeStart — the value the curve holds at neutral
    /// before the evening ramp begins. Default 6500 (full neutral).</summary>
    public int BedtimeStartKelvin { get; set; } = 6500;

    /// <summary>End of the evening ramp.</summary>
    public TimeOfDay Bedtime  { get; set; } = new(23, 30);
    /// <summary>Kelvin at Bedtime — the deep evening warmth. Default 2700.</summary>
    public int BedtimeKelvin { get; set; } = 2700;

    /// <summary>Start of the morning ramp. Defaults to WakeTime − 0.75h.</summary>
    public TimeOfDay WakeupStart { get; set; } = new(6, 45);
    /// <summary>Kelvin at WakeupStart — the curve has been at this value
    /// through the night. Default 2700.</summary>
    public int WakeupStartKelvin { get; set; } = 2700;

    /// <summary>End of the morning ramp.</summary>
    public TimeOfDay WakeTime { get; set; } = new(7, 30);
    /// <summary>Kelvin at WakeTime — back to neutral. Default 6500.</summary>
    public int WakeTimeKelvin { get; set; } = 6500;

    /// <summary>Legacy single kelvin used in v0.6.24 and earlier — meant the
    /// flat "deep night warmth" value. Migrated into BedtimeKelvin/WakeupStartKelvin
    /// on load (see EnsureProfilesInitialized). Still exposed because the
    /// Settings UI's "Warmth at night" slider drives both per-point kelvins
    /// in lockstep through this field.</summary>
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

/// <summary>
/// How Underlit maps the schedule warmth onto the user's Hue lights. Selected
/// per-bridge in Settings → Lights.
///   • Circadian   — kelvin → Hue mireds (153..500). Native colour temperature
///                   on the bulb's white axis. Subtle, what most users want.
///   • WhiteToRed  — kelvin → CIE xy. Daytime = white, deep evening = saturated
///                   red/orange. More dramatic; only works on bulbs that
///                   support full colour (Hue Color, Lightstrip, Play, etc.).
///   • WarmWhiteOnly — clamped to a narrow 5000K → 2700K band. Useful for
///                   ambient lighting where deep-warm colours feel too much.
/// </summary>
public enum HueColorRangeMode
{
    Circadian     = 0,
    WhiteToRed    = 1,
    WarmWhiteOnly = 2,
}
