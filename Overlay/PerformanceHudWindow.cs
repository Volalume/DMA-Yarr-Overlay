using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Overlay;

internal sealed class PerformanceHudWindow : Form
{
    private const double BasicDeadbandMs = 1.0;
    private const string WaitingText = "YarrOverlay | Waiting for starting...";
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private readonly Font _font = new("Consolas", 10.5f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _totalFont = new("Consolas", 14f, FontStyle.Bold, GraphicsUnit.Point);
    private MonitorInfo? _monitor;
    private PerformanceHudMode _mode;
    private bool _running;
    private double _displayedBasicLatency = double.NaN;
    public PerformanceHudWindow()
    {
        FormBorderStyle=FormBorderStyle.None; ShowInTaskbar=false; TopMost=true; StartPosition=FormStartPosition.Manual;
        BackColor=Color.FromArgb(13,16,22); ForeColor=Color.White; Opacity=.94; ClientSize=new Size(510,190);
        DoubleBuffered = true;
        ResizeRedraw = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        _timer.Tick += (_,_) => { if (_running && _mode != PerformanceHudMode.Off) Invalidate(); };
        _timer.Start();
    }
    protected override CreateParams CreateParams { get { var cp=base.CreateParams; cp.ExStyle|=NativeMethods.WsExTransparent|NativeMethods.WsExToolWindow|NativeMethods.WsExNoActivate; return cp; } }
    protected override bool ShowWithoutActivation => true;
    public void SetMonitor(MonitorInfo monitor) { _monitor=monitor; Location=new Point(monitor.Bounds.X+16,monitor.Bounds.Y+16); }
    public void SetMode(PerformanceHudMode mode, bool running)
    {
        if (_mode != mode || !running) _displayedBasicLatency = double.NaN;
        _mode=mode;
        _running=running;
        if (!running)
        {
            SetBasicHudSize(WaitingText);
            ApplyRoundedShape(12);
            ShowHud();
        }
        else if(mode==PerformanceHudMode.Off) Hide();
        else
        {
            if (mode == PerformanceHudMode.Basic) SetBasicHudSize("YarrOverlay | 9999 ms");
            else ClientSize=new Size(510,190);
            ApplyRoundedShape(mode == PerformanceHudMode.Basic ? 12 : 10);
            ShowHud();
        }
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        if (!_running)
        {
            DrawBasicBox(e.Graphics, WaitingText);
            return;
        }

        var p=LatencyMetrics.Global.Snapshot(); var w=p.TenSeconds; var y=10f;
        if(_mode==PerformanceHudMode.Basic)
        {
            DrawBasicBox(e.Graphics, $"YarrOverlay | {FBasic(PresentBasicLatency(p.TotalAppLatencyEstimateMs))} ms");
            return;
        }
        void Line(string s){e.Graphics.DrawString(s,_font,Brushes.White,10,y);y+=20;}
        Line($"YarrOverlay | submit {w.SubmitFps:F1} fps | queue {p.QueueLength}/{p.MaxQueueLength}");
        e.Graphics.DrawString($"TOTAL APP LATENCY  {F(p.TotalAppLatencyEstimateMs)} ms  (estimate)",_totalFont,Brushes.LightGreen,10,y);y+=30;
        Line($"software submit  {F(w.Pipeline.Current)} cur  {F(w.Pipeline.Average)} avg  {F(w.Pipeline.P95)} p95 ms");
        if(_mode==PerformanceHudMode.Detailed){Line($"age {F(w.FrameAge.Average)} interval {F(w.FrameInterval.Average)} acquire {F(w.AcquireWait.Average)} map {F(w.MapWait.Average)}");Line($"CPU copy {F(w.CpuCopy.Average)} UI {F(w.UiQueue.Average)} GPU copy/shader/total {F(w.GpuCopy.Average)}/{F(w.GpuShader.Average)}/{F(w.GpuTotal.Average)}");Line($"captured {p.CapturedFrames} submitted {p.SubmittedFrames} dropped {p.DroppedFrames} replaced {p.ReplacedFrames}");Line($"accumulated {p.AccumulatedFrames} max {p.MaxAccumulatedFrames} DXGI errors {p.DxgiErrors}");}
    }
    protected override void Dispose(bool disposing){if(disposing){_timer.Dispose();_font.Dispose();_totalFont.Dispose();}base.Dispose(disposing);}
    private static string F(double v)=>double.IsNaN(v)?"N/A":v.ToString("F2");
    private static string FBasic(double v)=>double.IsNaN(v)?"N/A":v.ToString("F0");

    private double PresentBasicLatency(double raw)
    {
        if (double.IsNaN(raw) || double.IsInfinity(raw)) return raw;
        if (double.IsNaN(_displayedBasicLatency)) _displayedBasicLatency = raw;
        if (Math.Abs(raw - _displayedBasicLatency) > BasicDeadbandMs) _displayedBasicLatency = raw;
        return _displayedBasicLatency;
    }

    private void SetBasicHudSize(string widestText)
    {
        var textSize = TextRenderer.MeasureText(widestText, _font);
        ClientSize = new Size(Math.Max(248, textSize.Width + 42), Math.Max(44, textSize.Height + 22));
    }

    private void DrawBasicBox(Graphics graphics, string text)
    {
        using var background = new SolidBrush(Color.FromArgb(20, 24, 32));
        using var border = new Pen(Color.FromArgb(72, 79, 92));
        using var accent = new SolidBrush(Color.FromArgb(255, 205, 72));
        graphics.FillRectangle(background, ClientRectangle);
        graphics.FillRectangle(accent, 0, 0, 4, ClientSize.Height);
        graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        graphics.DrawString(text, _font, Brushes.White, 14, 12);
    }

    private void ShowHud()
    {
        Show();
        NativeMethods.SetWindowPos(Handle,new IntPtr(NativeMethods.HwndTopmost),Left,Top,Width,Height,NativeMethods.SwpNoActivate|NativeMethods.SwpShowWindow);
    }

    private void ApplyRoundedShape(int radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        var rect = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        var oldRegion = Region;
        Region = new Region(path);
        oldRegion?.Dispose();
    }
}
