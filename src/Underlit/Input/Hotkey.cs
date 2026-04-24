using System;
using System.Text;

namespace Underlit.Input;

/// <summary>
/// Represents a user-bindable global hotkey. Round-trips cleanly to/from a string
/// like "Ctrl+Alt+Down" for settings storage.
/// </summary>
public sealed record Hotkey(HotkeyModifiers Modifiers, uint VirtualKey)
{
    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) sb.Append("Ctrl+");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt))     sb.Append("Alt+");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift))   sb.Append("Shift+");
        if (Modifiers.HasFlag(HotkeyModifiers.Win))     sb.Append("Win+");
        sb.Append(VkToName(VirtualKey));
        return sb.ToString();
    }

    public static Hotkey Parse(string s)
    {
        var mods = HotkeyModifiers.None;
        uint vk = 0;
        foreach (var tok in s.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (tok.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= HotkeyModifiers.Control; break;
                case "alt":                  mods |= HotkeyModifiers.Alt;     break;
                case "shift":                mods |= HotkeyModifiers.Shift;   break;
                case "win": case "windows":  mods |= HotkeyModifiers.Win;     break;
                default:
                    vk = NameToVk(tok);
                    break;
            }
        }
        if (vk == 0) throw new FormatException($"No key in hotkey '{s}'");
        return new Hotkey(mods, vk);
    }

    private static string VkToName(uint vk)
    {
        return vk switch
        {
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x20 => "Space",
            0x0D => "Enter",
            _ when vk >= 0x30 && vk <= 0x39 => ((char)vk).ToString(),
            _ when vk >= 0x41 && vk <= 0x5A => ((char)vk).ToString(),
            _ when vk >= 0x70 && vk <= 0x7B => "F" + (vk - 0x6F),
            _ => "VK" + vk.ToString("X2")
        };
    }

    private static uint NameToVk(string name)
    {
        name = name.Trim();
        switch (name.ToLowerInvariant())
        {
            case "left":  return 0x25;
            case "up":    return 0x26;
            case "right": return 0x27;
            case "down":  return 0x28;
            case "space": return 0x20;
            case "enter": case "return": return 0x0D;
        }
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (c >= '0' && c <= '9') return c;
            if (c >= 'A' && c <= 'Z') return c;
        }
        if ((name.StartsWith("f", StringComparison.OrdinalIgnoreCase) || name.StartsWith("F")) && name.Length >= 2)
        {
            if (int.TryParse(name[1..], out var n) && n >= 1 && n <= 12)
                return (uint)(0x6F + n);
        }
        throw new FormatException($"Unknown key '{name}'");
    }
}

[Flags]
public enum HotkeyModifiers
{
    None    = 0,
    Alt     = 0x0001,
    Control = 0x0002,
    Shift   = 0x0004,
    Win     = 0x0008,
}
