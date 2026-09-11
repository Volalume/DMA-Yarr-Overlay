using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Overlay;

internal sealed class OverlayHost : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private OverlayWindow? _window;
    private PerformanceHudWindow? _hud;
    private WindowCaptureProtection? _overlayProtection;
    private WindowCaptureProtection? _hudProtection;
    private ApplicationContext? _context;
    private bool _disposed;

    public OverlayHost()
    {
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "YarrOverlayWindow",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    public void SetMonitor(MonitorInfo monitor)
    {
        InvokeOnWindow(() => { _window!.SetMonitor(monitor); _hud!.SetMonitor(monitor); });
    }

    public void SetHudMode(PerformanceHudMode mode, bool running) => InvokeOnWindow(() => _hud!.SetMode(mode, running));

    public string OverlayCaptureStatus => _overlayProtection?.Status ?? "Pending";
    public string HudCaptureStatus => _hudProtection?.Status ?? "Pending";

    public void SetAntiCaptureMode(AntiCaptureMode mode) => InvokeOnWindow(() =>
    {
        _overlayProtection!.SetMode(mode);
        _hudProtection!.SetMode(mode);
    });

    public void SetAntiCaptureModes(AntiCaptureMode overlayMode, AntiCaptureMode hudMode) => InvokeOnWindow(() =>
    {
        _overlayProtection!.SetMode(overlayMode);
        _hudProtection!.SetMode(hudMode);
    });

    public WindowCapturePair InspectAntiCapture()
    {
        var overlay = WindowCaptureState.Unavailable;
        var hud = WindowCaptureState.Unavailable;
        InvokeOnWindow(() =>
        {
            overlay = _overlayProtection!.Inspect();
            hud = _hudProtection!.Inspect();
        });
        return new WindowCapturePair(overlay, hud);
    }

    public IntPtr OverlayHandle
    {
        get
        {
            var handle = IntPtr.Zero;
            InvokeOnWindow(() => handle = _window!.Handle);
            return handle;
        }
    }

    public void ShowOverlay()
    {
        InvokeOnWindow(() => _window!.ShowOverlay());
    }

    public void HideOverlay()
    {
        InvokeOnWindow(() => _window!.HideOverlay());
    }

    public void ResetOutputWindow(MonitorInfo monitor, AntiCaptureMode mode)
    {
        InvokeOnWindow(() =>
        {
            // Discard any previously displayed CPU DIB before enabling GPU protection.
            _window!.Dispose();
            _window = new OverlayWindow();
            _overlayProtection = new WindowCaptureProtection(_window);
            _overlayProtection.SetMode(mode);
            _ = _window.Handle;
            _window.SetMonitor(monitor);
        });
    }

    public void HideBlockedOutput(Func<bool> stillBlocked)
    {
        var window = _window;
        if (window is null || window.IsDisposed) return;
        try
        {
            // Never synchronously invoke the UI from a worker which Stop() joins.
            window.BeginInvoke(new Action(() =>
            {
                if (_disposed || !stillBlocked()) return;
                _window?.ClearFrame();
                _window?.HideOverlay();
                _hud?.SetMode(PerformanceHudMode.Off, false);
            }));
        }
        catch (InvalidOperationException) { }
    }

    public void ClearFrame()
    {
        _window?.ClearFrame();
    }

    public void SetFrame(FrameEnvelope frame)
    {
        _window?.SetFrame(frame);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_window is not null && !_window.IsDisposed)
        {
            try
            {
                _window.BeginInvoke(new Action(() =>
                {
                    _window.Dispose();
                    _hud?.Dispose();
                    _context?.ExitThread();
                }));
            }
            catch (InvalidOperationException)
            {
            }
        }

        if (_thread.IsAlive)
        {
            _thread.Join();
        }

        _ready.Dispose();
    }

    private void ThreadMain()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        _window = new OverlayWindow();
        _hud = new PerformanceHudWindow();
        _overlayProtection = new WindowCaptureProtection(_window);
        _hudProtection = new WindowCaptureProtection(_hud);
        _ = _window.Handle;
        _ = _hud.Handle;
        _context = new ApplicationContext();
        _ready.Set();
        Application.Run(_context);
    }

    private void InvokeOnWindow(Action action)
    {
        var window = _window;
        if (window is null || window.IsDisposed)
        {
            return;
        }

        if (window.InvokeRequired)
        {
            window.Invoke(action);
            return;
        }

        action();
    }
}
