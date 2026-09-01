using System;
using System.IO;
using System.Windows.Forms;
using Hexa.NET.ImGui;
using Hexa.NET.ImGui.Backends.Win32;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;
using BackendDevice = Hexa.NET.ImGui.Backends.D3D11.ID3D11Device;
using BackendDeviceContext = Hexa.NET.ImGui.Backends.D3D11.ID3D11DeviceContext;
using ImGuiD3D11Backend = Hexa.NET.ImGui.Backends.D3D11.ImGuiImplD3D11;

namespace Overlay;

internal sealed unsafe class ImGuiD3D11Controller : IDisposable
{
    private readonly IntPtr _windowHandle;
    private readonly ImGuiContextPtr _imGuiContext;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _deviceContext;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _renderTarget;
    private bool _disposed;

    public ImGuiD3D11Controller(IntPtr windowHandle, int width, int height, float dpiScale)
    {
        _windowHandle = windowHandle;
        CreateDeviceAndSwapChain(Math.Max(1, width), Math.Max(1, height));

        _imGuiContext = ImGui.CreateContext();
        ImGui.SetCurrentContext(_imGuiContext);
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;

        BodyFont = LoadFont(io.Fonts, "segoeui.ttf", 16f * dpiScale);
        MonoFont = LoadFont(io.Fonts, "consola.ttf", 15f * dpiScale);
        ImGuiTheme.Apply(dpiScale);

        ImGuiImplWin32.SetCurrentContext(_imGuiContext);
        ImGuiImplWin32.Init(windowHandle.ToPointer());
        ImGuiD3D11Backend.SetCurrentContext(_imGuiContext);
        ImGuiD3D11Backend.Init(
            (BackendDevice*)_device!.NativePointer.ToPointer(),
            (BackendDeviceContext*)_deviceContext!.NativePointer.ToPointer());
    }

    public ImFontPtr BodyFont { get; }

    public ImFontPtr MonoFont { get; }

    public bool ProcessWindowMessage(ref Message message)
    {
        if (_disposed)
        {
            return false;
        }

        ImGui.SetCurrentContext(_imGuiContext);
        var handled = ImGuiImplWin32.WndProcHandler(
            message.HWnd,
            (uint)message.Msg,
            (nuint)message.WParam,
            message.LParam);
        return handled != IntPtr.Zero;
    }

    public void Render(Action drawUi)
    {
        if (_disposed || _renderTarget is null || _deviceContext is null || _swapChain is null)
        {
            return;
        }

        ImGui.SetCurrentContext(_imGuiContext);
        ImGuiD3D11Backend.NewFrame();
        ImGuiImplWin32.NewFrame();
        ImGui.NewFrame();

        drawUi();

        ImGui.Render();
        _deviceContext.OMSetRenderTargets(_renderTarget);
        _deviceContext.ClearRenderTargetView(_renderTarget, new Color4(0.039f, 0.047f, 0.067f, 1f));
        ImGuiD3D11Backend.RenderDrawData(ImGui.GetDrawData());
        _swapChain.Present(1, PresentFlags.None);
    }

    public void Resize(int width, int height)
    {
        if (_disposed || width <= 0 || height <= 0 || _swapChain is null)
        {
            return;
        }

        _deviceContext?.ClearState();
        _renderTarget?.Dispose();
        _renderTarget = null;
        _swapChain.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None);
        CreateRenderTarget();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ImGui.SetCurrentContext(_imGuiContext);
        ImGuiD3D11Backend.Shutdown();
        ImGuiImplWin32.Shutdown();
        ImGui.DestroyContext(_imGuiContext);

        _renderTarget?.Dispose();
        _swapChain?.Dispose();
        _deviceContext?.Dispose();
        _device?.Dispose();
    }

    private void CreateDeviceAndSwapChain(int width, int height)
    {
        var featureLevels = new[]
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0
        };

        var result = D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            featureLevels,
            out ID3D11Device device,
            out _,
            out ID3D11DeviceContext deviceContext);
        if (result.Failure)
        {
            throw new InvalidOperationException($"Could not create the ImGui D3D11 device: {result.Code}");
        }

        _device = device;
        _deviceContext = deviceContext;

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
        var description = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format.R8G8B8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.None
        };

        _swapChain = factory.CreateSwapChainForHwnd(_device, _windowHandle, description);
        factory.MakeWindowAssociation(_windowHandle, WindowAssociationFlags.IgnoreAltEnter);
        CreateRenderTarget();
    }

    private void CreateRenderTarget()
    {
        using var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _renderTarget = _device!.CreateRenderTargetView(backBuffer);
    }

    private static ImFontPtr LoadFont(ImFontAtlasPtr atlas, string fileName, float size)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", fileName);
        return File.Exists(path) ? atlas.AddFontFromFileTTF(path, size) : atlas.AddFontDefault();
    }
}
