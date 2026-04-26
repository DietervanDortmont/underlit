// ─────────────────────────────────────────────────────────────────────────────
// DEPRECATED in v0.2.3.
//
// This file used to wrap SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) so the
// OSD wouldn't appear in its own BitBlt captures during the live-glass loop.
//
// Turns out WDA_EXCLUDEFROMCAPTURE is an anti-DRM feature: it makes the window
// appear as a *black rectangle* in any screen capture, not "see-through". So a
// per-frame BitBlt loop captured black, blurred black, and the OSD ended up
// looking permanently dark and opaque (and the user couldn't take screenshots
// of it either). Removed from the code path.
//
// Future: the proper fix is Windows.Graphics.Capture or DXGI Output Duplication
// (which return the desktop minus our window via DWM). That's a v0.3 task.
// ─────────────────────────────────────────────────────────────────────────────

namespace Underlit.Sys;

internal static class WindowDisplayAffinity_Deprecated
{
    // Intentionally empty. See header comment.
}
