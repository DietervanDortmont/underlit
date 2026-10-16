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
    // v0.6.79: 40 ms default (was 80). The gamma/overlay transition over
    // 80 ms was reading as "noticeable lag" on rapid hotkey presses,
    // especially in the lower brightness range where the overlay drives
    // the descent. 40 ms is short enough to feel snappy on press but long
    // enough to avoid harsh snaps. The user can still dial 0 (snap) or
    // a higher value via Settings → BRIGHTNESS → Software fade duration.
    public int RampDurationMs { get; set; } = 40;

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

    /// <summary>v0.6.69: user-pickable colour for the sun (brightness)
    /// icon in the OSD pill. Default #FFFEC80F (warm amber-yellow),
    /// matching the "candle flame" feel the OSD's other warmth-side
    /// elements already lean toward. Stored as #AARRGGBB; the OSD's
    /// own theme update path always honours this colour, ignoring the
    /// luminance-based auto colour that used to drive the icon.</summary>
    public string OsdSunIconColor { get; set; } = "#FFFEC80F";

    /// <summary>v0.6.71: right-side inset for the bar inside the OSD
    /// pill, in dip. Default 24. Smaller value = wider bar (extends
    /// closer to the pill's right edge); larger = narrower bar.
    /// Affects both the brightness and warmth indicators because
    /// they share the same ContentRoot Grid column.</summary>
    public int OsdBarRightInsetDip { get; set; } = 24;

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

    /// <summary>v0.6.48: true once the first-run intro modal has been
    /// dismissed. Stays in settings.json so the popup never appears
    /// twice for the same user across upgrades.</summary>
    public bool HasSeenIntro { get; set; } = false;

    /// <summary>v0.6.48: persisted Settings window geometry. -1 means
    /// "no saved value — use the WPF default (centre on screen, 760x600)".
    /// Only saved on close, not during resize, so a transient state
    /// doesn't get committed if the user accidentally maximises and
    /// crashes. Stored as int so JSON serialisation is trivial.</summary>
    public int SettingsWindowLeft   { get; set; } = -1;
    public int SettingsWindowTop    { get; set; } = -1;
    public int SettingsWindowWidth  { get; set; } = -1;
    public int SettingsWindowHeight { get; set; } = -1;

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

    /// <summary>v0.6.53: individual light ids selected for Underlit to control.
    /// Distinct from <see cref="HueSelectedGroupIds"/>: lights selected here
    /// get the per-bulb gamut-aware push (graceful fade-out for limited
    /// bulbs, manual on/off respect for Google-Home coexistence). Group
    /// selections are EXPANDED to their member lights at push time and
    /// merged with this list so the same per-bulb logic applies. Empty by
    /// default - opt-in per-light via the Lights settings page.</summary>
    public List<string> HueSelectedLightIds { get; set; } = new();

    /// <summary>v0.6.55: how much earlier than the bulb's own CT-max
    /// limit the fade-out should start, in kelvin. The fade ramps
    /// brightness 1 -> 0 between (bulb's CT max + this offset) and the
    /// schedule's deepest-warm anchor, so a Tradfri tunable that maxes
    /// at 2200 K with a 200 K offset starts dimming at 2400 K and is
    /// fully off when the schedule reaches its deepest warmth. Increase
    /// the value to make CT-limited bulbs ease into the fade earlier
    /// (smoother), decrease toward zero for a sharper cliff at the
    /// bulb's actual CT limit. Default 200 K - noticeable easing
    /// without intruding on the bulb's normal CT range.</summary>
    public int HueBulbFadeOnsetKelvin { get; set; } = 200;

    /// <summary>v0.6.55: user-defined Quick Scenes. Built-in scenes
    /// (Reading / Relax / Movie night / Energize / Resume / Off) are
    /// always present; this list extends them with whatever the user
    /// adds via the "+" button in the Quick Scenes card.</summary>
    public List<QuickScene> CustomQuickScenes { get; set; } = new();

    /// <summary>v0.6.55: opt-in wind-down notification. When enabled,
    /// Underlit shows a tray balloon "Wind-down time" the configured
    /// number of minutes BEFORE the active profile's bedtime. Off by
    /// default - users have to choose to enable it.</summary>
    public bool WindDownNotificationEnabled { get; set; } = false;
    public int  WindDownNotificationMinutesBeforeBedtime { get; set; } = 30;
    public string WindDownNotificationMessage { get; set; } =
        "Wind-down time. Want to dim the lights and ease into bedtime?";

    /// <summary>v0.6.55: time-triggered routines. Each routine fires at
    /// a specific time-of-day (or relative to wakeup/bedtime) and applies
    /// an action - typically toggling a Hue plug for the SAD-lamp use
    /// case. Routines run independently of the warmth schedule.</summary>
    public List<Routine> Routines { get; set; } = new();

    /// <summary>v0.6.58: per-group colour profiles. Each entry is a
    /// LightColorGroup with its own gradient + time offset that drives
    /// a subset of the user's selected lights/groups. Empty by default
    /// (= legacy single-gradient behaviour); users opt in by adding
    /// their first group via the Lights settings page. The HueController
    /// routes pushes through these when non-empty.
    /// <para>v0.6.73: now backed by <see cref="LightProfiles"/>. When
    /// at least one profile exists this returns the ACTIVE profile's
    /// ColorGroups list, so HueController and downstream callers don't
    /// have to know about the profile layer. The setter routes to the
    /// active profile too. On legacy settings (before any profile was
    /// ever created), a private backing list keeps the property
    /// functional until the first <see cref="EnsureLightProfilesPopulated"/>
    /// call wraps it into a "My profile" entry.</para></summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<LightColorGroup> ColorGroups
    {
        get
        {
            if (LightProfiles != null && LightProfiles.Count > 0)
            {
                var id = ActiveLightProfileId;
                var active = (string.IsNullOrEmpty(id) ? null : LightProfiles.FirstOrDefault(p => p.Id == id))
                             ?? LightProfiles[0];
                return active.ColorGroups;
            }
            return _legacyColorGroupsBacking;
        }
        set
        {
            var v = value ?? new List<LightColorGroup>();
            if (LightProfiles != null && LightProfiles.Count > 0)
            {
                var id = ActiveLightProfileId;
                var active = (string.IsNullOrEmpty(id) ? null : LightProfiles.FirstOrDefault(p => p.Id == id))
                             ?? LightProfiles[0];
                active.ColorGroups = v;
            }
            else
            {
                _legacyColorGroupsBacking = v;
            }
        }
    }

    /// <summary>JSON-only field — kept solely so settings files written
    /// before v0.6.73 (which had a top-level "ColorGroups" key but no
    /// "LightProfiles") can round-trip. After deserialisation,
    /// <see cref="EnsureLightProfilesPopulated"/> moves these into the
    /// first LightProfile and the backing field is no longer used.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("ColorGroups")]
    public List<LightColorGroup> ColorGroupsJsonSlot
    {
        get => ColorGroups;
        set => ColorGroups = value;
    }

    private List<LightColorGroup> _legacyColorGroupsBacking = new();

    /// <summary>v0.6.73: every "lights timeline" the user can switch
    /// between. Each LightProfile carries its own set of LightColorGroups
    /// (= rows in the lights timeline), so the user can have a
    /// "weekday morning" profile, an "evening wind-down" profile, etc.,
    /// and swap between them from the LIGHTS TIMELINE card without
    /// touching the underlying selected lights/groups.</summary>
    public List<LightProfile> LightProfiles { get; set; } = new();
    /// <summary>v0.6.73: id of the currently-active <see cref="LightProfile"/>.
    /// Resolved at access time via <see cref="ActiveLightProfile"/>;
    /// invalid ids silently fall back to the first profile.</summary>
    public string ActiveLightProfileId { get; set; } = "";

    /// <summary>v0.6.73: return the active <see cref="LightProfile"/>,
    /// populating <see cref="LightProfiles"/> from legacy ColorGroups
    /// (or creating a fresh "My profile" entry) if the list is empty.
    /// Always returns a non-null profile.</summary>
    public LightProfile ActiveLightProfile()
    {
        EnsureLightProfilesPopulated();
        var id = ActiveLightProfileId;
        return LightProfiles.FirstOrDefault(p => p.Id == id) ?? LightProfiles[0];
    }

    /// <summary>v0.6.73: make sure at least one LightProfile exists.
    /// On a legacy settings file this wraps the deserialised
    /// <see cref="_legacyColorGroupsBacking"/> into a single profile
    /// named "My profile". Called from <see cref="Load"/> and from
    /// <see cref="ActiveLightProfile"/> so it's safe to call multiple
    /// times — second and subsequent calls are no-ops.</summary>
    public void EnsureLightProfilesPopulated()
    {
        LightProfiles ??= new List<LightProfile>();
        if (LightProfiles.Count == 0)
        {
            var first = new LightProfile
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = _legacyColorGroupsBacking.Count > 0 ? "My profile" : "Default",
                IsBuiltIn = _legacyColorGroupsBacking.Count == 0,
                ColorGroups = _legacyColorGroupsBacking.ToList(),
            };
            LightProfiles.Add(first);
            ActiveLightProfileId = first.Id;
            _legacyColorGroupsBacking = new List<LightColorGroup>();
        }
        if (string.IsNullOrEmpty(ActiveLightProfileId) ||
            !LightProfiles.Any(p => p.Id == ActiveLightProfileId))
        {
            ActiveLightProfileId = LightProfiles[0].Id;
        }
    }

    /// <summary>v0.6.56: auto-switch the Windows app + system theme
    /// between light and dark based on the current target warmth.
    /// Off by default (so we don't surprise existing users); when on,
    /// crossing <see cref="AutoThemeKelvinThreshold"/> downward switches
    /// to dark, upward switches to light. The theme apply is hammered
    /// every 30 s and on every warmth change so flaky Windows builds
    /// (and tools like AutoDark that occasionally lose) can't outlast it.</summary>
    public bool AutoSwitchWindowsTheme { get; set; } = false;
    public int AutoThemeKelvinThreshold { get; set; } = 4000;

    /// <summary>v0.6.63: per-mode wallpaper / desktop-background switching
    /// applied alongside the light/dark theme. None = leave wallpaper
    /// alone; Image = set the configured image path; SolidColor =
    /// remove wallpaper and paint the desktop with the configured
    /// colour. Defaults to None for both modes so existing users see
    /// no change unless they opt in.</summary>
    public BackgroundChoice LightBackgroundChoice { get; set; } = BackgroundChoice.None;
    public BackgroundChoice DarkBackgroundChoice  { get; set; } = BackgroundChoice.None;
    public string LightBackgroundImagePath { get; set; } = "";
    public string DarkBackgroundImagePath  { get; set; } = "";
    /// <summary>Solid colour used when the corresponding mode's choice
    /// is SolidColor. Stored as #AARRGGBB.</summary>
    public string LightBackgroundColor { get; set; } = "#FFFFFFFF"; // pure white
    public string DarkBackgroundColor  { get; set; } = "#FF400010"; // deep red

    /// <summary>v0.6.57: OSD drop-shadow params. Always rendered (in all
    /// backdrop modes) so the pill has contrast against light backgrounds.
    /// Opacity 0..100 = none..opaque; BlurRadius 0..60 dip; Offset 0..30
    /// dip; Angle 0..359 deg (270 = below).
    /// v0.6.64: Spread 0..30 dip — grows the shadow's source pill outward
    /// by that many dip on every side before the blur is applied (CSS
    /// box-shadow "spread"). Useful when the user wants a bigger soft
    /// halo without making the blur look smeary.</summary>
    public int OsdShadowOpacity { get; set; } = 55;
    public int OsdShadowBlur    { get; set; } = 22;
    public int OsdShadowOffset  { get; set; } = 8;
    public int OsdShadowAngle   { get; set; } = 270;
    public int OsdShadowSpread  { get; set; } = 0;

    /// <summary>How Underlit maps the schedule warmth to Hue colour. Default is
    /// circadian (kelvin → mireds) which keeps the bulbs in their native colour-
    /// temperature space. Other options drive the bulbs out into RGB land for
    /// stronger evening cues.</summary>
    public HueColorRangeMode HueColorRange { get; set; } = HueColorRangeMode.Circadian;

    /// <summary>v0.6.45: gradient endpoints for the WhiteToRed color range.
    /// Stored as ARGB hex strings; defaults match the previous hard-coded
    /// behaviour (D65 white at 6500 K, deep red at 1500 K). Users can pick
    /// their own endpoints from the Lights settings page when WhiteToRed
    /// is selected.
    ///
    /// v0.6.48: defensive null check on the setter — older settings.json
    /// files predating these fields deserialise as null, which would
    /// crash the colour picker or get the bridge a literal "null" string.
    /// Coerce to the default so the gradient still works.</summary>
    public string HueGradientCoolColor
    {
        get => _hueGradientCool ?? "#FFFFFFFF";
        set => _hueGradientCool = string.IsNullOrWhiteSpace(value) ? "#FFFFFFFF" : value;
    }
    public string HueGradientWarmColor
    {
        get => _hueGradientWarm ?? "#FFB00010";
        set => _hueGradientWarm = string.IsNullOrWhiteSpace(value) ? "#FFB00010" : value;
    }
    private string? _hueGradientCool = "#FFFFFFFF";
    private string? _hueGradientWarm = "#FFB00010";

    /// <summary>
    /// v0.6.49: full multi-stop gradient. Photoshop-style — users can add as
    /// many colour stops as they like along the kelvin axis. The Hue
    /// controller maps the current kelvin to the two adjacent stops and
    /// lerps in CIE xy space.
    ///
    /// Stops are stored in the order the user added them; the controller
    /// sorts by kelvin at render time. The list always has at least two
    /// stops (a cool and a warm endpoint). On load, an empty/null list is
    /// migrated from the legacy <see cref="HueGradientCoolColor"/> +
    /// <see cref="HueGradientWarmColor"/> fields so old settings.json files
    /// still work without losing the user's customised endpoints.
    /// </summary>
    public List<HueGradientStop> HueGradientStops
    {
        get
        {
            if (_hueGradientStops is null || _hueGradientStops.Count < 2)
            {
                _hueGradientStops = new List<HueGradientStop>
                {
                    new HueGradientStop { Kelvin = 1500, Color = HueGradientWarmColor },
                    new HueGradientStop { Kelvin = 6500, Color = HueGradientCoolColor },
                };
            }
            return _hueGradientStops;
        }
        set => _hueGradientStops = value;
    }
    private List<HueGradientStop>? _hueGradientStops;

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

    /// <summary>v0.7.0: where the lamps get their colour from.
    /// ScreenWarmth = the old behaviour (follow the screen's kelvin curve,
    /// coloured through the kelvin gradient). TimeOfDay = each timeline
    /// row has its own colour stops placed directly on a 24 h clock.</summary>
    public HueColorSource HueColorSource { get; set; } = HueColorSource.ScreenWarmth;

    /// <summary>v0.7.0: time-of-day colour stops for lights that aren't
    /// claimed by a colour group (the "Default" row). Colour groups carry
    /// their own <see cref="LightColorGroup.TimeStops"/>. Always at least
    /// one stop; an empty / null list is reseeded with
    /// <see cref="DefaultTimeStops"/>.</summary>
    public List<HueTimeStop> HueTimeStops
    {
        get
        {
            if (_hueTimeStops is null || _hueTimeStops.Count == 0)
                _hueTimeStops = DefaultTimeStops();
            return _hueTimeStops;
        }
        set => _hueTimeStops = value;
    }
    private List<HueTimeStop>? _hueTimeStops;

    /// <summary>Starter day: deep red -> orange -> warm white -> white in
    /// the morning, the reverse in the evening. Stops are anchored to wake-up / bedtime so they move
    /// when the schedule does.</summary>
    public static List<HueTimeStop> DefaultTimeStops() => new()
    {
        // Morning: deep red -> orange -> warm white -> white.
        new HueTimeStop { Anchor = HueTimeAnchor.WakeTime, Minutes = -60,  Color = "#FFB00010", BrightnessPercent = 8 },
        new HueTimeStop { Anchor = HueTimeAnchor.WakeTime, Minutes = -40,  Color = "#FFFF7A00", BrightnessPercent = 35 },
        new HueTimeStop { Anchor = HueTimeAnchor.WakeTime, Minutes = -20,  Color = "#FFFFD7A0", BrightnessPercent = 70 },
        new HueTimeStop { Anchor = HueTimeAnchor.WakeTime, Minutes = 0,    Color = "#FFFFFFFF", BrightnessPercent = 100 },
        // Evening: the same in reverse, ending at a dim deep red.
        new HueTimeStop { Anchor = HueTimeAnchor.Bedtime,  Minutes = -240, Color = "#FFFFFFFF", BrightnessPercent = 100 },
        new HueTimeStop { Anchor = HueTimeAnchor.Bedtime,  Minutes = -150, Color = "#FFFFD7A0", BrightnessPercent = 85 },
        new HueTimeStop { Anchor = HueTimeAnchor.Bedtime,  Minutes = -75,  Color = "#FFFF7A00", BrightnessPercent = 55 },
        new HueTimeStop { Anchor = HueTimeAnchor.Bedtime,  Minutes = 0,    Color = "#FFB00010", BrightnessPercent = 20 },
    };

    /// <summary>v0.7.0: what happens after something other than Underlit
    /// (Google Home, the Hue app, a dimmer switch) changes a light's
    /// colour or brightness. Underlit always stops driving that bulb at
    /// first; this decides when it takes it back.</summary>
    public HueOverrideRelease HueOverrideRelease { get; set; } = HueOverrideRelease.UntilLightOff;

    /// <summary>v0.7.1: time-of-day mode. How far white-only bulbs (which
    /// can't make orange or red) keep following the colour before they've
    /// dimmed out completely. 0 = bow out as soon as the colour leaves
    /// white; 100 = hang on until deep orange. See BulbColorMapper.</summary>
    public int HueWhiteBulbFollowPercent { get; set; } = 30;

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
                    // v0.6.73: wrap any legacy ColorGroups (from JSON
                    // written before this version) into a LightProfile.
                    loaded.EnsureLightProfilesPopulated();
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
        fresh.EnsureLightProfilesPopulated();
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

/// <summary>
/// One Photoshop-style stop in the multi-stop Hue gradient (v0.6.49).
/// <see cref="Kelvin"/> is the position along the schedule's kelvin axis
/// (1500..6500); <see cref="Color"/> is an sRGB hex string ("#FFRRGGBB")
/// that the Hue controller converts to CIE xy at render time.
/// </summary>
public sealed class HueGradientStop
{
    public int Kelvin { get; set; } = 6500;
    public string Color { get; set; } = "#FFFFFFFF";
}

/// <summary>v0.7.0: see <see cref="AppSettings.HueColorSource"/>.</summary>
public enum HueColorSource
{
    ScreenWarmth = 0,
    TimeOfDay    = 1,
}

/// <summary>v0.7.0: see <see cref="AppSettings.HueOverrideRelease"/>.</summary>
public enum HueOverrideRelease
{
    /// <summary>Take the bulb back once it has been switched off (by
    /// anyone) and is on again.</summary>
    UntilLightOff = 0,
    /// <summary>Same as UntilLightOff, and also take it back at the next
    /// colour stop (time-of-day mode) or schedule ramp point.</summary>
    UntilNextStop = 1,
    /// <summary>Only take it back when the user picks "Resume lights
    /// schedule" (tray menu or the Resume quick scene).</summary>
    UntilResume   = 2,
}

/// <summary>What a <see cref="HueTimeStop"/>'s time is measured from.</summary>
public enum HueTimeAnchor
{
    /// <summary><see cref="HueTimeStop.Minutes"/> = minutes after midnight.</summary>
    Clock    = 0,
    /// <summary>Minutes relative to the active schedule's wake-up time.</summary>
    WakeTime = 1,
    /// <summary>Minutes relative to the active schedule's bedtime.</summary>
    Bedtime  = 2,
}

/// <summary>v0.7.0: one colour stop on the 24 h lights timeline. The
/// lamps fade smoothly from one stop to the next (wrapping past
/// midnight). BrightnessPercent 0 (or a black colour) means "off".</summary>
public sealed class HueTimeStop
{
    public HueTimeAnchor Anchor { get; set; } = HueTimeAnchor.Clock;
    /// <summary>Clock: minute of the day (0..1439). WakeTime / Bedtime:
    /// signed offset in minutes from that anchor.</summary>
    public int Minutes { get; set; } = 12 * 60;
    public string Color { get; set; } = "#FFFFFFFF";
    public int BrightnessPercent { get; set; } = 100;
    /// <summary>True = keep this stop's colour until the next stop and
    /// switch there; false (default) = fade smoothly into the next stop.</summary>
    public bool Hold { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsOff => BrightnessPercent <= 0 || IsBlack(Color);

    private static bool IsBlack(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#') return false;
        try
        {
            int off = hex.Length == 9 ? 3 : 1;
            int r = Convert.ToInt32(hex.Substring(off, 2), 16);
            int g = Convert.ToInt32(hex.Substring(off + 2, 2), 16);
            int b = Convert.ToInt32(hex.Substring(off + 4, 2), 16);
            return r + g + b < 24;
        }
        catch { return false; }
    }

    public HueTimeStop Copy() => new()
    {
        Anchor = Anchor, Minutes = Minutes, Color = Color, BrightnessPercent = BrightnessPercent, Hold = Hold,
    };
}

/// <summary>v0.6.55: a one-click Hue scene the user can apply to all
/// currently-selected groups + lights. Built-in scenes are baked into
/// SettingsWindow as constants; the user's own additions live in
/// <see cref="AppSettings.CustomQuickScenes"/> and round-trip via JSON.</summary>
public sealed class QuickScene
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>How this scene affects the selected lights when applied.
    /// SetCtBri = turn on, push the scene's Kelvin and Brightness;
    /// TurnOff = send on:false (sticky - schedule won't auto-revive);
    /// ResumeSchedule = send on:true and let the next schedule tick
    /// reassert the schedule's current warmth.</summary>
    public QuickSceneAction Action { get; set; } = QuickSceneAction.SetCtBri;

    public int Kelvin { get; set; } = 4500;
    /// <summary>0..100. Converted to bridge-native 1..254 at apply time.</summary>
    public int BrightnessPercent { get; set; } = 100;
}

public enum QuickSceneAction
{
    SetCtBri       = 0,
    TurnOff        = 1,
    ResumeSchedule = 2,
}

/// <summary>v0.6.63: how the auto-theme switcher should change the
/// desktop background when toggling light/dark.
///   None       - leave the wallpaper alone (default)
///   Image      - set the configured image file as the wallpaper
///   SolidColor - clear wallpaper, paint the desktop with the configured colour</summary>
public enum BackgroundChoice
{
    None       = 0,
    Image      = 1,
    SolidColor = 2,
}

/// <summary>v0.6.55: time-triggered automation. Fires once a day when
/// the local clock crosses the trigger time. Common use: turn a Hue
/// plug ON at wake time, OFF after N minutes (the SAD-lamp pattern).</summary>
public sealed class Routine
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>How the trigger time is interpreted. AbsoluteTime fires
    /// at HHmm wall-clock; RelativeToWakeup / RelativeToBedtime offset
    /// from the active profile's anchor (negative = before).</summary>
    public RoutineTriggerKind TriggerKind { get; set; } = RoutineTriggerKind.AbsoluteTime;
    public int TriggerHour   { get; set; } = 7;
    public int TriggerMinute { get; set; } = 0;
    /// <summary>For Relative triggers: minutes offset (negative = before
    /// the anchor). E.g. -10 with TriggerKind=RelativeToWakeup fires 10
    /// minutes before the user's WakeupStart.</summary>
    public int TriggerOffsetMinutes { get; set; } = 0;
    /// <summary>Days of the week the routine fires (bit flags). 0x7F =
    /// every day; 0x3E = Mon-Fri only; 0x41 = weekend only.</summary>
    public RoutineDays Days { get; set; } = RoutineDays.EveryDay;

    /// <summary>Light/group/plug ids the routine acts on. Mixed types
    /// allowed; the host figures out which API to use per id.</summary>
    public List<string> TargetLightIds { get; set; } = new();
    public List<string> TargetGroupIds { get; set; } = new();

    /// <summary>What the routine does at trigger time.</summary>
    public RoutineAction Action { get; set; } = RoutineAction.TurnOn;
    /// <summary>For TurnOnFor: how long the lights stay on before being
    /// turned off again. Ignored for TurnOn / TurnOff.</summary>
    public int DurationMinutes { get; set; } = 25;
    /// <summary>For TurnOn / TurnOnFor: optional CT/bri override. Null
    /// = leave the bridge to its last value (or full bright for plugs).</summary>
    public int? Kelvin { get; set; }
    public int? BrightnessPercent { get; set; }
}

public enum RoutineTriggerKind
{
    AbsoluteTime       = 0,
    RelativeToWakeup   = 1,
    RelativeToBedtime  = 2,
}

public enum RoutineAction
{
    TurnOn    = 0,
    TurnOff   = 1,
    /// <summary>Turn on, then automatically turn off after
    /// <see cref="Routine.DurationMinutes"/> minutes. The SAD-lamp pattern.</summary>
    TurnOnFor = 2,
}

/// <summary>v0.6.58: a colour profile that drives a subset of the
/// user's lights with its own gradient and time offset. Lets the user
/// say "the kitchen group dims 30 minutes earlier than the living
/// room" or "the bedroom uses an entirely different colour palette".
///
/// <para>Lights/groups not assigned to any LightColorGroup are driven
/// by the legacy global gradient (= same as before this feature
/// existed). When the user adds at least one group, only the lights
/// they explicitly assign to it get the per-group treatment.</para></summary>
/// <summary>v0.6.73: a "lights timeline profile" — a named collection of
/// <see cref="LightColorGroup"/> entries that drive the user's Hue lights
/// together. Switching the active profile swaps the entire timeline
/// (all rows) at once. A user can keep a recommended built-in profile
/// for everyday use and create additional Custom profiles for specific
/// scenarios. See <see cref="AppSettings.LightProfiles"/> and
/// <see cref="AppSettings.ActiveLightProfileId"/>.</summary>
public sealed class LightProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Custom profile";
    /// <summary>True for the recommended/default profile. Built-in
    /// profiles can be reset to factory defaults but not deleted.</summary>
    public bool IsBuiltIn { get; set; } = false;
    /// <summary>The colour groups (= timeline rows) belonging to this
    /// profile. Switching profiles swaps in this list.</summary>
    public List<LightColorGroup> ColorGroups { get; set; } = new();
}

public sealed class LightColorGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New group";
    public bool Enabled { get; set; } = true;

    /// <summary>Hue light ids the group drives directly.</summary>
    public List<string> LightIds { get; set; } = new();
    /// <summary>Hue group ids the group drives. Expanded to member lights
    /// at push time, deduped with <see cref="LightIds"/>.</summary>
    public List<string> GroupIds { get; set; } = new();

    /// <summary>Gradient to apply to this group, kelvin → colour. Same
    /// shape as the legacy multi-stop gradient.</summary>
    public List<HueGradientStop> Gradient { get; set; } = new();

    /// <summary>v0.7.0: time-of-day colour stops used when
    /// <see cref="AppSettings.HueColorSource"/> is TimeOfDay. Empty =
    /// seeded from the Default row's stops the first time the row is shown.</summary>
    public List<HueTimeStop> TimeStops { get; set; } = new();

    /// <summary>How many minutes earlier (negative) or later (positive)
    /// this group's colour curve fires relative to the schedule. -30 =
    /// the group hits its 6 PM colour at 5:30 PM. Stored in minutes;
    /// the controller applies it as a wall-clock shift when computing
    /// the group's effective kelvin.</summary>
    public int TimeOffsetMinutes { get; set; } = 0;
}

[Flags]
public enum RoutineDays
{
    None      = 0,
    Sunday    = 1 << 0,
    Monday    = 1 << 1,
    Tuesday   = 1 << 2,
    Wednesday = 1 << 3,
    Thursday  = 1 << 4,
    Friday    = 1 << 5,
    Saturday  = 1 << 6,
    Weekdays  = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekend   = Saturday | Sunday,
    EveryDay  = Weekdays | Weekend,
}
