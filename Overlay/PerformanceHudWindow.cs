using System;
using System.Drawing;
using System.Windows.Forms;

namespace Overlay;

internal sealed class PerformanceHudWindow : Form
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private readonly Font _font = new("Consolas", 10f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _totalFont = new("Consolas", 14f, FontStyle.Bold, GraphicsUnit.Point);
    private MonitorInfo? _monitor;
    private PerformanceHudMode _mode;
    public PerformanceHudWindow()
    {
        FormBorderStyle=FormBorderStyle.None; ShowInTaskbar=false; TopMost=true; StartPosition=FormStartPosition.Manual;
        BackColor=Color.FromArgb(16,16,20); ForeColor=Color.White; Opacity=.86; ClientSize=new Size(510,190);
        _timer.Tick += (_,_) => Invalidate(); _timer.Start();
    }
    protected override CreateParams CreateParams { get { var cp=base.CreateParams; cp.ExStyle|=NativeMethods.WsExTransparent|NativeMethods.WsExToolWindow|NativeMethods.WsExNoActivate; return cp; } }
    protected override bool ShowWithoutActivation => true;
    public void SetMonitor(MonitorInfo monitor) { _monitor=monitor; Location=new Point(monitor.Bounds.X+16,monitor.Bounds.Y+16); }
    public void SetMode(PerformanceHudMode mode, bool running) { _mode=mode; if(mode==PerformanceHudMode.Off||!running) Hide(); else { ClientSize=mode==PerformanceHudMode.Basic?new Size(220,38):new Size(510,190); Show(); NativeMethods.SetWindowPos(Handle,new IntPtr(NativeMethods.HwndTopmost),Left,Top,Width,Height,NativeMethods.SwpNoActivate|NativeMethods.SwpShowWindow); } }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var p=LatencyMetrics.Global.Snapshot(); var w=p.TenSeconds; var y=10f;
        if(_mode==PerformanceHudMode.Basic){e.Graphics.DrawString($"YarrOverlay | {F(p.TotalAppLatencyEstimateMs)} ms",_font,Brushes.White,10,y);return;}
        void Line(string s){e.Graphics.DrawString(s,_font,Brushes.White,10,y);y+=20;}
        Line($"YarrOverlay | submit {w.SubmitFps:F1} fps | queue {p.QueueLength}/{p.MaxQueueLength}");
        e.Graphics.DrawString($"TOTAL APP LATENCY  {F(p.TotalAppLatencyEstimateMs)} ms  (estimate)",_totalFont,Brushes.LightGreen,10,y);y+=30;
        Line($"software submit  {F(w.Pipeline.Current)} cur  {F(w.Pipeline.Average)} avg  {F(w.Pipeline.P95)} p95 ms");
        if(_mode==PerformanceHudMode.Detailed){Line($"age {F(w.FrameAge.Average)} interval {F(w.FrameInterval.Average)} acquire {F(w.AcquireWait.Average)} map {F(w.MapWait.Average)}");Line($"CPU copy {F(w.CpuCopy.Average)} UI {F(w.UiQueue.Average)} GPU copy/shader/total {F(w.GpuCopy.Average)}/{F(w.GpuShader.Average)}/{F(w.GpuTotal.Average)}");Line($"captured {p.CapturedFrames} submitted {p.SubmittedFrames} dropped {p.DroppedFrames} replaced {p.ReplacedFrames}");Line($"accumulated {p.AccumulatedFrames} max {p.MaxAccumulatedFrames} DXGI errors {p.DxgiErrors}");}
    }
    protected override void Dispose(bool disposing){if(disposing){_timer.Dispose();_font.Dispose();_totalFont.Dispose();}base.Dispose(disposing);}
    private static string F(double v)=>double.IsNaN(v)?"N/A":v.ToString("F2");
}
