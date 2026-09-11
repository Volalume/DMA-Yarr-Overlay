using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace Overlay;

internal sealed class OverlayWindow : Form
{
    private readonly object _frameSync = new();
    private FrameEnvelope? _currentFrame;
    private MonitorInfo? _monitor;
    private int _renderQueued;
    private int _frameVersion;
    private int _renderedVersion;
    private IntPtr _legacyMemoryDc;

    public OverlayWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WsExLayered
                | NativeMethods.WsExTransparent
                | NativeMethods.WsExToolWindow
                | NativeMethods.WsExNoActivate
                | NativeMethods.WsExNoRedirectionBitmap;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    public void SetMonitor(MonitorInfo monitor)
    {
        _monitor = monitor;

        if (IsHandleCreated)
        {
            Bounds = monitor.Bounds;
            NativeMethods.SetWindowPos(
                Handle,
                new IntPtr(NativeMethods.HwndTopmost),
                monitor.Bounds.X,
                monitor.Bounds.Y,
                monitor.Bounds.Width,
                monitor.Bounds.Height,
                NativeMethods.SwpNoActivate);
        }
    }

    public void ShowOverlay()
    {
        if (_monitor is null)
        {
            return;
        }

        Bounds = _monitor.Bounds;
        Show();
        NativeMethods.SetWindowPos(
            Handle,
            new IntPtr(NativeMethods.HwndTopmost),
            _monitor.Bounds.X,
            _monitor.Bounds.Y,
            _monitor.Bounds.Width,
            _monitor.Bounds.Height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        QueueRender();
    }

    public void HideOverlay()
    {
        Hide();
    }

    public void ClearFrame()
    {
        lock (_frameSync)
        {
            _currentFrame?.Dispose();
            _currentFrame = null;
        }
    }

    public void SetFrame(FrameEnvelope frame)
    {
        if (IsDisposed)
        {
            frame.Dispose();
            return;
        }

        lock (_frameSync)
        {
            if (_currentFrame is not null)
            {
                _currentFrame.Timing.FrameDroppedOrReplaced = FrameTiming.Now;
                _currentFrame.Dispose();
                if (_currentFrame.MetricsEnabled) LatencyMetrics.Global.RecordReplaced();
            }
            frame.Timing.OverlayFrameReceived = FrameTiming.Now;
            _currentFrame = frame;
            if (frame.MetricsEnabled) LatencyMetrics.Global.SetQueueLength(1);
            _frameVersion++;
        }

        QueueRender();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        QueueRender();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClearFrame();
            if (_legacyMemoryDc != IntPtr.Zero) { NativeMethods.DeleteDC(_legacyMemoryDc); _legacyMemoryDc = IntPtr.Zero; }
        }

        base.Dispose(disposing);
    }

    private void RenderFrame()
    {
        LatencyMetrics.Global.RenderThreadId = Environment.CurrentManagedThreadId;
        FrameEnvelope? envelope = null;
        MonitorInfo? monitor;
        var version = 0;

        lock (_frameSync)
        {
            if (_currentFrame is null || _monitor is null || !IsHandleCreated || !Visible)
            {
                Interlocked.Exchange(ref _renderQueued, 0);
                return;
            }

            envelope = _currentFrame;
            _currentFrame = null; // hand ownership to the only queued render; new arrivals replace only unsubmitted frames.
            if (envelope.MetricsEnabled) LatencyMetrics.Global.SetQueueLength(0);
            monitor = _monitor;
            version = _frameVersion;
        }

        envelope.Timing.UiRenderStarted = FrameTiming.Now;
        var frame = envelope.Bitmap;

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (_legacyMemoryDc == IntPtr.Zero) _legacyMemoryDc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
        var memDc = _legacyMemoryDc;
        envelope.Timing.HBitmapStarted = FrameTiming.Now;
        var hBitmap = frame.GetHbitmap(Color.FromArgb(0));
        envelope.Timing.HBitmapReturned = FrameTiming.Now;
        var oldBitmap = NativeMethods.SelectObject(memDc, hBitmap);

        try
        {
            var dst = new NativeMethods.Point(monitor.Bounds.Left, monitor.Bounds.Top);
            var size = new NativeMethods.Size(monitor.Bounds.Width, monitor.Bounds.Height);
            var src = new NativeMethods.Point(0, 0);
            var blend = new NativeMethods.BlendFunction
            {
                BlendOp = 0,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = NativeMethods.AcSrcAlpha
            };

            envelope.Timing.UpdateLayeredWindowStarted = FrameTiming.Now;
            NativeMethods.UpdateLayeredWindow(
                Handle,
                screenDc,
                ref dst,
                ref size,
                memDc,
                ref src,
                0,
                ref blend,
                NativeMethods.UlwAlpha);
            envelope.Timing.UpdateLayeredWindowReturned = FrameTiming.Now;
        }
        finally
        {
            NativeMethods.SelectObject(memDc, oldBitmap);
            NativeMethods.DeleteObject(hBitmap);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            envelope.Dispose();
            if (envelope.MetricsEnabled) LatencyMetrics.Global.RecordSubmitted(envelope.Timing);
            if (envelope.Csv is not null && envelope.Timing.FrameId % envelope.CsvInterval == 0)
                envelope.Csv.TryWrite(envelope.Timing, envelope.Pipeline, envelope.InputAdapter, envelope.OutputAdapter, 0, 0);
            _renderedVersion = version;
            Interlocked.Exchange(ref _renderQueued, 0);
        }

        if (_renderedVersion != Volatile.Read(ref _frameVersion))
        {
            QueueRender();
        }
    }

    private void QueueRender()
    {
        if (!Visible || !IsHandleCreated || IsDisposed)
        {
            return;
        }

        if (Interlocked.Exchange(ref _renderQueued, 1) == 1)
        {
            return;
        }

        try
        {
            BeginInvoke(new Action(RenderFrame));
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _renderQueued, 0);
        }
    }

}
