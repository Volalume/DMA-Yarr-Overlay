using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace Overlay;

internal sealed class OverlayWindow : Form
{
    private readonly object _frameSync = new();
    private Bitmap? _currentFrame;
    private MonitorInfo? _monitor;
    private int _renderQueued;
    private int _frameVersion;
    private int _renderedVersion;

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
                | NativeMethods.WsExNoActivate;
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
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
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

    public void SetFrame(Bitmap frame)
    {
        if (IsDisposed)
        {
            frame.Dispose();
            return;
        }

        lock (_frameSync)
        {
            _currentFrame?.Dispose();
            _currentFrame = frame;
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
        }

        base.Dispose(disposing);
    }

    private void RenderFrame()
    {
        Bitmap? frame = null;
        MonitorInfo? monitor;
        var version = 0;

        lock (_frameSync)
        {
            if (_currentFrame is null || _monitor is null || !IsHandleCreated || !Visible)
            {
                Interlocked.Exchange(ref _renderQueued, 0);
                return;
            }

            frame = CreateRenderFrame(_currentFrame, _monitor.Bounds.Size);
            monitor = _monitor;
            version = _frameVersion;
        }

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        var memDc = NativeMethods.CreateCompatibleDC(screenDc);
        var hBitmap = frame.GetHbitmap(Color.FromArgb(0));
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
        }
        finally
        {
            NativeMethods.SelectObject(memDc, oldBitmap);
            NativeMethods.DeleteObject(hBitmap);
            NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            frame.Dispose();
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

    private static Bitmap CreateRenderFrame(Bitmap source, Size targetSize)
    {
        if (source.Width == targetSize.Width && source.Height == targetSize.Height)
        {
            return (Bitmap)source.Clone();
        }

        var scaled = new Bitmap(targetSize.Width, targetSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using (var g = Graphics.FromImage(scaled))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.SmoothingMode = SmoothingMode.HighSpeed;
            g.DrawImage(source, new Rectangle(0, 0, targetSize.Width, targetSize.Height));
        }

        return scaled;
    }
}
