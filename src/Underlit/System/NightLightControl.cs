using System;
using Microsoft.Win32;

namespace Underlit.Sys;

/// <summary>
/// Disables Windows' built-in Night Light so it doesn't fight our gamma-based warmth.
///
/// Windows stores Night Light state in an opaque binary blob under a deeply nested
/// CloudStore registry key. The state byte is either 0x15 (on) or 0x13 (off), preceded
/// by the sentinel bytes { 0x10, 0x00 }.
///
/// This IS reverse-engineered — Microsoft reserves the right to change it. We treat any
/// failure as a no-op and log. The user can always toggle Night Light manually as a
/// fallback.
/// </summary>
internal static class NightLightControl
{
    private const string KeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate";

    public static void Disable()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (k?.GetValue("Data") is not byte[] data) return; // Night Light has never been toggled — nothing to disable.

            // Find the sentinel; if we can't, bail. Don't guess.
            if (!TryFindStateByteOffset(data, out int stateOffset)) return;
            if (data[stateOffset] == 0x13) return; // already disabled

            var copy = (byte[])data.Clone();
            copy[stateOffset] = 0x13;
            // Bump the change counter so the system picks up the update.
            if (copy.Length > 23) copy[23]++;
            k.SetValue("Data", copy, RegistryValueKind.Binary);
            Logger.Info("Disabled Windows Night Light");
        }
        catch (Exception ex)
        {
            Logger.Warn("NightLightControl.Disable failed (harmless)", ex);
        }
    }

    private static bool TryFindStateByteOffset(byte[] data, out int offset)
    {
        // The state byte follows a { 0x10, 0x00 } marker.
        for (int i = 0; i + 2 < data.Length; i++)
        {
            if (data[i] == 0x10 && data[i + 1] == 0x00)
            {
                byte candidate = data[i + 2];
                if (candidate == 0x15 || candidate == 0x13)
                {
                    offset = i + 2;
                    return true;
                }
            }
        }
        offset = -1;
        return false;
    }
}
