using System;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Underlit.Sys;

/// <summary>
/// Live screen capture via Windows.Graphics.Capture (WGC). Returns the latest
/// captured monitor frame as a BGRA byte buffer. The OSD is excluded from the
/// captured frames via SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) — this
/// flag IS honoured by WGC (it isn't by BitBlt/DXGI), so the captured monitor
/// shows the desktop *behind* the OSD with proper transparency rather than a
/// black silhouette.
///
/// Threading: WGC's FrameArrived fires on a free-threaded WGC worker thread.
/// We grab the texture, copy to a CPU-readable staging texture, map it,
/// and store the pixels in a shared byte[] (lock-protected). The caller's
/// render loop on the UI thread polls for the latest pixels.
///
/// LIFECYCLE: Start() boots the D3D11 + WGC stack and begins receiving frames.
/// Stop() ends the session and disposes resources. Any failure during Start()
/// throws; caller should catch and fall back to the static capture path.
/// </summary>
public sealed class WgcCapture : IDisposable
{
    // Public output: latest BGRA frame and its dimensions. Lock _frameLock to read.
    public byte[]? LatestFrame { get; private set; }
    public int FrameWidth  { get; private set; }
    public int FrameHeight { get; private set; }
    public int FrameStride { get; private set; }
    public readonly object FrameLock = new();
    public event Action? FrameArrived;

    private GraphicsCaptureItem?              _item;
    private Direct3D11CaptureFramePool?       _framePool;
    private GraphicsCaptureSession?           _session;
    private IDirect3DDevice?                  _winrtDevice;
    private IntPtr                            _d3dDevice;
    private IntPtr                            _d3dContext;
    private IntPtr                            _stagingTex;
    private int                               _stagingW;
    private int                               _stagingH;

    private bool _disposed;
    private bool _running;

    public bool IsRunning => _running && !_disposed;

    /// <summary>
    /// Start capturing the monitor that contains the given HWND. Throws if the
    /// platform doesn't support WGC or any setup step fails.
    /// </summary>
    public void StartForMonitor(IntPtr hMonitor)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WgcCapture));
        if (_running) return;

        // 1. Create a D3D11 device (BGRA support so we can copy to CPU-readable texture).
        IntPtr device, context;
        const uint FLAGS = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
        var featureLevels = new[] { 0xb000 /* 11_0 */ };
        int hr = D3D11CreateDevice(IntPtr.Zero, /*HARDWARE*/ 1, IntPtr.Zero, FLAGS,
                                    featureLevels, featureLevels.Length, 7 /*SDK_VERSION*/,
                                    out device, out _, out context);
        if (hr != 0) throw new InvalidOperationException($"D3D11CreateDevice failed: 0x{hr:X8}");
        _d3dDevice = device;
        _d3dContext = context;

        // 2. Get IDXGIDevice from the D3D11 device.
        Guid iidDxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        if (Marshal.QueryInterface(_d3dDevice, ref iidDxgiDevice, out IntPtr dxgiDevice) != 0)
            throw new InvalidOperationException("Could not QI for IDXGIDevice");

        // 3. Wrap as a WinRT IDirect3DDevice.
        try
        {
            hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out IntPtr graphicsDevice);
            if (hr != 0) throw new InvalidOperationException($"CreateDirect3D11DeviceFromDXGIDevice failed: 0x{hr:X8}");
            _winrtDevice = MarshalInterfaceFromIntPtr<IDirect3DDevice>(graphicsDevice);
        }
        finally
        {
            Marshal.Release(dxgiDevice);
        }

        // 4. GraphicsCaptureItem from the monitor handle (via interop COM interface).
        Guid itemIID = typeof(GraphicsCaptureItem).GUID;
        var interop = GetActivationFactory<IGraphicsCaptureItemInterop>(
            "Windows.Graphics.Capture.GraphicsCaptureItem");
        hr = interop.CreateForMonitor(hMonitor, ref itemIID, out IntPtr itemPtr);
        if (hr != 0) throw new InvalidOperationException($"CreateForMonitor failed: 0x{hr:X8}");
        _item = MarshalInterfaceFromIntPtr<GraphicsCaptureItem>(itemPtr);

        // 5. Frame pool — free-threaded so FrameArrived fires off-UI.
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _item.Size);
        _framePool.FrameArrived += OnFrameArrived;

        // 6. Session.
        _session = _framePool.CreateCaptureSession(_item);
        try { _session.IsCursorCaptureEnabled = false; } catch { /* older WGC */ }
        try { _session.IsBorderRequired = false;       } catch { /* Win11 24H2+ only */ }

        _session.StartCapture();
        _running = true;
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_disposed) return;
        Direct3D11CaptureFrame? frame = null;
        try
        {
            frame = sender.TryGetNextFrame();
            if (frame == null) return;

            int w = frame.ContentSize.Width;
            int h = frame.ContentSize.Height;
            if (w <= 0 || h <= 0) return;

            // Get ID3D11Texture2D from the IDirect3DSurface via DXGI interop.
            IntPtr texture;
            Guid iidTex2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
            var access = (IDirect3DDxgiInterfaceAccess)(object)frame.Surface;
            access.GetInterface(ref iidTex2D, out texture);
            if (texture == IntPtr.Zero) return;

            try
            {
                EnsureStagingTexture(w, h);
                // Copy framePool texture → CPU-readable staging.
                ID3D11DeviceContext_CopyResource(_d3dContext, _stagingTex, texture);

                // Map staging.
                D3D11_MAPPED_SUBRESOURCE mapped = default;
                int hr = ID3D11DeviceContext_Map(_d3dContext, _stagingTex, 0,
                                                  /*D3D11_MAP_READ=1*/ 1, 0, ref mapped);
                if (hr != 0) return;
                try
                {
                    int rowBytes = w * 4;
                    int total = rowBytes * h;
                    byte[] buf = LatestFrame != null && LatestFrame.Length >= total
                        ? LatestFrame : new byte[total];

                    // Copy pixels row by row (mapped.RowPitch may be larger than rowBytes).
                    for (int y = 0; y < h; y++)
                    {
                        Marshal.Copy(IntPtr.Add(mapped.pData, y * (int)mapped.RowPitch),
                                     buf, y * rowBytes, rowBytes);
                    }

                    lock (FrameLock)
                    {
                        LatestFrame = buf;
                        FrameWidth  = w;
                        FrameHeight = h;
                        FrameStride = rowBytes;
                    }
                }
                finally
                {
                    ID3D11DeviceContext_Unmap(_d3dContext, _stagingTex, 0);
                }

                FrameArrived?.Invoke();
            }
            finally
            {
                Marshal.Release(texture);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("WGC frame handler failed", ex);
        }
        finally
        {
            // Direct3D11CaptureFrame implements IDisposable.
            (frame as IDisposable)?.Dispose();
        }
    }

    private void EnsureStagingTexture(int w, int h)
    {
        if (_stagingTex != IntPtr.Zero && _stagingW == w && _stagingH == h) return;
        if (_stagingTex != IntPtr.Zero)
        {
            Marshal.Release(_stagingTex);
            _stagingTex = IntPtr.Zero;
        }
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)w, Height = (uint)h,
            MipLevels = 1, ArraySize = 1,
            Format = 87 /* DXGI_FORMAT_B8G8R8A8_UNORM */,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = 3 /* D3D11_USAGE_STAGING */,
            BindFlags = 0,
            CPUAccessFlags = 0x20000 /* D3D11_CPU_ACCESS_READ */,
            MiscFlags = 0,
        };
        int hr = ID3D11Device_CreateTexture2D(_d3dDevice, ref desc, IntPtr.Zero, out IntPtr tex);
        if (hr != 0) throw new InvalidOperationException($"CreateTexture2D(staging) failed: 0x{hr:X8}");
        _stagingTex = tex;
        _stagingW = w;
        _stagingH = h;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        try { _session?.Dispose(); } catch { } _session = null;
        try
        {
            if (_framePool != null) _framePool.FrameArrived -= OnFrameArrived;
            _framePool?.Dispose();
        } catch { } _framePool = null;
        _item = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        if (_stagingTex != IntPtr.Zero) { Marshal.Release(_stagingTex); _stagingTex = IntPtr.Zero; }
        if (_d3dContext != IntPtr.Zero) { Marshal.Release(_d3dContext); _d3dContext = IntPtr.Zero; }
        if (_d3dDevice  != IntPtr.Zero) { Marshal.Release(_d3dDevice);  _d3dDevice  = IntPtr.Zero; }
        _winrtDevice = null;
    }

    // ============================================================
    // P/Invoke + COM interop
    // ============================================================

    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;

    [DllImport("d3d11.dll", PreserveSig = true)]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter, int driverType, IntPtr software, uint flags,
        int[]? featureLevels, int featureLevelsCount, uint sdkVersion,
        out IntPtr ppDevice, out int featureLevel, out IntPtr ppContext);

    [DllImport("d3d11.dll", PreserveSig = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [StructLayout(LayoutKind.Sequential)] private struct DXGI_SAMPLE_DESC { public uint Count; public uint Quality; }
    [StructLayout(LayoutKind.Sequential)] private struct D3D11_TEXTURE2D_DESC
    {
        public uint Width, Height, MipLevels, ArraySize;
        public int Format;
        public DXGI_SAMPLE_DESC SampleDesc;
        public uint Usage;
        public uint BindFlags, CPUAccessFlags, MiscFlags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct D3D11_MAPPED_SUBRESOURCE
    {
        public IntPtr pData; public uint RowPitch; public uint DepthPitch;
    }

    // Manually invoke ID3D11Device::CreateTexture2D via vtable slot 5.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTexture2DDelegate(IntPtr self, ref D3D11_TEXTURE2D_DESC desc,
                                                  IntPtr initialData, out IntPtr texture);
    private static int ID3D11Device_CreateTexture2D(IntPtr device, ref D3D11_TEXTURE2D_DESC desc,
                                                     IntPtr initialData, out IntPtr texture)
    {
        IntPtr vtable = Marshal.ReadIntPtr(device);
        IntPtr fn = Marshal.ReadIntPtr(vtable, 5 * IntPtr.Size); // CreateTexture2D
        var del = Marshal.GetDelegateForFunctionPointer<CreateTexture2DDelegate>(fn);
        return del(device, ref desc, initialData, out texture);
    }

    // ID3D11DeviceContext::CopyResource — vtable slot 47.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void CopyResourceDelegate(IntPtr self, IntPtr dst, IntPtr src);
    private static void ID3D11DeviceContext_CopyResource(IntPtr ctx, IntPtr dst, IntPtr src)
    {
        IntPtr vtable = Marshal.ReadIntPtr(ctx);
        IntPtr fn = Marshal.ReadIntPtr(vtable, 47 * IntPtr.Size);
        var del = Marshal.GetDelegateForFunctionPointer<CopyResourceDelegate>(fn);
        del(ctx, dst, src);
    }

    // ID3D11DeviceContext::Map — vtable slot 14.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int MapDelegate(IntPtr self, IntPtr resource, uint subresource,
                                      int mapType, uint mapFlags, ref D3D11_MAPPED_SUBRESOURCE mapped);
    private static int ID3D11DeviceContext_Map(IntPtr ctx, IntPtr resource, uint sub, int mapType,
                                                uint flags, ref D3D11_MAPPED_SUBRESOURCE mapped)
    {
        IntPtr vtable = Marshal.ReadIntPtr(ctx);
        IntPtr fn = Marshal.ReadIntPtr(vtable, 14 * IntPtr.Size);
        var del = Marshal.GetDelegateForFunctionPointer<MapDelegate>(fn);
        return del(ctx, resource, sub, mapType, flags, ref mapped);
    }

    // ID3D11DeviceContext::Unmap — vtable slot 15.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void UnmapDelegate(IntPtr self, IntPtr resource, uint subresource);
    private static void ID3D11DeviceContext_Unmap(IntPtr ctx, IntPtr resource, uint sub)
    {
        IntPtr vtable = Marshal.ReadIntPtr(ctx);
        IntPtr fn = Marshal.ReadIntPtr(vtable, 15 * IntPtr.Size);
        var del = Marshal.GetDelegateForFunctionPointer<UnmapDelegate>(fn);
        del(ctx, resource, sub);
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr captureItem);
        [PreserveSig] int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr captureItem);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        [PreserveSig] int GetInterface(ref Guid iid, out IntPtr ppv);
    }

    [DllImport("api-ms-win-core-winrt-l1-1-0.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", PreserveSig = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out IntPtr hstring);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", PreserveSig = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    /// <summary>Get a typed activation-factory interface for a WinRT class.</summary>
    private static T GetActivationFactory<T>(string className) where T : class
    {
        WindowsCreateString(className, className.Length, out IntPtr hstr);
        try
        {
            Guid iid = typeof(T).GUID;
            int hr = RoGetActivationFactory(hstr, ref iid, out IntPtr factory);
            if (hr != 0 || factory == IntPtr.Zero)
                throw new InvalidOperationException($"RoGetActivationFactory failed: 0x{hr:X8}");
            try
            {
                return (T)Marshal.GetTypedObjectForIUnknown(factory, typeof(T));
            }
            finally { Marshal.Release(factory); }
        }
        finally { WindowsDeleteString(hstr); }
    }

    /// <summary>Wrap a raw IInspectable* pointer as the projected WinRT type T.</summary>
    private static T MarshalInterfaceFromIntPtr<T>(IntPtr ptr) where T : class
    {
        if (ptr == IntPtr.Zero) throw new ArgumentNullException(nameof(ptr));
        try
        {
            // .NET's built-in WinRT marshaller handles projected types when this
            // pointer is an IInspectable. GetObjectForIUnknown returns the runtime
            // object; cast to the projection type.
            return (T)Marshal.GetObjectForIUnknown(ptr);
        }
        finally
        {
            Marshal.Release(ptr);
        }
    }
}
