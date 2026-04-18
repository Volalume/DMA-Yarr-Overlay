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
        InvokeOnWindow(() => _window!.SetMonitor(monitor));
    }

    public void ShowOverlay()
    {
        InvokeOnWindow(() => _window!.ShowOverlay());
    }

    public void HideOverlay()
    {
        InvokeOnWindow(() => _window!.HideOverlay());
    }

    public void ClearFrame()
    {
        _window?.ClearFrame();
    }

    public void SetFrame(Bitmap frame)
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
        _ = _window.Handle;
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
