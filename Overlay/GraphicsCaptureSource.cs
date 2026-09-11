using System;
using System.Runtime.InteropServices;
using TerraFX.Interop.Windows;
using TerraWinRT = TerraFX.Interop.WinRT.WinRT;
using TerraHString = TerraFX.Interop.WinRT.HSTRING;
using TerraInspectable = TerraFX.Interop.WinRT.IInspectable;
using TerraCaptureInterop = TerraFX.Interop.WinRT.IGraphicsCaptureItemInterop;
using TerraDxgiAccess = TerraFX.Interop.WinRT.IDirect3DDxgiInterfaceAccess;
using Vortice.Direct3D11;
using VorticeTexture2D = Vortice.Direct3D11.ID3D11Texture2D;
using TerraDxgiDevice = TerraFX.Interop.DirectX.IDXGIDevice;
using WinRtDirect3DDevice = Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice;
using WinRtDirect3DSurface = Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface;
using WinRtCaptureItem = Windows.Graphics.Capture.GraphicsCaptureItem;
using WinRtCaptureFramePool = Windows.Graphics.Capture.Direct3D11CaptureFramePool;
using WinRtCaptureSession = Windows.Graphics.Capture.GraphicsCaptureSession;
using WinRtPixelFormat = Windows.Graphics.DirectX.DirectXPixelFormat;

#pragma warning disable CA1416

namespace Overlay;

// Windows Graphics Capture source used only for Hardware Protection mode. It keeps
// the protected output swap-chain unchanged while avoiding Desktop Duplication's
// invalidation when Parsec attaches to a virtual display.
internal sealed unsafe class GraphicsCaptureSource : IDisposable
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid GraphicsCaptureItemInteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid DxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private readonly WinRtCaptureItem _item;
    private readonly WinRtDirect3DDevice _winrtDevice;
    private readonly WinRtCaptureFramePool _framePool;
    private readonly WinRtCaptureSession _session;
    private bool _disposed;

    private GraphicsCaptureSource(WinRtCaptureItem item, WinRtDirect3DDevice winrtDevice, WinRtCaptureFramePool framePool, WinRtCaptureSession session)
    {
        _item = item;
        _winrtDevice = winrtDevice;
        _framePool = framePool;
        _session = session;
    }

    public static GraphicsCaptureSource Create(ID3D11Device device, MonitorInfo monitor)
    {
        if (!WinRtCaptureSession.IsSupported())
            throw new NotSupportedException("Windows Graphics Capture is not supported on this Windows build.");

        var item = CreateItemForMonitor(monitor);
        try
        {
            using var dxgiDevice = device.QueryInterface<Vortice.DXGI.IDXGIDevice>();
            TerraInspectable* inspectable = null;
            var terraDxgi = (TerraDxgiDevice*)dxgiDevice.NativePointer;
            Check(TerraWinRT.CreateDirect3D11DeviceFromDXGIDevice(terraDxgi, &inspectable));
            try
            {
                var winrtDevice = WinRT.MarshalInterface<WinRtDirect3DDevice>.FromAbi((IntPtr)inspectable);
                var size = new Windows.Graphics.SizeInt32 { Width = item.Size.Width, Height = item.Size.Height };
                var pool = WinRtCaptureFramePool.CreateFreeThreaded(winrtDevice, WinRtPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                try
                {
                    var session = pool.CreateCaptureSession(item);
                    try
                    {
                        session.IsCursorCaptureEnabled = true;
                        session.StartCapture();
                        return new GraphicsCaptureSource(item, winrtDevice, pool, session);
                    }
                    catch { session.Dispose(); throw; }
                }
                catch { pool.Dispose(); throw; }
            }
            finally { Marshal.Release((IntPtr)inspectable); }
        }
        catch { throw; }
    }

    public VorticeTexture2D? TryGetNextTexture()
    {
        using var frame = _framePool.TryGetNextFrame();
        if (frame is null) return null;
        var surface = frame.Surface;
        // Use the C#/WinRT native object reference. Marshal.GetIUnknownForObject
        // can return the projection identity rather than the underlying WinRT ABI
        // pointer, which makes IDirect3DDxgiInterfaceAccess appear unavailable.
        if (surface is not WinRT.IWinRTObject winRtSurface)
            throw new InvalidOperationException("WGC surface is not backed by a WinRT ABI object.");
        using var accessReference = winRtSurface.NativeObject.As(DxgiInterfaceAccessIid);
        try
        {
            var access = (TerraDxgiAccess*)accessReference.ThisPtr;
            var textureIid = typeof(VorticeTexture2D).GUID;
            void* texturePtr = null;
            Check(access->GetInterface(&textureIid, &texturePtr));
            return new VorticeTexture2D((IntPtr)texturePtr);
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80004002))
        {
            throw new InvalidOperationException("WGC surface does not expose IDirect3DDxgiInterfaceAccess (E_NOINTERFACE).", ex);
        }
    }

    private static WinRtCaptureItem CreateItemForMonitor(MonitorInfo monitor)
    {
        var point = new NativeMethods.Point(monitor.Bounds.Left + 1, monitor.Bounds.Top + 1);
        var hmonitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MonitorDefaultToNearest);
        if (hmonitor == IntPtr.Zero) throw new InvalidOperationException($"Monitor handle unavailable for {monitor.DeviceName}.");

        var className = WinRT.MarshalString.FromManaged("Windows.Graphics.Capture.GraphicsCaptureItem");
        try
        {
            TerraInspectable* factoryObject = null;
            var factoryIid = GraphicsCaptureItemInteropIid;
            Check(TerraWinRT.RoGetActivationFactory((TerraHString)className, &factoryIid, (void**)&factoryObject));
            try
            {
                var interop = (TerraCaptureInterop*)factoryObject;
                var itemIid = GraphicsCaptureItemIid;
                void* itemObject = null;
                Check(interop->CreateForMonitor((HMONITOR)hmonitor, &itemIid, &itemObject));
                try { return WinRtCaptureItem.FromAbi((IntPtr)itemObject); }
                finally { Marshal.Release((IntPtr)itemObject); }
            }
            finally { Marshal.Release((IntPtr)factoryObject); }
        }
        finally { WinRT.MarshalString.DisposeAbi(className); }
    }

    private static void Check(HRESULT hr)
    {
        if ((int)hr < 0) Marshal.ThrowExceptionForHR((int)hr);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
        _framePool.Dispose();
    }
}
#pragma warning restore CA1416
