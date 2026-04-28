using System;
using System.Runtime.InteropServices;

namespace Underlit.Sys;

/// <summary>
/// Live screen capture via the Windows Magnification API. No yellow privacy
/// border (unlike Windows.Graphics.Capture on Win11 22H2+). Uses
/// <c>MagSetWindowFilterList</c> to exclude the OSD from captured frames so we
/// don't need cloak/uncloak tricks or WDA flags.
///
/// How it works:
///   1. MagInitialize boots the magnification subsystem.
///   2. We register a tiny custom window class and create an invisible
///      (WS_EX_LAYERED, alpha=0) host window positioned off-screen.
///   3. We create a child window of class "Magnifier" — Windows automatically
///      renders the configured source rect into this window's client area.
///   4. MagSetWindowFilterList tells the magnifier to skip our OSD HWND.
///   5. Each frame: MagSetWindowSource updates the source rect, then we
///      BitBlt from the magnifier's HDC to a memory DC and read the pixels.
///
/// All blocking — no async events to marshal between threads. Caller drives
/// frame timing (LiveGlassController calls CaptureFrame on every
/// CompositionTarget.Rendering tick).
/// </summary>
public sealed class MagCapture : IDisposable
{
    private const string MagWindowClass = "Magnifier";
    private const string HostWindowClass = "UnderlitMagHost";

    // Public output (matches WgcCapture's interface where convenient).
    public byte[]? LatestFrame { get; private set; }
    public int FrameWidth  { get; private set; }
    public int FrameHeight { get; private set; }
    public int FrameStride { get; private set; }
    public long FrameId    { get; private set; }
    public readonly object FrameLock = new();
    public event Action? FrameArrived;

    private bool _initialized;
    private bool _disposed;
    private bool _capturing;
    private IntPtr _hostHwnd;
    private IntPtr _magHwnd;
    private IntPtr _osdHwnd;
    private IntPtr _hostClassAtom;
    private WndProcDelegate? _wndProc;        // GC-rooted so the function pointer stays valid
    private GCHandle _wndProcHandle;

    // Reusable BitBlt readback resources.
    private IntPtr _memDC;
    private IntPtr _memBitmap;
    private IntPtr _oldBitmap;
    private int _bufW;
    private int _bufH;
    private byte[]? _scratch;

    public bool IsCapturing => _capturing && !_disposed;

    /// <summary>
    /// Set up Magnification + the off-screen host/magnifier windows. Returns false
    /// if the OS doesn't support the Magnification API or any setup step fails.
    /// </summary>
    public bool Initialize(IntPtr osdHwnd, int captureWidth, int captureHeight)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MagCapture));
        if (_initialized) return true;

        Logger.Info("MagCapture: Initialize begin");

        try
        {
            if (!MagInitialize())
            {
                Logger.Warn("MagCapture: MagInitialize() returned FALSE");
                return false;
            }
            Logger.Info("MagCapture: MagInitialize OK");

            RegisterHostClass();
            Logger.Info("MagCapture: host class registered");

            int w = Math.Max(1, captureWidth);
            int h = Math.Max(1, captureHeight);

            // Host window — invisible (alpha=0) layered popup, off-screen, no taskbar.
            _hostHwnd = CreateWindowExW(
                WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                HostWindowClass, "UnderlitMagHost", WS_POPUP,
                -32000, -32000, w, h,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
            if (_hostHwnd == IntPtr.Zero)
                throw new InvalidOperationException("CreateWindowEx(host) failed: " + Marshal.GetLastWin32Error());
            // FULLY OPAQUE alpha so the DWM compositor actually renders the window
            // (and its magnifier child). Earlier alpha=0 made the compositor skip
            // it entirely → magnifier never painted → BitBlt got empty pixels.
            // Window is positioned at (-32000, -32000) so the user can't see it.
            SetLayeredWindowAttributes(_hostHwnd, 0, 255, LWA_ALPHA);
            Logger.Info("MagCapture: host hwnd created 0x" + _hostHwnd.ToInt64().ToString("X"));

            _magHwnd = CreateWindowExW(0, MagWindowClass, "MagChild",
                WS_CHILD | WS_VISIBLE,
                0, 0, w, h,
                _hostHwnd, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
            if (_magHwnd == IntPtr.Zero)
                throw new InvalidOperationException("CreateWindowEx(Magnifier) failed: " + Marshal.GetLastWin32Error());
            Logger.Info("MagCapture: magnifier hwnd created 0x" + _magHwnd.ToInt64().ToString("X"));

            // Exclude our OSD from the captured content. This is the reason we
            // chose Magnification — built-in HWND filtering, no cloaking required.
            _osdHwnd = osdHwnd;
            IntPtr[] filterList = { osdHwnd };
            if (!MagSetWindowFilterList(_magHwnd, MW_FILTERMODE_EXCLUDE, 1, filterList))
                Logger.Warn("MagCapture: MagSetWindowFilterList returned FALSE");
            else
                Logger.Info("MagCapture: filter list set (OSD excluded)");

            // Show the host so the magnifier child can render. SW_SHOWNOACTIVATE so
            // it doesn't steal focus.
            ShowWindow(_hostHwnd, SW_SHOWNOACTIVATE);

            _initialized = true;
            Logger.Info("MagCapture: Initialize complete");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("MagCapture: Initialize failed", ex);
            TeardownWindows();
            try { MagUninitialize(); } catch { }
            return false;
        }
    }

    public void StartCapture()
    {
        if (!_initialized || _disposed) return;
        _capturing = true;
        Logger.Info("MagCapture: StartCapture (no border, no indicator)");
    }

    public void StopCapture()
    {
        _capturing = false;
        Logger.Info("MagCapture: StopCapture");
    }

    /// <summary>
    /// Synchronously read the desktop area `(srcX, srcY, w, h)` (excluding the
    /// OSD) into LatestFrame. Returns true on success. Called from the WPF
    /// CompositionTarget.Rendering tick.
    /// </summary>
    public bool CaptureFrame(int srcX, int srcY, int w, int h)
    {
        if (!_initialized || _disposed || !_capturing) return false;
        if (w <= 0 || h <= 0) return false;

        try
        {
            // Make sure the magnifier window is the right size — Mag draws into its
            // client area at 1:1 with the source rect (we don't scale).
            if (w != _bufW || h != _bufH)
            {
                MoveWindow(_magHwnd, 0, 0, w, h, false);
                MoveWindow(_hostHwnd, -32000, -32000, w, h, false);
                ReallocReadbackResources(w, h);
            }

            var src = new RECT { left = srcX, top = srcY, right = srcX + w, bottom = srcY + h };
            if (!MagSetWindowSource(_magHwnd, src))
            {
                Logger.Warn("MagCapture: MagSetWindowSource failed");
                return false;
            }

            // FORCE SYNCHRONOUS PAINT. MagSetWindowSource only queues the source change;
            // the magnifier window paints asynchronously on its own WM_PAINT cycle.
            // Without RedrawWindow we'd BitBlt before new content is rendered and read
            // stale (or empty) pixels. RDW_UPDATENOW makes WM_PAINT happen immediately.
            RedrawWindow(_magHwnd, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_UPDATENOW);

            // BitBlt the magnifier's content into our memory DC.
            IntPtr magDC = GetDC(_magHwnd);
            if (magDC == IntPtr.Zero) return false;
            try
            {
                BitBlt(_memDC, 0, 0, w, h, magDC, 0, 0, SRCCOPY);
            }
            finally
            {
                ReleaseDC(_magHwnd, magDC);
            }

            // Pull pixels out of the memory bitmap as BGRA.
            var bi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h,        // negative => top-down DIB
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,    // BI_RGB
                }
            };
            int needed = w * h * 4;
            byte[] buf = (_scratch != null && _scratch.Length >= needed) ? _scratch : (_scratch = new byte[needed]);
            int copied = GetDIBits(_memDC, _memBitmap, 0, (uint)h, buf, ref bi, 0);
            if (copied <= 0)
            {
                Logger.Warn("MagCapture: GetDIBits returned 0");
                return false;
            }

            lock (FrameLock)
            {
                LatestFrame = buf;
                FrameWidth  = w;
                FrameHeight = h;
                FrameStride = w * 4;
                FrameId++;
                if (FrameId == 1)
                {
                    // One-shot diagnostic: log a sample pixel from the centre of the
                    // first captured frame so we can confirm BitBlt actually pulled
                    // real desktop content (not a black/empty frame).
                    int cx = w / 2, cy = h / 2;
                    int idx = cy * (w * 4) + cx * 4;
                    Logger.Info($"MagCapture: first frame captured ({w}x{h}); centre pixel BGRA = ({buf[idx]}, {buf[idx+1]}, {buf[idx+2]}, {buf[idx+3]})");
                }
            }
            FrameArrived?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("MagCapture: CaptureFrame failed", ex);
            return false;
        }
    }

    private void ReallocReadbackResources(int w, int h)
    {
        // Tear down existing.
        if (_memDC != IntPtr.Zero && _oldBitmap != IntPtr.Zero)
        {
            SelectObject(_memDC, _oldBitmap);
            _oldBitmap = IntPtr.Zero;
        }
        if (_memBitmap != IntPtr.Zero) { DeleteObject(_memBitmap); _memBitmap = IntPtr.Zero; }
        if (_memDC != IntPtr.Zero) { DeleteDC(_memDC); _memDC = IntPtr.Zero; }

        IntPtr screenDC = GetDC(IntPtr.Zero);
        try
        {
            _memDC = CreateCompatibleDC(screenDC);
            _memBitmap = CreateCompatibleBitmap(screenDC, w, h);
            _oldBitmap = SelectObject(_memDC, _memBitmap);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDC);
        }
        _bufW = w;
        _bufH = h;
    }

    private void RegisterHostClass()
    {
        _wndProc = HostWndProc;
        _wndProcHandle = GCHandle.Alloc(_wndProc);
        IntPtr fn = Marshal.GetFunctionPointerForDelegate(_wndProc);

        var wc = new WNDCLASSEX
        {
            cbSize       = Marshal.SizeOf<WNDCLASSEX>(),
            style        = 0,
            lpfnWndProc  = fn,
            cbClsExtra   = 0,
            cbWndExtra   = 0,
            hInstance    = GetModuleHandleW(null),
            hIcon        = IntPtr.Zero,
            hCursor      = IntPtr.Zero,
            hbrBackground = IntPtr.Zero,
            lpszMenuName = IntPtr.Zero,
            lpszClassName = Marshal.StringToHGlobalUni(HostWindowClass),
            hIconSm      = IntPtr.Zero,
        };
        try
        {
            ushort atom = RegisterClassExW(ref wc);
            if (atom == 0)
            {
                int err = Marshal.GetLastWin32Error();
                if (err != 1410) // ERROR_CLASS_ALREADY_EXISTS — fine
                    throw new InvalidOperationException("RegisterClassEx failed: " + err);
            }
            _hostClassAtom = (IntPtr)atom;
        }
        finally
        {
            Marshal.FreeHGlobal(wc.lpszClassName);
        }
    }

    private static IntPtr HostWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        => DefWindowProcW(hwnd, msg, wParam, lParam);

    private void TeardownWindows()
    {
        if (_magHwnd != IntPtr.Zero) { DestroyWindow(_magHwnd); _magHwnd = IntPtr.Zero; }
        if (_hostHwnd != IntPtr.Zero) { DestroyWindow(_hostHwnd); _hostHwnd = IntPtr.Zero; }
        if (_memDC != IntPtr.Zero && _oldBitmap != IntPtr.Zero)
        { SelectObject(_memDC, _oldBitmap); _oldBitmap = IntPtr.Zero; }
        if (_memBitmap != IntPtr.Zero) { DeleteObject(_memBitmap); _memBitmap = IntPtr.Zero; }
        if (_memDC != IntPtr.Zero) { DeleteDC(_memDC); _memDC = IntPtr.Zero; }
        if (_wndProcHandle.IsAllocated) _wndProcHandle.Free();
        _wndProc = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopCapture();
        TeardownWindows();
        try { if (_initialized) MagUninitialize(); } catch { }
        _initialized = false;
        Logger.Info("MagCapture: Dispose");
    }

    // ============================================================
    // P/Invoke
    // ============================================================
    private const int MW_FILTERMODE_EXCLUDE = 0;
    private const int WS_CHILD       = 0x40000000;
    private const int WS_VISIBLE     = 0x10000000;
    private const int WS_POPUP       = unchecked((int)0x80000000);
    private const int WS_EX_LAYERED  = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW  = 0x00000080;
    private const int WS_EX_NOACTIVATE  = 0x08000000;
    private const int WS_EX_TOPMOST     = 0x00000008;
    private const uint LWA_ALPHA = 0x2;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint SRCCOPY = 0xCC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public int biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public int biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
    {
        public int    cbSize;
        public int    style;
        public IntPtr lpfnWndProc;
        public int    cbClsExtra;
        public int    cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("magnification.dll", SetLastError = true)] private static extern bool MagInitialize();
    [DllImport("magnification.dll", SetLastError = true)] private static extern bool MagUninitialize();
    [DllImport("magnification.dll", SetLastError = true)] private static extern bool MagSetWindowSource(IntPtr hwnd, RECT rect);
    [DllImport("magnification.dll", SetLastError = true)] private static extern bool MagSetWindowFilterList(IntPtr hwnd, int dwFilterMode, int count, [In] IntPtr[] pHWND);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateWindowExW")]
    private static extern IntPtr CreateWindowExW(int dwExStyle, [MarshalAs(UnmanagedType.LPWStr)] string lpClassName, [MarshalAs(UnmanagedType.LPWStr)] string lpWindowName,
        int dwStyle, int X, int Y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);
    private const uint RDW_INVALIDATE = 0x1;
    private const uint RDW_UPDATENOW  = 0x100;
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "RegisterClassExW")]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    private static extern IntPtr GetModuleHandleW([MarshalAs(UnmanagedType.LPWStr)] string? lpModuleName);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr ho);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, byte[] lpvBits, ref BITMAPINFO lpbi, uint usage);
}
