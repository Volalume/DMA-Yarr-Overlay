using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
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
    private readonly object _sync = new();
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private MonitorInfo? _inputMonitor;
    private MonitorInfo? _outputMonitor;
    private int _threshold;
    private float _sharpness;
    private bool _disposed;

    public event Action<Bitmap>? FrameReady;
    public event Action<int>? FpsChanged;

    public void Start(MonitorInfo inputMonitor, MonitorInfo outputMonitor, int threshold, float sharpness)
    {
        lock (_sync)
        {
            StopInternal();

            _inputMonitor = inputMonitor;
            _outputMonitor = outputMonitor;
            _threshold = threshold;
            _sharpness = sharpness;
            _cts = new CancellationTokenSource();
            _thread = new Thread(() => CaptureLoop(_cts.Token))
            {
                IsBackground = true,
                Name = "YarrOverlayCapture",
                Priority = ThreadPriority.Highest
            };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }
    }

    public void SetThreshold(int threshold)
    {
        _threshold = threshold;
    }

    public void SetSharpness(float sharpness)
    {
        _sharpness = Math.Clamp(sharpness, 0.0f, 1.0f);
    }

    public void Stop()
    {
        lock (_sync)
        {
            StopInternal();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    private void StopInternal()
    {
        if (_cts is null)
        {
            return;
        }

        _cts.Cancel();
        _thread?.Join();
        _thread = null;
        _cts.Dispose();
        _cts = null;
        FpsChanged?.Invoke(0);
    }

    private void CaptureLoop(CancellationToken token)
    {
        if (_inputMonitor is null || _outputMonitor is null)
        {
            return;
        }

        using var context = CreateCaptureContext(_inputMonitor, _outputMonitor);
        using var titleFont = new Font("Segoe UI Semibold", 26f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var titleShadowBrush = new SolidBrush(System.Drawing.Color.FromArgb(220, 0, 0, 0));
        using var titleBrush = new SolidBrush(System.Drawing.Color.FromArgb(255, 255, 230, 120));
        using var centerFormat = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Near
        };

        var frameCounter = 0;
        var fpsWatch = Stopwatch.StartNew();

        while (!token.IsCancellationRequested)
        {
            IDXGIResource? desktopResource = null;

            try
            {
                var result = context.Duplication.AcquireNextFrame(0, out var _, out desktopResource);
                if (result.Failure)
                {
                    if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code)
                    {
                        Thread.Yield();
                        continue;
                    }

                    if (result.Code == Vortice.DXGI.ResultCode.AccessLost.Code)
                    {
                        break;
                    }

                    Thread.Yield();
                    continue;
                }

                using var frameTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
                context.DeviceContext.CopyResource(context.SourceTexture, frameTexture);
                context.DeviceContext.UpdateSubresource(
                    new ShaderParams
                    {
                        Threshold = _threshold / 255.0f,
                        Feather = 24.0f / 255.0f,
                        InvInputWidth = 1.0f / context.InputWidth,
                        InvInputHeight = 1.0f / context.InputHeight,
                        Sharpness = _sharpness,
                        Padding = 0.0f
                    },
                    context.ShaderParamsBuffer);

                context.DeviceContext.OMSetRenderTargets(context.OutputRenderTargetView);
                context.DeviceContext.RSSetViewport(0, 0, context.OutputWidth, context.OutputHeight);
                context.DeviceContext.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                context.DeviceContext.VSSetShader(context.VertexShader);
                context.DeviceContext.PSSetShader(context.PixelShader);
                context.DeviceContext.PSSetShaderResource(0, context.SourceShaderResourceView);
                context.DeviceContext.PSSetSampler(0, context.LinearSampler);
                context.DeviceContext.PSSetConstantBuffer(0, context.ShaderParamsBuffer);
                context.DeviceContext.ClearRenderTargetView(context.OutputRenderTargetView, new Color4(0f, 0f, 0f, 0f));
                context.DeviceContext.Draw(3, 0);
                context.DeviceContext.PSSetShaderResource(0, null!);
                context.DeviceContext.CopyResource(context.OutputStagingTexture, context.OutputTexture);

                var map = context.DeviceContext.Map(
                    context.OutputStagingTexture,
                    0,
                    MapMode.Read,
                    Vortice.Direct3D11.MapFlags.None,
                    out MappedSubresource mapped);

                if (map.Failure)
                {
                    context.Duplication.ReleaseFrame();
                    continue;
                }

                try
                {
                    Bitmap? frame = null;

                    try
                    {
                        frame = new Bitmap(context.OutputWidth, context.OutputHeight, PixelFormat.Format32bppArgb);
                        CopyMappedTextureToBitmap(frame, mapped.DataPointer, (int)mapped.RowPitch);

                        using (var g = Graphics.FromImage(frame))
                        {
                            var titleBounds = new RectangleF(0, 14, frame.Width, 40);
                            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                            g.DrawString("YarrOverlay <<", titleFont, titleShadowBrush, new RectangleF(3, 17, frame.Width, 40), centerFormat);
                            g.DrawString("YarrOverlay <<", titleFont, titleBrush, titleBounds, centerFormat);
                        }

                        var frameReady = FrameReady;
                        if (frameReady is null)
                        {
                            frame.Dispose();
                            frame = null;
                        }
                        else
                        {
                            frameReady(frame);
                            frame = null;
                        }
                    }
                    finally
                    {
                        frame?.Dispose();
                    }
                }
                finally
                {
                    context.DeviceContext.Unmap(context.OutputStagingTexture, 0);
                    context.Duplication.ReleaseFrame();
                }

                frameCounter++;
                if (fpsWatch.ElapsedMilliseconds >= 1000)
                {
                    FpsChanged?.Invoke(frameCounter);
                    frameCounter = 0;
                    fpsWatch.Restart();
                }
            }
            catch
            {
                break;
            }
            finally
            {
                desktopResource?.Dispose();
            }
        }
    }

    private CaptureContext CreateCaptureContext(MonitorInfo inputMonitor, MonitorInfo outputMonitor)
    {
        var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        for (var adapterIndex = 0; ; adapterIndex++)
        {
            var adapterResult = factory.EnumAdapters1((uint)adapterIndex, out var adapter);
            if (adapterResult.Failure || adapter is null)
            {
                break;
            }

            using (adapter)
            {
                for (var outputIndex = 0; ; outputIndex++)
                {
                    var outputResult = adapter.EnumOutputs((uint)outputIndex, out var output);
                    if (outputResult.Failure || output is null)
                    {
                        break;
                    }

                    using (output)
                    {
                        var outputDesc = output.Description;
                        if (!string.Equals(outputDesc.DeviceName, inputMonitor.DeviceName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var featureLevels = new[]
                        {
                            FeatureLevel.Level_11_1,
                            FeatureLevel.Level_11_0,
                            FeatureLevel.Level_10_1,
                            FeatureLevel.Level_10_0
                        };

                        var createResult = D3D11CreateDevice(
                            adapter,
                            DriverType.Unknown,
                            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                            featureLevels,
                            out ID3D11Device device,
                            out _,
                            out ID3D11DeviceContext deviceContext);

                        if (createResult.Failure)
                        {
                            throw new InvalidOperationException($"D3D11CreateDevice failed: {createResult.Code}");
                        }

                        var output1 = output.QueryInterface<IDXGIOutput1>();
                        var duplication = output1.DuplicateOutput(device);

                        var sourceTextureDesc = new Texture2DDescription(
                            Format.B8G8R8A8_UNorm,
                            (uint)inputMonitor.Bounds.Width,
                            (uint)inputMonitor.Bounds.Height,
                            1,
                            1,
                            BindFlags.ShaderResource,
                            ResourceUsage.Default,
                            CpuAccessFlags.None,
                            1,
                            0,
                            ResourceOptionFlags.None);

                        var sourceTexture = device.CreateTexture2D(sourceTextureDesc);
                        var sourceShaderResourceView = device.CreateShaderResourceView(sourceTexture);

                        var outputTextureDesc = new Texture2DDescription(
                            Format.B8G8R8A8_UNorm,
                            (uint)outputMonitor.Bounds.Width,
                            (uint)outputMonitor.Bounds.Height,
                            1,
                            1,
                            BindFlags.RenderTarget,
                            ResourceUsage.Default,
                            CpuAccessFlags.None,
                            1,
                            0,
                            ResourceOptionFlags.None);

                        var outputTexture = device.CreateTexture2D(outputTextureDesc);
                        var outputRenderTargetView = device.CreateRenderTargetView(outputTexture);

                        var outputStagingTextureDesc = new Texture2DDescription(
                            Format.B8G8R8A8_UNorm,
                            (uint)outputMonitor.Bounds.Width,
                            (uint)outputMonitor.Bounds.Height,
                            1,
                            1,
                            BindFlags.None,
                            ResourceUsage.Staging,
                            CpuAccessFlags.Read,
                            1,
                            0,
                            ResourceOptionFlags.None);

                        var outputStagingTexture = device.CreateTexture2D(outputStagingTextureDesc);
                        var linearSampler = device.CreateSamplerState(SamplerDescription.LinearClamp);

                        var vertexShaderBytecode = Compiler.Compile(
                            ShaderSource,
                            "VSMain",
                            "YarrOverlayGpu.hlsl",
                            "vs_4_0",
                            ShaderFlags.OptimizationLevel3);

                        var pixelShaderBytecode = Compiler.Compile(
                            ShaderSource,
                            "PSMain",
                            "YarrOverlayGpu.hlsl",
                            "ps_4_0",
                            ShaderFlags.OptimizationLevel3);

                        var vertexShader = device.CreateVertexShader(vertexShaderBytecode.Span);
                        var pixelShader = device.CreatePixelShader(pixelShaderBytecode.Span);
                        var shaderParamsBuffer = device.CreateBuffer(
                            new BufferDescription((uint)Marshal.SizeOf<ShaderParams>(), BindFlags.ConstantBuffer));

                        return new CaptureContext(
                            factory,
                            device,
                            deviceContext,
                            duplication,
                            sourceTexture,
                            sourceShaderResourceView,
                            outputTexture,
                            outputRenderTargetView,
                            outputStagingTexture,
                            linearSampler,
                            vertexShader,
                            pixelShader,
                            shaderParamsBuffer,
                            inputMonitor.Bounds.Width,
                            inputMonitor.Bounds.Height,
                            outputMonitor.Bounds.Width,
                            outputMonitor.Bounds.Height);
                    }
                }
            }
        }

        factory.Dispose();
        throw new InvalidOperationException($"Could not create duplication for {inputMonitor.DeviceName}.");
    }

    private unsafe void CopyMappedTextureToBitmap(Bitmap bitmap, nint sourcePtr, int sourceRowPitch)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        try
        {
            for (var y = 0; y < bitmap.Height; y++)
            {
                var src = (byte*)sourcePtr + (y * sourceRowPitch);
                var dst = (byte*)data.Scan0 + (y * data.Stride);
                Buffer.MemoryCopy(src, dst, data.Stride, bitmap.Width * 4L);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private const string ShaderSource = """
cbuffer ShaderParams : register(b0)
{
    float threshold;
    float feather;
    float invInputWidth;
    float invInputHeight;
    float sharpness;
    float padding0;
    float padding1;
    float padding2;
};

Texture2D inputTexture : register(t0);
SamplerState linearSampler : register(s0);

struct VSOutput
{
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
};

VSOutput VSMain(uint vertexId : SV_VertexID)
{
    float2 positions[3] =
    {
        float2(-1.0, -1.0),
        float2(-1.0, 3.0),
        float2(3.0, -1.0)
    };

    float2 uvs[3] =
    {
        float2(0.0, 1.0),
        float2(0.0, -1.0),
        float2(2.0, 1.0)
    };

    VSOutput output;
    output.position = float4(positions[vertexId], 0.0, 1.0);
    output.uv = uvs[vertexId];
    return output;
}

float4 PSMain(VSOutput input) : SV_TARGET
{
    float2 texel = float2(invInputWidth, invInputHeight);
    float2 uv = saturate(input.uv);

    float4 center = inputTexture.Sample(linearSampler, uv);
    float4 north = inputTexture.Sample(linearSampler, saturate(uv + float2(0.0, -texel.y)));
    float4 south = inputTexture.Sample(linearSampler, saturate(uv + float2(0.0, texel.y)));
    float4 west = inputTexture.Sample(linearSampler, saturate(uv + float2(-texel.x, 0.0)));
    float4 east = inputTexture.Sample(linearSampler, saturate(uv + float2(texel.x, 0.0)));

    float3 neighborAverage = (north.rgb + south.rgb + west.rgb + east.rgb) * 0.25;
    float3 detail = center.rgb - neighborAverage;
    float detailStrength = saturate(length(detail) * 4.0);
    float sharpenAmount = sharpness * (1.0 - detailStrength * 0.35);

    float4 color = center;
    color.rgb = saturate(center.rgb + detail * sharpenAmount);

    float maxChannel = max(color.r, max(color.g, color.b));
    float alpha = saturate((maxChannel - threshold) / max(feather, 0.00001));
    color.a *= alpha;
    return color;
}
""";

    [StructLayout(LayoutKind.Sequential)]
    private struct ShaderParams
    {
        public float Threshold;
        public float Feather;
        public float InvInputWidth;
        public float InvInputHeight;
        public float Sharpness;
        public float Padding;
        public float Padding1;
        public float Padding2;
    }

    private sealed class CaptureContext : IDisposable
    {
        public CaptureContext(
            IDXGIFactory1 factory,
            ID3D11Device device,
            ID3D11DeviceContext deviceContext,
            IDXGIOutputDuplication duplication,
            ID3D11Texture2D sourceTexture,
            ID3D11ShaderResourceView sourceShaderResourceView,
            ID3D11Texture2D outputTexture,
            ID3D11RenderTargetView outputRenderTargetView,
            ID3D11Texture2D outputStagingTexture,
            ID3D11SamplerState linearSampler,
            ID3D11VertexShader vertexShader,
            ID3D11PixelShader pixelShader,
            ID3D11Buffer shaderParamsBuffer,
            int inputWidth,
            int inputHeight,
            int outputWidth,
            int outputHeight)
        {
            Factory = factory;
            Device = device;
            DeviceContext = deviceContext;
            Duplication = duplication;
            SourceTexture = sourceTexture;
            SourceShaderResourceView = sourceShaderResourceView;
            OutputTexture = outputTexture;
            OutputRenderTargetView = outputRenderTargetView;
            OutputStagingTexture = outputStagingTexture;
            LinearSampler = linearSampler;
            VertexShader = vertexShader;
            PixelShader = pixelShader;
            ShaderParamsBuffer = shaderParamsBuffer;
            InputWidth = inputWidth;
            InputHeight = inputHeight;
            OutputWidth = outputWidth;
            OutputHeight = outputHeight;
        }

        public IDXGIFactory1 Factory { get; }
        public ID3D11Device Device { get; }
        public ID3D11DeviceContext DeviceContext { get; }
        public IDXGIOutputDuplication Duplication { get; }
        public ID3D11Texture2D SourceTexture { get; }
        public ID3D11ShaderResourceView SourceShaderResourceView { get; }
        public ID3D11Texture2D OutputTexture { get; }
        public ID3D11RenderTargetView OutputRenderTargetView { get; }
        public ID3D11Texture2D OutputStagingTexture { get; }
        public ID3D11SamplerState LinearSampler { get; }
        public ID3D11VertexShader VertexShader { get; }
        public ID3D11PixelShader PixelShader { get; }
        public ID3D11Buffer ShaderParamsBuffer { get; }
        public int InputWidth { get; }
        public int InputHeight { get; }
        public int OutputWidth { get; }
        public int OutputHeight { get; }

        public void Dispose()
        {
            ShaderParamsBuffer.Dispose();
            PixelShader.Dispose();
            VertexShader.Dispose();
            LinearSampler.Dispose();
            OutputStagingTexture.Dispose();
            OutputRenderTargetView.Dispose();
            OutputTexture.Dispose();
            SourceShaderResourceView.Dispose();
            SourceTexture.Dispose();
            Duplication.Dispose();
            DeviceContext.Dispose();
            Device.Dispose();
            Factory.Dispose();
        }
    }
}
