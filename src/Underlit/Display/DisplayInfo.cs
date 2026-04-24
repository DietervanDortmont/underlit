using System;

namespace Underlit.Display;

/// <summary>
/// Public, plain monitor bounds in physical pixels (virtual-screen coordinates).
/// Separate from <see cref="NativeMethods.RECT"/> so that DisplayInfo (which is public)
/// doesn't need to expose an internal interop type.
/// </summary>
public readonly record struct MonitorBounds(int Left, int Top, int Width, int Height)
{
    public int Right  => Left + Width;
    public int Bottom => Top + Height;
}

/// <summary>
/// Snapshot of a single monitor at enumeration time.
/// Holds the HMONITOR plus bounds in physical pixels (virtual-screen coords).
/// Device name (\\.\DISPLAY1 style) identifies the monitor for gamma / DC access.
/// </summary>
public sealed class DisplayInfo
{
    public IntPtr HMonitor { get; init; }
    public string DeviceName { get; init; } = "";
    public MonitorBounds Bounds { get; init; }
    public bool IsPrimary { get; init; }

    /// <summary>Stable ID so settings can be saved per-monitor across sessions.</summary>
    public string StableId => DeviceName; // \\.\DISPLAY1 is stable enough for a first cut.

    public override string ToString()
        => $"{DeviceName} {(IsPrimary ? "(primary) " : "")}{Bounds.Width}x{Bounds.Height} @ ({Bounds.Left},{Bounds.Top})";
}
