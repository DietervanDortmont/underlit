using System;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Underlit.Sys;

/// <summary>
/// Live screen capture via Windows.Graphics.Capture (WGC). The OSD must call
/// SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) on its HWND first — that flag
/// is honoured by WGC (BitBlt and DXGI Output Duplication don't), so the captured
/// monitor frames will show the desktop *behind* the OSD with proper transparency
/// rather than a black silhouette.
///
/// Threading: WGC's FrameArrived fires on a free-threaded WGC worker thread.
/// We grab the texture, copy to a CPU-readable staging texture, map it, and store
/// the pixels in a shared byte[] (lock-protected). The caller's render loop on
/// the UI thread polls for the latest pixels.
///
/// .NET 8 + windows10.0.19041.0 WinRT marshaling notes:
///   • WinRT projected types (GraphicsCaptureItem, IDirect3DDevice, etc) MUST be
///     marshaled with WinRT.MarshalInspectable&lt;T&gt;.FromAbi — not the legacy
///     Marshal.GetObjectForIUnknown which returns a generic __ComObject and fails
///     to cast to the projected type.
///   • Casting a projected WinRT type to a [ComImport] COM interface (e.g. casting
///     IDirect3DSurface to IDirect3DDxgiInterfaceAccess) requires going via the
///     ABI pointer — get pointer with MarshalInspectable.FromManaged, QI, wrap.
/// </summary>
public sealed class WgcCapture : IDisposable
{
    public byte[]? LatestFrame { get; private set; }
    public int FrameWidth  { get; private set; }
    public int FrameHeight { get; private set; }
    public int FrameStride { get; private set; }
    /// <summary>Monotonic counter, bumped each time LatestFrame is replaced.
    /// Consumers compare against their last-seen value to skip redundant renders.</summary>
    public long FrameId    { get; private set; }
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

    public void StartForMonitor(IntPtr hMonitor)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WgcCapture));
        if (_running) return;

        Logger.Info("WGC: StartForMonitor begin, hMon=0x" + hMonitor.ToInt64().ToString("X"));

        // ---- 1. D3D11 device -----------------------------------------------
        IntPtr device, context;
        const uint FLAGS = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
        var featureLevels = new[] { 0xb000 /* 11_0 */ };
        int hr = D3D11CreateDevice(IntPtr.Zero, /*HARDWARE*/ 1, IntPtr.Zero, FLAGS,
                                    featureLevels, featureLevels.Length, 7,
                                    out device, out _, out context);
        if (hr != 0) throw new InvalidOperationException($"D3D11CreateDevice failed: 0x{hr:X8}");
        _d3dDevice = device;
        _d3dContext = context;
        Logger.Info("WGC: D3D11 device created");

        // ---- 2. Wrap as WinRT IDirect3DDevice ------------------------------
        Guid iidDxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        if (Marshal.QueryInterface(_d3dDevice, ref iidDxgiDevice, out IntPtr dxgiDevice) != 0)
            throw new InvalidOperationException("Could not QI for IDXGIDevice");

        try
        {
            hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out IntPtr graphicsDevice);
            if (hr != 0) throw new InvalidOperationException($"CreateDirect3D11DeviceFromDXGIDevice failed: 0x{hr:X8}");
            try
            {
                // CsWinRT marshalling: IInspectable* → projected IDirect3DDevice.
                _winrtDevice = MarshalInspectable<IDirect3DDevice>.FromAbi(graphicsDevice);
            }
            finally
            {
                Marshal.Release(graphicsDevice);
            }
        }
        finally
        {
            Marshal.Release(dxgiDevice);
        }
        Logger.Info("WGC: IDirect3DDevice wrapped");

        // ---- 3. GraphicsCaptureItem from monitor via interop COM -----------
        IntPtr factoryPtr = IntPtr.Zero;
        IntPtr hstr = IntPtr.Zero;
        const string itemClassName = "Windows.Graphics.Capture.GraphicsCaptureItem";
        try
        {
            hr = WindowsCreateString(itemClassName, itemClassName.Length, out hstr);
            if (hr != 0) throw new InvalidOperationException($"WindowsCreateString failed: 0x{hr:X8}");

            Guid iidInterop = typeof(IGraphicsCaptureItemInterop).GUID;
            hr = RoGetActivationFactory(hstr, ref iidInterop, out factoryPtr);
            if (hr != 0 || factoryPtr == IntPtr.Zero)
                throw new InvalidOperationException($"RoGetActivationFactory(IGraphicsCaptureItemInterop) failed: 0x{hr:X8}");

            // The factory pointer IS the interop interface (we asked for that IID).
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            Logger.Info("WGC: IGraphicsCaptureItemInterop obtained");

            // IGraphicsCaptureItem default-interface IID (NOT typeof(GraphicsCaptureItem).GUID
            // which under CsWinRT returns the runtime-class GUID — that's E_NOINTERFACE
            // when passed to CreateForMonitor; took a v0.6.2 → v0.6.3 round-trip to learn).
            Guid itemIID = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
            hr = interop.CreateForMonitor(hMonitor, ref itemIID, out IntPtr itemPtr);
            if (hr != 0 || itemPtr == IntPtr.Zero)
                throw new InvalidOperationException($"CreateForMonitor failed: 0x{hr:X8}");

            try
            {
                _item = MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
            }
            finally
            {
                Marshal.Release(itemPtr);
            }
            Logger.Info("WGC: GraphicsCaptureItem created");
        }
        finally
        {
            if (factoryPtr != IntPtr.Zero) Marshal.Release(factoryPtr);
            if (hstr != IntPtr.Zero) WindowsDeleteString(hstr);
        }

        // Init done — D3D + capture item are alive. The framepool + session are
        // created on demand in StartCapture() so the Win11 yellow border only
        // shows while we're ACTIVELY capturing (i.e. while the OSD is visible),
        // not 24/7.
        _running = true;
        Logger.Info("WGC: capture initialised (session not yet started)");
    }

    /// <summary>
    /// Begin actively capturing frames. Creates the framepool + session. The
    /// Win11 22H2+ yellow capture border appears WHILE this is running. Call
    /// StopCapture when you no longer need frames so the border goes away.
    /// </summary>
    public void StartCapture()
    {
        if (_disposed || !_running) return;
        if (_session != null) return;          // already capturing
        if (_winrtDevice == null || _item == null) return;

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _item.Size);
        _framePool.FrameArrived += OnFrameArrived;

        _session = _framePool.CreateCaptureSession(_item);
        try { _session.IsCursorCaptureEnabled = false; } catch { }
        TrySuppressCaptureBorder();
        _session.StartCapture();
        Logger.Info("WGC: StartCapture OK — frames flowing, border visible");
    }

    /// <summary>Stop capturing. Disposes the session + framepool, which removes
    /// the Win11 capture-border indicator. Safe to call multiple times.</summary>
    public void StopCapture()
    {
        if (_session == null && _framePool == null) return;
        try { _session?.Dispose(); } catch { }
        _session = null;
        try
        {
            if (_framePool != null) _framePool.FrameArrived -= OnFrameArrived;
            _framePool?.Dispose();
        } catch { }
        _framePool = null;
        Logger.Info("WGC: StopCapture — session disposed, border removed");
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

            // Get ID3D11Texture2D from the IDirect3DSurface.
            // The projected IDirect3DSurface implements (via QI) the COM interface
            // IDirect3DDxgiInterfaceAccess. To get the raw COM pointer we go via
            // the ABI: MarshalInspectable.FromManaged → QueryInterface.
            IntPtr surfaceAbi = MarshalInspectable<IDirect3DSurface>.FromManaged(frame.Surface);
            if (surfaceAbi == IntPtr.Zero) return;
            try
            {
                Guid iidAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
                if (Marshal.QueryInterface(surfaceAbi, ref iidAccess, out IntPtr accessPtr) != 0) return;
                try
                {
                    var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(accessPtr);
                    Guid iidTex2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
                    int hr = access.GetInterface(ref iidTex2D, out IntPtr texture);
                    if (hr != 0 || texture == IntPtr.Zero) return;
                    try
                    {
                        ProcessTexture(texture, w, h);
                    }
                    finally { Marshal.Release(texture); }
                }
                finally { Marshal.Release(accessPtr); }
            }
            finally { Marshal.Release(surfaceAbi); }
        }
        catch (Exception ex)
        {
            Logger.Warn("WGC frame handler failed", ex);
        }
        finally
        {
            (frame as IDisposable)?.Dispose();
        }
    }

    private void ProcessTexture(IntPtr texture, int w, int h)
    {
        EnsureStagingTexture(w, h);
        ID3D11DeviceContext_CopyResource(_d3dContext, _stagingTex, texture);

        D3D11_MAPPED_SUBRESOURCE mapped = default;
        int hr = ID3D11DeviceContext_Map(_d3dContext, _stagingTex, 0, /*D3D11_MAP_READ=1*/ 1, 0, ref mapped);
        if (hr != 0) return;
        try
        {
            int rowBytes = w * 4;
            int total = rowBytes * h;
            byte[] buf = LatestFrame != null && LatestFrame.Length >= total
                ? LatestFrame : new byte[total];

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
                FrameId++;
            }
        }
        finally
        {
            ID3D11DeviceContext_Unmap(_d3dContext, _stagingTex, 0);
        }

        FrameArrived?.Invoke();
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

    /// <summary>
    /// Try every known mechanism to remove the yellow WGC capture border.
    /// Win11 24H2+ supports it; older Win11 22H2/23H2 enforces it unconditionally.
    /// </summary>
    private void TrySuppressCaptureBorder()
    {
        // 1. Optional: Win11 24H2+ borderless access request. The API takes a
        //    GraphicsCaptureAccessKind; "Borderless" is value 1 in the enum.
        //    Calling this may pop a permission dialog the first time.
        try
        {
            var accessType = Type.GetType(
                "Windows.Graphics.Capture.GraphicsCaptureAccess, Windows, ContentType=WindowsRuntime");
            var kindType = Type.GetType(
                "Windows.Graphics.Capture.GraphicsCaptureAccessKind, Windows, ContentType=WindowsRuntime");
            if (accessType != null && kindType != null)
            {
                var requestMethod = accessType.GetMethod("RequestAccessAsync",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (requestMethod != null)
                {
                    object borderless = Enum.ToObject(kindType, 1);
                    requestMethod.Invoke(null, new[] { borderless });   // fire-and-forget
                    Logger.Info("WGC: requested borderless capture access");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Info($"WGC: GraphicsCaptureAccess.RequestAccessAsync unavailable ({ex.GetType().Name})");
        }

        // 2. Set the per-session IsBorderRequired flag (Win11 24H2+).
        try
        {
            var prop = typeof(GraphicsCaptureSession).GetProperty("IsBorderRequired");
            if (prop != null)
            {
                prop.SetValue(_session, false);
                Logger.Info("WGC: IsBorderRequired = false set");
            }
            else
            {
                Logger.Info("WGC: IsBorderRequired property not available on this build");
            }
        }
        catch (Exception ex)
        {
            Logger.Info($"WGC: setting IsBorderRequired threw ({ex.GetType().Name}: {ex.Message})");
        }
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        StopCapture();
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

    // ID3D11Device::CreateTexture2D — vtable slot 5
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTexture2DDelegate(IntPtr self, ref D3D11_TEXTURE2D_DESC desc,
                                                  IntPtr initialData, out IntPtr texture);
    private static int ID3D11Device_CreateTexture2D(IntPtr device, ref D3D11_TEXTURE2D_DESC desc,
                                                     IntPtr initialData, out IntPtr texture)
    {
        IntPtr vtable = Marshal.ReadIntPtr(device);
        IntPtr fn = Marshal.ReadIntPtr(vtable, 5 * IntPtr.Size);
        var del = Marshal.GetDelegateForFunctionPointer<CreateTexture2DDelegate>(fn);
        return del(device, ref desc, initialData, out texture);
    }

    // ID3D11DeviceContext::CopyResource — vtable slot 47
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void CopyResourceDelegate(IntPtr self, IntPtr dst, IntPtr src);
    private static void ID3D11DeviceContext_CopyResource(IntPtr ctx, IntPtr dst, IntPtr src)
    {
        IntPtr vtable = Marshal.ReadIntPtr(ctx);
        IntPtr fn = Marshal.ReadIntPtr(vtable, 47 * IntPtr.Size);
        var del = Marshal.GetDelegateForFunctionPointer<CopyResourceDelegate>(fn);
        del(ctx, dst, src);
    }

    // ID3D11DeviceContext::Map — vtable slot 14
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

    // ID3D11DeviceContext::Unmap — vtable slot 15
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
}
