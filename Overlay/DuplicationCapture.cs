using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.D3DCompiler;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;

namespace Overlay;

internal sealed class DuplicationCapture : IDisposable
{
    private readonly object _sync = new(); private Thread? _thread; private CancellationTokenSource? _cts;
    private MonitorInfo? _input; private MonitorInfo? _output; private int _threshold; private float _sharpness; private ScalingMode _scaling; private IntPtr _overlayHwnd; private CaptureOptions _options; private bool _disposed;
    private readonly LatencyMetrics _metrics = LatencyMetrics.Global;
    public event Action<FrameEnvelope>? FrameReady;
    public event Action<CaptureDiagnostics>? DiagnosticsChanged;
    public event Action<string>? RecreateRequested;
    public event Action? ProtectedOutputBlocked;
    private GpuProtectionStatus _gpuProtectionStatus = GpuProtectionStatus.Off;
    public GpuProtectionStatus ProtectionStatus => Volatile.Read(ref _gpuProtectionStatus);
    public void ResetProtectionStatus() { lock (_sync) { if (_thread?.IsAlive == true) throw new InvalidOperationException("Stop output before clearing its protection status."); Volatile.Write(ref _gpuProtectionStatus, GpuProtectionStatus.Off); } }
    public void Start(MonitorInfo input, MonitorInfo output, int threshold, float sharpness, ScalingMode scaling, IntPtr overlayHwnd, CaptureOptions options)
    {
        lock (_sync) { StopInternal(); _input = input; _output = output; _threshold = threshold; _sharpness = sharpness; _scaling = scaling; _overlayHwnd = overlayHwnd; _options = options; _metrics.Reset(); Volatile.Write(ref _gpuProtectionStatus, options.GpuProtection == GpuProtectionMode.Off ? GpuProtectionStatus.Off : GpuProtectionStatus.Pending); _cts = new CancellationTokenSource(); _thread = new Thread(() => CaptureLoop(_cts.Token)) { IsBackground = true, Name = "YarrOverlayCapture", Priority = options.ThreadPriority switch { CapturePriority.Highest => ThreadPriority.Highest, CapturePriority.AboveNormal => ThreadPriority.AboveNormal, _ => ThreadPriority.Normal } }; _thread.SetApartmentState(ApartmentState.MTA); _thread.Start(); }
    }
    public void SetThreshold(int threshold) => Volatile.Write(ref _threshold, threshold);
    public void SetSharpness(float sharpness) => Volatile.Write(ref _sharpness, Math.Clamp(sharpness, 0, 1));
    public void Stop() { lock (_sync) StopInternal(); }
    public void Dispose() { if (_disposed) return; _disposed = true; Stop(); }
    private void StopInternal() { if (_cts is null) return; _cts.Cancel(); if (_thread != Thread.CurrentThread) _thread?.Join(); _thread = null; _cts.Dispose(); _cts = null; DiagnosticsChanged?.Invoke(CaptureDiagnostics.Empty); }

    private void CaptureLoop(CancellationToken token)
    {
        // An unknown nonzero mode must fail closed too, not bypass this policy.
        var requireProtection = _options.GpuProtection != GpuProtectionMode.Off;
        var protectionPresented = false;
        var failures = 0; long dropped = 0; var recreateCount = 0; var frames = 0; long frameSequence = 0, acquireCalls = 0, submittedFrames = 0, lastLossTick = 0; var pipelineStage = "starting"; var fpsClock = Stopwatch.StartNew(); DateTimeOffset? lastFrame = null; var lastError = ""; long recoveryStarted = 0; double lastRecoveryMs = 0, totalRecoveryMs = 0;
        IntPtr mmcss = IntPtr.Zero; if (_options.EnableMmcss) { mmcss = NativeMethods.AvSetMmThreadCharacteristics("Games", out _); if (mmcss == IntPtr.Zero) Logger.Error($"MMCSS registration failed: {Marshal.GetLastWin32Error()}"); }
        using var csv = _options.CsvEnabled ? new PerformanceCsvWriter() : null;
        // The HUD's total latency value is sourced from LatencyMetrics. Keep the
        // lightweight submission samples enabled whenever Hardware Protection is
        // active so the protected WGC path still reports an ms value even if the
        // user selected Metrics: Off. Detailed GPU timing remains opt-in.
        var collectMetrics = _options.MetricsMode != MetricsMode.Off || _options.GpuProtection == GpuProtectionMode.HardwareRequired;
        var spin = new SpinWait();
        try
        {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_input is null || _output is null) return;
                var sameAdapter = string.Equals(_input.AdapterLuid, _output.AdapterLuid, StringComparison.Ordinal);
                if (!Enum.IsDefined(_options.GpuProtection)) throw new InvalidOperationException("Unknown GPU protection mode.");
                if (requireProtection && (_options.PipelineMode == PipelineMode.LegacyCpu || !sameAdapter))
                    throw new NotSupportedException("Protected output requires Auto/GPU Native and both displays on the same GPU. CPU fallback is disabled.");
                using var context = CreateContext(_input, _output, requireProtection);
                Logger.Info($"Capture: {_input.Identity} -> {_output.Identity}; {_options.GpuProtection}; {context.CaptureBackend}.");
                // Keep profiling queries out of the experimental protected command path.
                using var gpuTimestamps = _options.MetricsMode == MetricsMode.DetailedGpu && !requireProtection ? new GpuTimestampCollector(context.Device) : null;
                GpuOverlayRenderer? gpu = null;
                if (_options.PipelineMode != PipelineMode.LegacyCpu && sameAdapter)
                {
                    try { gpu = GpuOverlayRenderer.Create(context.Device, context.DeviceContext, context.Factory, _overlayHwnd, context.OutputWidth, context.OutputHeight, _options.MaximumFrameLatency, _options.GpuProtection); Logger.Info("Pipeline mode: GPU Native (DirectComposition, single GPU, one source copy)"); }
                    catch (Exception ex) when (_options.PipelineMode == PipelineMode.Auto && !requireProtection) { Logger.Error($"GPU Native initialization failed; using Legacy CPU fallback: {ex}"); }
                }
                else if (!sameAdapter) Logger.Info("Pipeline mode: Legacy CPU fallback (cross-adapter GPU sharing is not enabled)");
                if ((_options.PipelineMode == PipelineMode.GpuNative || requireProtection) && gpu is null) throw new InvalidOperationException("GPU Native was requested but cannot initialize on the selected adapter route.");
                if (gpu is null) context.EnsureLegacyResources();
                var pipelineName = gpu is null ? "Legacy CPU" : "GPU Native";
                if (recoveryStarted != 0) { lastRecoveryMs = FrameTiming.Ms(recoveryStarted, FrameTiming.Now); totalRecoveryMs += lastRecoveryMs; recoveryStarted = 0; }
                using (gpu)
                {
                Logger.Info($"Capture source ready: backend={context.CaptureBackend}; device={_input.DeviceName}; adapter={_input.AdapterName}.");
                while (!token.IsCancellationRequested)
                {
                    pipelineStage = "ValidateProtection";
                    gpu?.ValidateProtectionIfDue();
                    IDXGIResource? resource = null;
                    ID3D11Texture2D? sourceTexture = null;
                    try
                    {
                        var timing = new FrameTiming { AcquireCallStart = FrameTiming.Now };
                        acquireCalls++;
                        var accumulatedFrames = 1;
                        long desktopPresentTime = 0, desktopMouseUpdateTime = 0;
                        uint metadataSize = 0;
                        bool pointerVisible = false, protectedMaskedOut = false;
                        if (context.Wgc is not null)
                        {
                            pipelineStage = "WgcTryGetNextFrame";
                            sourceTexture = context.Wgc.TryGetNextTexture();
                            timing.AcquireReturned = FrameTiming.Now;
                            if (sourceTexture is null)
                            {
                                if(collectMetrics)_metrics.RecordTimeout();
                                spin.SpinOnce();
                                continue;
                            }
                            desktopPresentTime = timing.AcquireReturned;
                            timing.DesktopPresentTime = desktopPresentTime;
                        }
                        else
                        {
                            pipelineStage = "AcquireNextFrame";
                            var result = context.Duplication!.AcquireNextFrame(0, out var frameInfo, out resource);
                            timing.AcquireReturned = FrameTiming.Now; timing.DesktopPresentTime = frameInfo.LastPresentTime; timing.DesktopMouseUpdateTime = frameInfo.LastMouseUpdateTime; timing.AccumulatedFrames = (int)frameInfo.AccumulatedFrames; timing.TotalMetadataBufferSize = frameInfo.TotalMetadataBufferSize; timing.PointerVisible = frameInfo.PointerPosition.Visible; timing.ProtectedContentMaskedOut = frameInfo.ProtectedContentMaskedOut;
                            accumulatedFrames = (int)frameInfo.AccumulatedFrames; desktopPresentTime = frameInfo.LastPresentTime; desktopMouseUpdateTime = frameInfo.LastMouseUpdateTime; metadataSize = frameInfo.TotalMetadataBufferSize; pointerVisible = frameInfo.PointerPosition.Visible; protectedMaskedOut = frameInfo.ProtectedContentMaskedOut;
                            if (result.Failure)
                            {
                                if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code) { if(collectMetrics)_metrics.RecordTimeout(); spin.SpinOnce(); continue; }
                                if(collectMetrics)_metrics.RecordDxgiError();
                                failures++; var error = $"AcquireNextFrame failed: 0x{result.Code:X8}"; lastError = error;
                                var sinceLoss = lastLossTick == 0 ? -1 : FrameTiming.Ms(lastLossTick, FrameTiming.Now);
                                Logger.Error($"{error}; stage={pipelineStage}; acquireCalls={acquireCalls}; frames={frameSequence}; submitted={submittedFrames}; accumulated={frameInfo.AccumulatedFrames}; lastPresent={frameInfo.LastPresentTime}; lastMouse={frameInfo.LastMouseUpdateTime}; metadata={frameInfo.TotalMetadataBufferSize}; sincePreviousLossMs={sinceLoss:F2}; {gpu?.DescribeRuntimeState() ?? "gpu=none"}");
                                lastLossTick = FrameTiming.Now;
                                if (requireProtection && IsDuplicationOnlyRecovery(result.Code))
                                {
                                    recreateCount++;
                                    if (recoveryStarted == 0) recoveryStarted = FrameTiming.Now;
                                    protectionPresented = false;
                                    Volatile.Write(ref _gpuProtectionStatus, new(false, false, "Capture reconnecting", $"{error}; the protected output surface remains attached."));
                                    Logger.Info($"Protected input duplication lost; preserving the output surface and recreating input only: {error}; recovery={recreateCount}");
                                    Thread.Sleep(250);
                                    pipelineStage = "RecreateInputDuplication";
                                    context.RecreateDuplication(() => CreateDuplication(context.Factory, context.Device, _input!));
                                    Logger.Info($"Input duplication recreated successfully; output state: {gpu?.DescribeRuntimeState() ?? "gpu=none"}");
                                    continue;
                                }
                                if (IsRecoverable(result.Code))
                                {
                                    recreateCount++;
                                    if (recoveryStarted == 0) recoveryStarted = FrameTiming.Now;
                                    if (!requireProtection) RecreateRequested?.Invoke(error);
                                    break;
                                }
                                if (requireProtection) throw new InvalidOperationException(error);
                                Thread.Yield(); continue;
                            }
                            if (desktopPresentTime == 0) continue;
                            sourceTexture = resource!.QueryInterface<ID3D11Texture2D>();
                        }
                        timing.DesktopMouseUpdateTime = desktopMouseUpdateTime; timing.AccumulatedFrames = accumulatedFrames; timing.TotalMetadataBufferSize = metadataSize; timing.PointerVisible = pointerVisible; timing.ProtectedContentMaskedOut = protectedMaskedOut;
                        spin.Reset(); if(collectMetrics)_metrics.RecordCaptured(accumulatedFrames);
                        frameSequence++;
                        pipelineStage = "CopyResource";
                        if (gpuTimestamps is not null)
                        {
                            gpuTimestamps.TryCollect(context.DeviceContext, out timing.GpuCopyMs, out timing.GpuShaderMs, out timing.GpuTotalMs);
                            gpuTimestamps.Begin(context.DeviceContext);
                        }
                        context.DeviceContext.CopyResource(context.SourceTexture, sourceTexture); timing.SourceCopySubmitted = FrameTiming.Now;
                        gpuTimestamps?.CopyFinished(context.DeviceContext);
                        UpdateShaderParamsIfChanged(context);
                        var renderTarget = gpu?.CurrentRenderTarget ?? context.LegacyRtv!;
                        try
                        {
                            pipelineStage = "Draw";
                            gpu?.BeginDraw();
                            context.DeviceContext.OMSetRenderTargets(renderTarget); context.DeviceContext.RSSetViewport(0, 0, context.OutputWidth, context.OutputHeight);
                            context.DeviceContext.IASetPrimitiveTopology(PrimitiveTopology.TriangleList); context.DeviceContext.VSSetShader(context.Vs); context.DeviceContext.PSSetShader(context.Ps);
                            context.DeviceContext.PSSetShaderResource(0, context.Srv); context.DeviceContext.PSSetSampler(0, context.Sampler); context.DeviceContext.PSSetConstantBuffer(0, context.Params);
                            context.DeviceContext.ClearRenderTargetView(renderTarget, new Color4(0, 0, 0, 0)); context.DeviceContext.Draw(3, 0); context.DeviceContext.PSSetShaderResource(0, null!); timing.ShaderSubmitted = FrameTiming.Now;
                        }
                        finally { gpu?.EndDraw(); }
                        gpuTimestamps?.ShaderFinished(context.DeviceContext);

                        if (gpu is not null)
                        {
                            pipelineStage = "Present";
                            timing.SubmitStarted = FrameTiming.Now; var present = gpu.Present(); timing.SubmitReturned = FrameTiming.Now;
                            if (present.Backlogged) { timing.FrameDroppedOrReplaced = FrameTiming.Now; dropped++; if(collectMetrics)_metrics.RecordDropped(); continue; }
                            if (!present.Submitted)
                            {
                                // DXGI success-status codes are not necessarily a submitted
                                // frame. Do not leave the UI claiming that output is presenting.
                                if (requireProtection && (protectionPresented || ProtectionStatus == GpuProtectionStatus.Pending))
                                {
                                    Volatile.Write(ref _gpuProtectionStatus, new(false, false, "Waiting for display", $"Present status 0x{present.ResultCode:X8}; protected buffers retained."));
                                    Logger.Info($"Protected output waiting: Present status 0x{present.ResultCode:X8}");
                                }
                                protectionPresented = false;
                                continue;
                            }
                            if (requireProtection && !protectionPresented)
                            {
                                protectionPresented = true;
                                Volatile.Write(ref _gpuProtectionStatus, new(false, true, "Driver accepted / presenting", gpu.ProtectionDetails));
                                Logger.Info($"GPU protection: {gpu.ProtectionDetails}");
                            }
                            submittedFrames++;
                            if (frameSequence % 120 == 0)
                                Logger.Debug($"Protected frame: {frameSequence}; submitted={submittedFrames}; {gpu.DescribeRuntimeState()}");
                            if (present.HasStatistics) { timing.PresentCount=present.Statistics.PresentCount; timing.PresentRefreshCount=present.Statistics.PresentRefreshCount; timing.SyncRefreshCount=present.Statistics.SyncRefreshCount; timing.PresentSyncQpcTime=present.Statistics.SyncQPCTime; }
                            if(collectMetrics) LatencyMetrics.Global.RecordSubmitted(timing); lastFrame = DateTimeOffset.Now; frames++;
                            if (csv is not null && timing.FrameId % _options.CsvIntervalFrames == 0) csv.TryWrite(timing, pipelineName, _input.AdapterName, _output.AdapterName, 0, present.ResultCode);
                            _metrics.RenderThreadId = Environment.CurrentManagedThreadId;
                            PublishDiagnosticsIfDue(ref frames, fpsClock, dropped, failures, lastFrame, recreateCount, pipelineName, csv?.LostRows ?? 0, lastError, lastRecoveryMs, totalRecoveryMs);
                            continue;
                        }

                        // UpdateLayeredWindow requires a CPU-backed DIB. This is the one unavoidable readback in the existing WinForms overlay architecture.
                        context.DeviceContext.CopyResource(context.LegacyStaging!, context.LegacyOutputTexture!); timing.StagingCopySubmitted = FrameTiming.Now;
                        timing.MapStarted = FrameTiming.Now; var mapResult = context.DeviceContext.Map(context.LegacyStaging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out MappedSubresource mapped); timing.MapReturned = FrameTiming.Now;
                        if (mapResult.Failure) { dropped++; if(collectMetrics)_metrics.RecordDropped(); continue; }
                        try { var frame = new Bitmap(context.OutputWidth, context.OutputHeight, PixelFormat.Format32bppArgb); timing.CpuCopyStarted = FrameTiming.Now; CopyToBitmap(frame, mapped.DataPointer, (int)mapped.RowPitch); timing.CpuCopyFinished = FrameTiming.Now; timing.FrameEventRaised = FrameTiming.Now; FrameReady?.Invoke(new FrameEnvelope(frame, timing, csv, pipelineName, _input.AdapterName, _output.AdapterName, _options.CsvIntervalFrames, collectMetrics)); }
                        finally { context.DeviceContext.Unmap(context.LegacyStaging!, 0); }
                        lastFrame = DateTimeOffset.Now; frames++;
                        PublishDiagnosticsIfDue(ref frames, fpsClock, dropped, failures, lastFrame, recreateCount, pipelineName, csv?.LostRows ?? 0, lastError, lastRecoveryMs, totalRecoveryMs);
                    }
                    finally { sourceTexture?.Dispose(); if (resource is not null) { context.Duplication!.ReleaseFrame(); resource.Dispose(); } }
                }
                }
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested) break;
                Logger.Error($"Capture loop exception: stage={pipelineStage}; acquireCalls={acquireCalls}; frames={frameSequence}; submitted={submittedFrames}; {ex.GetType().Name}: {ex.Message} (0x{ex.HResult:X8})");
                if (requireProtection && IsRecoverable(ex.HResult))
                {
                    recreateCount++; failures++; lastError = $"{ex.GetType().Name}: {ex.Message}";
                    if (recoveryStarted == 0) recoveryStarted = FrameTiming.Now;
                    protectionPresented = false;
                    Volatile.Write(ref _gpuProtectionStatus, new(false, false, "Reconnecting protected output", $"0x{ex.HResult:X8}; protected resources will be recreated without an unprotected fallback."));
                    Logger.Error($"Protected DXGI resource was lost; recreating in place: {lastError}");
                    Thread.Sleep(100);
                    continue;
                }
                if (requireProtection)
                {
                    var detail = $"{ex.Message} (0x{ex.HResult:X8}). No unprotected fallback. Press Retry or select a different Anti-Capture level.";
                    Volatile.Write(ref _gpuProtectionStatus, new(true, false, "Blocked / output stopped", detail));
                    Logger.Error($"GPU protection failed closed: {ex}");
                    ProtectedOutputBlocked?.Invoke();
                    break;
                }
                recreateCount++; failures++; lastError=$"{ex.GetType().Name}: {ex.Message}"; if(recoveryStarted==0) recoveryStarted=FrameTiming.Now; Logger.Error($"Duplication recreation required: {lastError}"); RecreateRequested?.Invoke(ex.Message); if (!token.IsCancellationRequested) Thread.Sleep(250);
            }
        }
        }
        finally { if (mmcss != IntPtr.Zero) NativeMethods.AvRevertMmThreadCharacteristics(mmcss); }
    }

    private void PublishDiagnosticsIfDue(ref int frames, Stopwatch clock, long dropped, int failures, DateTimeOffset? lastFrame, int recreates, string pipeline, long csvLost, string lastError, double lastRecoveryMs, double totalRecoveryMs)
    {
        if (clock.ElapsedMilliseconds < 1000) return;
        using var process=Process.GetCurrentProcess();
        DiagnosticsChanged?.Invoke(new CaptureDiagnostics(frames, dropped, failures, lastFrame, lastFrame is null || DateTimeOffset.Now - lastFrame > TimeSpan.FromSeconds(2), recreates, lastError, _metrics.Snapshot(), pipeline, Environment.CurrentManagedThreadId, _metrics.RenderThreadId, _input?.AdapterName ?? "", _output?.AdapterName ?? "", _input?.AdapterLuid == _output?.AdapterLuid, csvLost, GC.CollectionCount(0),GC.CollectionCount(1),GC.CollectionCount(2),GC.GetTotalAllocatedBytes(false),process.WorkingSet64, lastRecoveryMs, totalRecoveryMs));
        frames = 0; clock.Restart();
    }
    private static bool IsRecoverable(int code) => code == Vortice.DXGI.ResultCode.AccessLost.Code || code == Vortice.DXGI.ResultCode.InvalidCall.Code || code == Vortice.DXGI.ResultCode.DeviceRemoved.Code || code == Vortice.DXGI.ResultCode.DeviceReset.Code;
    private static bool IsDuplicationOnlyRecovery(int code) => code == Vortice.DXGI.ResultCode.AccessLost.Code || code == Vortice.DXGI.ResultCode.InvalidCall.Code;
    private ShaderParams BuildParams(CaptureContext c)
    {
        var inputAspect = c.InputWidth / (float)c.InputHeight; var outputAspect = c.OutputWidth / (float)c.OutputHeight; var scaleX = 1f; var scaleY = 1f;
        if (_scaling == ScalingMode.Fit) { if (inputAspect > outputAspect) scaleY = outputAspect / inputAspect; else scaleX = inputAspect / outputAspect; }
        if (_scaling == ScalingMode.Fill) { if (inputAspect > outputAspect) scaleX = inputAspect / outputAspect; else scaleY = outputAspect / inputAspect; }
        return new ShaderParams { Threshold = Volatile.Read(ref _threshold) / 255f, Feather = 24 / 255f, InvW = 1f / c.InputWidth, InvH = 1f / c.InputHeight, Sharpness = Volatile.Read(ref _sharpness), ScaleX = scaleX, ScaleY = scaleY, Rotation = RotationValue(_input!.Rotation) };
    }
    private void UpdateShaderParamsIfChanged(CaptureContext c)
    {
        var value=BuildParams(c); if(c.HasParams && c.LastParams.Equals(value)) return;
        c.DeviceContext.UpdateSubresource(value,c.Params); c.LastParams=value; c.HasParams=true;
    }
    private static float RotationValue(string rotation) => rotation.Contains("Rotate90", StringComparison.OrdinalIgnoreCase) ? 1 : rotation.Contains("Rotate180", StringComparison.OrdinalIgnoreCase) ? 2 : rotation.Contains("Rotate270", StringComparison.OrdinalIgnoreCase) ? 3 : 0;
    private CaptureContext CreateContext(MonitorInfo input, MonitorInfo output, bool useWgc)
    {
        var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        try
        {
            for (uint ai = 0; ; ai++)
            {
                var ar = factory.EnumAdapters1(ai, out var adapter);
                if (ar.Failure || adapter is null) break;
                using (adapter)
                {
                    if (adapter.Description1.Luid.ToString() != input.AdapterLuid) continue;
                    var outputResult = adapter.EnumOutputs(input.OutputIndex, out var dxgiOutput);
                    if (outputResult.Failure || dxgiOutput is null) continue;
                    using (dxgiOutput)
                    {
                        if (!string.Equals(dxgiOutput.Description.DeviceName.TrimEnd('\0'), input.DeviceName, StringComparison.OrdinalIgnoreCase)) continue;
                        ID3D11Device? device = null;
                        ID3D11DeviceContext? dc = null;
                        IDXGIOutputDuplication? duplication = null;
                        GraphicsCaptureSource? wgc = null;
                        try
                        {
                            var cr = D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
                                out device, out _, out dc);
                            cr.CheckError();
                            if (useWgc)
                                wgc = GraphicsCaptureSource.Create(device, input);
                            else
                            {
                                using var output1 = dxgiOutput.QueryInterface<IDXGIOutput1>();
                                duplication = output1.DuplicateOutput(device);
                            }
                            // Ownership transfers only after all initialization succeeds.
                            return new CaptureContext(factory, device, dc, duplication, wgc, input.Bounds.Width, input.Bounds.Height, output.Bounds.Width, output.Bounds.Height);
                        }
                        catch
                        {
                            wgc?.Dispose(); duplication?.Dispose(); dc?.Dispose(); device?.Dispose();
                            throw;
                        }
                    }
                }
            }
            throw new InvalidOperationException($"DXGI output was not found: {input.Identity}");
        }
        catch { factory.Dispose(); throw; }
    }
    private static IDXGIOutputDuplication CreateDuplication(IDXGIFactory1 factory, ID3D11Device device, MonitorInfo input)
    {
        for (uint ai = 0; ; ai++)
        {
            var adapterResult = factory.EnumAdapters1(ai, out var adapter);
            if (adapterResult.Failure || adapter is null) break;
            using (adapter)
            {
                if (adapter.Description1.Luid.ToString() != input.AdapterLuid) continue;
                var outputResult = adapter.EnumOutputs(input.OutputIndex, out var output);
                if (outputResult.Failure || output is null) continue;
                using (output)
                {
                    if (!string.Equals(output.Description.DeviceName.TrimEnd('\0'), input.DeviceName, StringComparison.OrdinalIgnoreCase)) continue;
                    using var output1 = output.QueryInterface<IDXGIOutput1>();
                    return output1.DuplicateOutput(device);
                }
            }
        }
        throw new InvalidOperationException($"DXGI output was not found while reconnecting: {input.Identity}");
    }
    private static unsafe void CopyToBitmap(Bitmap bitmap, nint ptr, int pitch) { var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb); try { for (var y = 0; y < bitmap.Height; y++) Buffer.MemoryCopy((byte*)ptr + y * pitch, (byte*)data.Scan0 + y * data.Stride, data.Stride, bitmap.Width * 4L); } finally { bitmap.UnlockBits(data); } }
    [StructLayout(LayoutKind.Sequential)] private struct ShaderParams { public float Threshold, Feather, InvW, InvH, Sharpness, ScaleX, ScaleY, Rotation; }
    private const string ShaderSource = """
cbuffer P : register(b0) { float threshold, feather, invW, invH, sharpness, scaleX, scaleY, rotation; }; Texture2D t : register(t0); SamplerState s : register(s0);
struct O { float4 p:SV_POSITION; float2 uv:TEXCOORD0; }; O VSMain(uint id:SV_VertexID) { float2 ps[3]={float2(-1,-1),float2(-1,3),float2(3,-1)}; float2 u[3]={float2(0,1),float2(0,-1),float2(2,1)}; O o; o.p=float4(ps[id],0,1); o.uv=u[id]; return o; }
float2 RotateUv(float2 u) { if(rotation==1) return float2(1-u.y,u.x); if(rotation==2) return 1-u; if(rotation==3) return float2(u.y,1-u.x); return u; }
float4 PSMain(O i):SV_TARGET { float2 uv=(i.uv-float2(.5,.5))/float2(scaleX,scaleY)+float2(.5,.5); if(any(uv<0)||any(uv>1)) return 0; uv=RotateUv(uv); float2 q=float2(invW,invH); float4 c=t.Sample(s,uv); float3 a=(t.Sample(s,uv+float2(q.x,0)).rgb+t.Sample(s,uv-float2(q.x,0)).rgb+t.Sample(s,uv+float2(0,q.y)).rgb+t.Sample(s,uv-float2(0,q.y)).rgb)*.25; c.rgb=saturate(c.rgb+(c.rgb-a)*sharpness); c.a*=saturate((max(c.r,max(c.g,c.b))-threshold)/max(feather,.00001)); c.rgb*=c.a; return c; }
""";
    private sealed class CaptureContext : IDisposable
    {
        public readonly IDXGIFactory1 Factory; public readonly ID3D11Device Device; public readonly ID3D11DeviceContext DeviceContext; public IDXGIOutputDuplication? Duplication { get; private set; } public readonly GraphicsCaptureSource? Wgc; public string CaptureBackend => Wgc is null ? "Desktop Duplication" : "Windows Graphics Capture"; public readonly ID3D11Texture2D SourceTexture; public ID3D11Texture2D? LegacyOutputTexture, LegacyStaging; public readonly ID3D11ShaderResourceView Srv; public ID3D11RenderTargetView? LegacyRtv; public readonly ID3D11SamplerState Sampler; public readonly ID3D11VertexShader Vs; public readonly ID3D11PixelShader Ps; public readonly ID3D11Buffer Params; public readonly int InputWidth, InputHeight, OutputWidth, OutputHeight;
        public ShaderParams LastParams; public bool HasParams;
        public CaptureContext(IDXGIFactory1 f, ID3D11Device d, ID3D11DeviceContext c, IDXGIOutputDuplication? dup, GraphicsCaptureSource? wgc, int iw, int ih, int ow, int oh)
        {
            Factory=f; Device=d; DeviceContext=c; Duplication=dup; Wgc=wgc; InputWidth=iw; InputHeight=ih; OutputWidth=ow; OutputHeight=oh;
            try
            {
                SourceTexture=d.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,(uint)iw,(uint)ih,1,1,BindFlags.ShaderResource,ResourceUsage.Default,CpuAccessFlags.None,1,0,ResourceOptionFlags.None));
                Srv=d.CreateShaderResourceView(SourceTexture); Sampler=d.CreateSamplerState(SamplerDescription.LinearClamp);
                var vs=Compiler.Compile(ShaderSource,"VSMain","YarrOverlayGpu.hlsl","vs_4_0",ShaderFlags.OptimizationLevel3);
                var ps=Compiler.Compile(ShaderSource,"PSMain","YarrOverlayGpu.hlsl","ps_4_0",ShaderFlags.OptimizationLevel3);
                Vs=d.CreateVertexShader(vs.Span); Ps=d.CreatePixelShader(ps.Span);
                Params=d.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<ShaderParams>(),BindFlags.ConstantBuffer));
            }
            catch
            {
                // The caller still owns the device, duplication, and factory on failure.
                Params?.Dispose(); Ps?.Dispose(); Vs?.Dispose(); Sampler?.Dispose(); Srv?.Dispose(); SourceTexture?.Dispose();
                throw;
            }
        }
        public void RecreateDuplication(Func<IDXGIOutputDuplication> create)
        {
            if (Duplication is null) throw new InvalidOperationException("Desktop Duplication is not active for this capture source.");
            Duplication.Dispose();
            Duplication = create();
        }
        public void EnsureLegacyResources(){if(LegacyOutputTexture is not null)return;LegacyOutputTexture=Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,(uint)OutputWidth,(uint)OutputHeight,1,1,BindFlags.RenderTarget,ResourceUsage.Default,CpuAccessFlags.None,1,0,ResourceOptionFlags.None));LegacyRtv=Device.CreateRenderTargetView(LegacyOutputTexture);LegacyStaging=Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,(uint)OutputWidth,(uint)OutputHeight,1,1,BindFlags.None,ResourceUsage.Staging,CpuAccessFlags.Read,1,0,ResourceOptionFlags.None));}
        public void Dispose(){ Params.Dispose(); Ps.Dispose(); Vs.Dispose(); Sampler.Dispose(); LegacyStaging?.Dispose(); LegacyRtv?.Dispose(); LegacyOutputTexture?.Dispose(); Srv.Dispose(); SourceTexture.Dispose(); Wgc?.Dispose(); Duplication?.Dispose(); DeviceContext.Dispose(); Device.Dispose(); Factory.Dispose(); }
    }
}
