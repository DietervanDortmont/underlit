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

    /// <summary>Try to intercept the laptop's Fn brightness keys.</summary>
    public bool HookNativeBrightnessKeys { get; set; } = true;

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
    /// Whether to apply Win32 acrylic blur to the OSD background.
    ///   Auto = follow Windows' "Transparency effects" toggle (default).
    ///   On   = always on regardless of Windows setting.
    ///   Off  = always off, opaque-tinted background only.
    /// </summary>
    public TransparencyMode TransparencyEffects { get; set; } = TransparencyMode.Auto;

    // ---- Schedule (optional baseline curve) ----

    public bool ScheduleEnabled { get; set; } = false;
    public TimeOfDay BedtimeStart { get; set; } = new(21, 30);  // starts warming at 9:30pm
    public TimeOfDay BedtimeEnd   { get; set; } = new(23, 30);  // reaches the night warmth floor by 11:30pm
    public TimeOfDay WakeupStart  { get; set; } = new(6, 30);   // starts un-warming at 6:30am
    public TimeOfDay WakeupEnd    { get; set; } = new(8, 00);   // fully neutral by 8am
    /// <summary>Night-floor warmth in K. Brightness is intentionally NOT on a schedule —
    /// sudden brightness changes during the day felt disorienting.</summary>
    public int NightWarmthKelvin { get; set; } = 3400;

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
                if (loaded != null) return loaded;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Settings load failed, using defaults", ex);
        }
        return new AppSettings();
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

public enum TransparencyMode
{
    Auto = 0,
    On   = 1,
    Off  = 2,
}
