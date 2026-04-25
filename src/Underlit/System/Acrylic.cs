using System;
using System.Runtime.InteropServices;
using System.Windows.Media;
using Color = System.Windows.Media.Color; // disambiguate vs System.Drawing.Color (WinForms is referenced)

namespace Underlit.Sys;

/// <summary>
/// Applies a Win32 acrylic blur backdrop to a window via the undocumented
/// <c>SetWindowCompositionAttribute</c> API.
///
/// We use the legacy path rather than the modern <c>DwmSetWindowAttribute</c>
/// (DWMWA_SYSTEMBACKDROP_TYPE) because it works alongside <c>AllowsTransparency=True</c>
/// — required for our non-rectangular OSD shape — and is supported on Win10 1803+
/// as well as all of Win11.
///
/// The "tint" you pass in is a semi-transparent color blended over the blurred backdrop.
/// Our OSD's existing rounded Border already contributes a tint; the acrylic API's tint
/// can be a near-zero alpha so we don't double-tint.
/// </summary>
public static class Acrylic
{
    public static void Enable(IntPtr hwnd, Color tint, byte tintOpacity = 0x40)
    {
        ApplyAccentPolicy(hwnd, AccentState.AcrylicBlurBehind, tint, tintOpacity);
    }

    public static void EnablePlainBlur(IntPtr hwnd)
    {
        // For older Windows that doesn't support acrylic, fall back to plain blur.
        ApplyAccentPolicy(hwnd, AccentState.BlurBehind, Color.FromRgb(0, 0, 0), 0);
    }

    public static void Disable(IntPtr hwnd)
    {
        ApplyAccentPolicy(hwnd, AccentState.Disabled, Color.FromRgb(0, 0, 0), 0);
    }

    // ---------------- internals ----------------

    private enum AccentState : int
    {
        Disabled                = 0,
        EnableGradient          = 1,
        EnableTransparentGradient = 2,
        BlurBehind              = 3,
        AcrylicBlurBehind       = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public uint GradientColor;  // ABGR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    private const int WCA_ACCENT_POLICY = 19;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    private static void ApplyAccentPolicy(IntPtr hwnd, AccentState state, Color tint, byte tintOpacity)
    {
        // AccentPolicy.GradientColor is ABGR (alpha high byte, blue, green, red).
        // For BlurBehind (legacy Aero blur), the gradient color is ignored.
        uint abgr = ((uint)tintOpacity << 24)
                  | ((uint)tint.B      << 16)
                  | ((uint)tint.G      << 8)
                  | (uint)tint.R;

        var policy = new AccentPolicy
        {
            AccentState = state,
            // Flag 2 = GradientColor is the tint to blend over the blur.
            AccentFlags = state == AccentState.AcrylicBlurBehind ? 2 : 0,
            GradientColor = abgr,
            AnimationId = 0
        };

        int size = Marshal.SizeOf(policy);
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, ptr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = ptr,
                SizeOfData = size
            };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
