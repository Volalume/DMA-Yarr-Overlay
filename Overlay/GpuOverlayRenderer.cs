using System;
using Vortice.DirectComposition;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Overlay;

// Owns the GPU-only presentation half of the overlay. It is deliberately used from
// the capture thread: no Bitmap, UI message, GDI DC, or CPU readback is involved.
internal sealed class GpuOverlayRenderer : IDisposable
{
    private readonly IDXGISwapChain1 _swapChain;
    private readonly ID3D11RenderTargetView[] _rtvs;
    private readonly IDCompositionDevice _composition;
    private readonly IDCompositionTarget _target;
    private readonly IDCompositionVisual _visual;
    private bool _disposed;

    private GpuOverlayRenderer(IDXGISwapChain1 swapChain, ID3D11RenderTargetView[] rtvs, IDCompositionDevice composition, IDCompositionTarget target, IDCompositionVisual visual)
    { _swapChain = swapChain; _rtvs = rtvs; _composition = composition; _target = target; _visual = visual; }

    public ID3D11RenderTargetView CurrentRenderTarget => _rtvs[0];

    public static GpuOverlayRenderer Create(ID3D11Device device, IDXGIFactory1 factory, IntPtr hwnd, int width, int height, int maximumFrameLatency)
    {
        Logger.Info($"GPU Native init: hwnd=0x{hwnd:X}, size={width}x{height}, latency={maximumFrameLatency}");
        using var factory2 = factory.QueryInterface<IDXGIFactory2>();
        var desc = new SwapChainDescription1
        {
            Width = (uint)width, Height = (uint)height, Format = Format.B8G8R8A8_UNorm,
            Stereo = false, SampleDescription = new SampleDescription(1, 0), BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2, Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Premultiplied, Flags = SwapChainFlags.None
        };
        Logger.Info("GPU Native init: CreateSwapChainForComposition");
        var swapChain = factory2.CreateSwapChainForComposition(device, desc, null);
        Logger.Info("GPU Native init: swap chain created");
        using (var latencyDevice = device.QueryInterface<IDXGIDevice1>()) { latencyDevice.SetMaximumFrameLatency((uint)Math.Clamp(maximumFrameLatency, 1, 3)).CheckError(); }
        // Flip-sequential exposes buffer 0 as the writable back buffer. DXGI rotates it on Present.
        var rtvs = new ID3D11RenderTargetView[1];
        for (var i = 0; i < rtvs.Length; i++)
        {
            Logger.Info($"GPU Native init: create RTV {i}");
            using var buffer = swapChain.GetBuffer<ID3D11Texture2D>((uint)i);
            rtvs[i] = device.CreateRenderTargetView(buffer);
        }
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        var composition = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        Logger.Info("GPU Native init: DirectComposition device created");
        composition.CreateTargetForHwnd(hwnd, true, out var target).CheckError();
        Logger.Info("GPU Native init: HWND target created");
        composition.CreateVisual(out var visual).CheckError();
        visual.SetContent(swapChain).CheckError();
        target.SetRoot(visual).CheckError();
        composition.Commit().CheckError();
        Logger.Info("GPU Native init: committed");
        return new GpuOverlayRenderer(swapChain, rtvs, composition, target, visual);
    }

    public PresentOutcome Present()
    {
        var result = _swapChain.Present(0, PresentFlags.DoNotWait);
        if (result.Code == Vortice.DXGI.ResultCode.WasStillDrawing.Code) return new PresentOutcome(result.Code, true, default, false);
        result.CheckError();
        var statsResult = _swapChain.GetFrameStatistics(out var stats);
        return new PresentOutcome(result.Code, false, stats, statsResult.Success);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var rtv in _rtvs) rtv.Dispose();
        _visual.Dispose(); _target.Dispose(); _composition.Dispose(); _swapChain.Dispose();
    }
}
internal readonly record struct PresentOutcome(int ResultCode, bool Backlogged, Vortice.DXGI.FrameStatistics Statistics, bool HasStatistics);
