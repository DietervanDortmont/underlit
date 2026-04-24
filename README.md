# Underlit

A Windows brightness + color-temperature controller that extends **below** Windows' native minimum and adds a second "color temperature" axis with the same keyboard-driven feel. Think f.lux's warmth plus SunsetScreen's dimming, but wired straight into the way Windows handles brightness keys instead of living in a menu.

## What it does

- **Brightness past zero.** When your monitor's hardware brightness bottoms out, Underlit keeps dimming via gamma-ramp + a transparent black overlay on every monitor. You just keep pressing the brightness-down key.
- **Color temperature with its own hotkey axis.** A second keyboard pair (default `Ctrl+Alt+Left/Right`) shifts warmth between 1500K and 6500K.
- **Windows-style OSD.** Rounded, centered flyout matching the Win11 look, so it feels native.
- **Multi-monitor aware.** One overlay per monitor, per-display gamma, DDC/CI for external monitors that support it.
- **Schedule (optional).** Drifts toward a warmer/underlit baseline between your bedtime and wakeup times and back.
- **Boost.** One keypress jumps back to full brightness + neutral warmth for a configurable number of seconds, then restores.
- **Per-monitor offsets.** Dim your secondary display relative to the main one.
- **App exclusion list.** Auto-suspends dimming while, say, Photoshop is focused.

## Requirements

- Windows 10 (2004+) or Windows 11
- .NET 8 desktop runtime (bundled by the installer if you build the self-contained publish)

## Build

```powershell
# From repo root
dotnet restore
dotnet publish src/Underlit/Underlit.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

The single-file `publish\Underlit.exe` is ready to run. To build a proper installer, install [Inno Setup 6](https://jrsoftware.org/isdl.php) and compile `installer/Underlit.iss`.

## Run

Launch `Underlit.exe`. It lives in the system tray — left-click opens settings, right-click pauses or quits.

Default hotkeys (re-bindable in Settings → Hotkeys):

- `Ctrl+Alt+Down / Up` — brightness
- `Ctrl+Alt+Left / Right` — warmth
- `Ctrl+Alt+B` — boost to full for 10s
- `Ctrl+Alt+Shift+D` — toggle Underlit on/off

If your laptop's Fn brightness keys route through the Windows hotkey system (most do), Underlit will extend them automatically — keep pressing past the Windows minimum and it picks up the slack.

## Architecture notes

```
UnderlitHost             — top-level wiring, lifecycle
  UnderlitEngine         — state machine; splits a single level into hw / gamma / overlay layers
  OverlayManager       — per-monitor click-through transparent windows
  GammaRampApplier     — SetDeviceGammaRamp per monitor, brightness + warmth
  HardwareBrightness   — WMI (internal panel) + DDC/CI (externals) unified
  Scheduler            — bedtime/wakeup baseline curve
  HotkeyManager        — RegisterHotKey for user-bound combos
  LowLevelKeyboardHook — WH_KEYBOARD_LL for native Fn-brightness key interception
  TrayIcon, OsdWindow, SettingsWindow
```

## Known limitations

- **HDR.** Windows rejects gamma-ramp writes when a display is in HDR mode; on those displays Underlit falls back to overlay-only (still works, just less smooth).
- **Fullscreen exclusive games.** Overlays don't cover DX exclusive fullscreen — Underlit is a no-op there. Most modern games use borderless windowed, which works fine.
- **DDC/CI compatibility** varies by monitor. Some lie about their range; some refuse writes faster than ~5Hz. The engine debounces hardware writes to once per ramp tick.
- **Fn brightness keys on some OEMs** (especially older Dell/HP) route through vendor HID devices that don't surface to user-mode hooks. In that case use Underlit's custom hotkeys.
- **Windows Night Light** will conflict with Underlit's warmth. Underlit disables it at startup when configured (default on).

## Files on disk

- Settings: `%APPDATA%\Underlit\settings.json`
- Log: `%LOCALAPPDATA%\Underlit\underlit.log`

## Not included yet (planned)

- Philips Hue (and other LAN-addressable bulb) integration, so ambient lighting tracks the on-screen baseline.
- Per-application brightness profiles (not just exclusion).
- Toast-style quick-adjust popup when you mouse over the tray icon.

## License

MIT. See LICENSE.
