using System;
using System.Diagnostics;
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
    private readonly ID3D11DeviceContext3? _protectedContext;
    private readonly ID3D11Device _device; // Borrowed; CaptureContext outlives this renderer.
    private long _nextProtectionCheck;
    public string ProtectionDetails { get; }
    private bool _disposed;

    private GpuOverlayRenderer(ID3D11Device device, IDXGISwapChain1 swapChain, ID3D11RenderTargetView[] rtvs, IDCompositionDevice composition, IDCompositionTarget target, IDCompositionVisual visual, ID3D11DeviceContext3? protectedContext, string protectionDetails)
    { _device = device; _swapChain = swapChain; _rtvs = rtvs; _composition = composition; _target = target; _visual = visual; _protectedContext = protectedContext; ProtectionDetails = protectionDetails; }

    public ID3D11RenderTargetView CurrentRenderTarget => _rtvs[0];

    public static GpuOverlayRenderer Create(ID3D11Device device, ID3D11DeviceContext context, IDXGIFactory1 factory, IntPtr hwnd, int width, int height, int maximumFrameLatency, GpuProtectionMode protection)
    {
        Logger.Info($"GPU Native init: hwnd=0x{hwnd:X}, size={width}x{height}, latency={maximumFrameLatency}, protection={protection}");
        if (!Enum.IsDefined(protection)) throw new ArgumentOutOfRangeException(nameof(protection));
        var hardwareRequired = protection == GpuProtectionMode.HardwareRequired;
        using var factory2 = factory.QueryInterface<IDXGIFactory2>();
        var desc = new SwapChainDescription1
        {
            Width = (uint)width, Height = (uint)height, Format = Format.B8G8R8A8_UNorm,
            Stereo = false, SampleDescription = new SampleDescription(1, 0), BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2, Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Premultiplied,
            Flags = hardwareRequired ? SwapChainFlags.HwProtected | SwapChainFlags.DisplayOnly : SwapChainFlags.None
        };
        IDXGISwapChain1? swapChain = null;
        IDCompositionDevice? composition = null;
        IDCompositionTarget? target = null;
        IDCompositionVisual? visual = null;
        ID3D11DeviceContext3? protectedContext = null;
        var rtvs = new ID3D11RenderTargetView[1];
        var protectionDetails = "Off";
        try
        {
            if (hardwareRequired)
            {
                var version = GpuProtectionSupport.RequireModernDriver(device);
                protectedContext = context.QueryInterface<ID3D11DeviceContext3>();
                protectedContext.HardwareProtectionState = true;
                if (!protectedContext.HardwareProtectionState)
                    throw new NotSupportedException("D3D11 context did not enable hardware protection.");
                // Keep the context inside one protected command session for the
                // lifetime of the swap chain. Toggling this state for every moving
                // frame can invalidate Desktop Duplication on some WDDM drivers.
                protectionDetails = $"WDDM {version / 1000}.{version % 1000 / 100}; HW buffers + display-only flags accepted; protected command state retained. Not capture-tested or attested.";
            }
            // Exact composition format/alpha/flags are the support probe. Never retry
            // without protection flags if this fails (including on virtual adapters).
            swapChain = factory2.CreateSwapChainForComposition(device, desc, null);
            if ((swapChain.Description1.Flags & desc.Flags) != desc.Flags)
                throw new NotSupportedException("DXGI did not retain the requested protection flags.");
            using (var latencyDevice = device.QueryInterface<IDXGIDevice1>())
                latencyDevice.SetMaximumFrameLatency((uint)Math.Clamp(maximumFrameLatency, 1, 3)).CheckError();
            // Validate every buffer, not just the current render target.
            for (uint i = 0; i < desc.BufferCount; i++)
            {
                using var buffer = swapChain.GetBuffer<ID3D11Texture2D>(i);
                if (hardwareRequired) ValidateProtectedBuffer(buffer.Description, i);
                // Flip-sequential exposes buffer 0 as the writable back buffer.
                if (i == 0) rtvs[0] = device.CreateRenderTargetView(buffer);
            }
            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            composition = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
            composition.CreateTargetForHwnd(hwnd, true, out target).CheckError();
            composition.CreateVisual(out visual).CheckError();
            visual.SetContent(swapChain).CheckError();
            target.SetRoot(visual).CheckError();
            composition.Commit().CheckError();
            Logger.Info($"GPU Native init: committed; {protectionDetails}");
            return new GpuOverlayRenderer(device, swapChain, rtvs, composition, target, visual, protectedContext, protectionDetails);
        }
        catch
        {
            // Creation can fail at any point on unsupported GPUs. Release everything
            // before the user retries; do not leave a partially attached visual.
            if (visual is not null) visual.SetContent(null);
            if (target is not null) target.SetRoot(null);
            if (composition is not null) composition.Commit();
            if (protectedContext is not null) protectedContext.HardwareProtectionState = false;
            foreach (var rtv in rtvs) rtv?.Dispose();
            visual?.Dispose(); target?.Dispose(); composition?.Dispose(); swapChain?.Dispose(); protectedContext?.Dispose();
            throw;
        }
    }

    public void BeginDraw()
    {
        if (_protectedContext is null) return;
        if (!_protectedContext.HardwareProtectionState)
            throw new InvalidOperationException("D3D11 hardware protection state was lost.");
    }

    public void EndDraw() { }

    public string DescribeRuntimeState()
    {
        var desc = _swapChain.Description1;
        var removed = _device.DeviceRemovedReason;
        var protectedState = _protectedContext is not null && _protectedContext.HardwareProtectionState;
        return $"swapFlags={desc.Flags}; buffers={desc.BufferCount}; effect={desc.SwapEffect}; alpha={desc.AlphaMode}; deviceRemoved=0x{removed.Code:X8}; protectionState={protectedState}";
    }

    // Runs on the owning capture thread, including when the source is static.
    // Runs on the owning capture thread, including when the source is static.
    // These are driver-reported invariants, not attestation or a detector for other
    // programs taking captures.
    public void ValidateProtectionIfDue()
    {
        if (_protectedContext is null) return;
        var now = Stopwatch.GetTimestamp();
        if (now < _nextProtectionCheck) return;
        _nextProtectionCheck = now + Stopwatch.Frequency;
        _device.DeviceRemovedReason.CheckError();
        if (!_protectedContext.HardwareProtectionState)
            throw new InvalidOperationException("D3D11 hardware protection state was lost.");
        var desc = _swapChain.Description1;
        const SwapChainFlags required = SwapChainFlags.HwProtected | SwapChainFlags.DisplayOnly;
        if ((desc.Flags & required) != required || desc.BufferCount != 2 || desc.SwapEffect != SwapEffect.FlipSequential)
            throw new InvalidOperationException("Protected swap-chain configuration changed.");
        // Keep the runtime buffer check enabled: creation-time validation alone is
        // not sufficient if a driver changes or replaces a protected allocation.
        for (uint i = 0; i < desc.BufferCount; i++)
        {
            using var buffer = _swapChain.GetBuffer<ID3D11Texture2D>(i);
            ValidateProtectedBuffer(buffer.Description, i);
        }
    }

    private static void ValidateProtectedBuffer(Texture2DDescription desc, uint index)
    {
        if ((desc.MiscFlags & ResourceOptionFlags.HardwareProtected) == 0)
            throw new NotSupportedException($"DXGI back buffer {index} did not report hardware protection.");
        if (desc.CPUAccessFlags != CpuAccessFlags.None || desc.Usage != ResourceUsage.Default)
            throw new NotSupportedException($"DXGI back buffer {index} reported unexpected CPU access or resource usage.");
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
        _visual.SetContent(null); _target.SetRoot(null); _composition.Commit();
        if (_protectedContext is not null) _protectedContext.HardwareProtectionState = false;
        foreach (var rtv in _rtvs) rtv.Dispose();
        _visual.Dispose(); _target.Dispose(); _composition.Dispose(); _swapChain.Dispose(); _protectedContext?.Dispose();
    }
}
internal readonly record struct PresentOutcome(int ResultCode, bool Backlogged, Vortice.DXGI.FrameStatistics Statistics, bool HasStatistics)
{
    public bool Submitted => !Backlogged && ResultCode == 0;
}
