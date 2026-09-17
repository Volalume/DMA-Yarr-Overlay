using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace Overlay;

internal sealed class PerformanceDetailsWindow : Form
{
    private readonly AppState _state;
    private readonly RichTextBox _text = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    public PerformanceDetailsWindow(AppState state)
    {
        _state=state; Text="Overlay — Full Latency Metrics"; ClientSize=new Size(1180,820); MinimumSize=new Size(900,600);
        BackColor=Color.FromArgb(10,12,16); ForeColor=Color.Gainsboro;
        _text.Dock=DockStyle.Fill;_text.ReadOnly=true;_text.BorderStyle=BorderStyle.None;_text.BackColor=BackColor;_text.ForeColor=ForeColor;_text.Font=new Font("Consolas",10f);_text.WordWrap=false;_text.ScrollBars=RichTextBoxScrollBars.Both;Controls.Add(_text);
        _timer.Tick+=(_,_)=>RefreshText();_timer.Start();RefreshText();
    }
    private void RefreshText()
    {
        var d=_state.Diagnostics;var p=d.Performance??LatencyMetrics.Global.Snapshot();var b=new StringBuilder(8192);
        b.AppendLine($"Pipeline: {d.PipelineMode}    capture/render thread: {d.CaptureThreadId}/{d.RenderThreadId}    same adapter: {d.SameAdapter}");
        b.AppendLine($"Capture adapter: {d.CaptureAdapter}");b.AppendLine($"Output adapter : {d.OutputAdapter}");
        b.AppendLine($"Last DXGI error: {(string.IsNullOrWhiteSpace(d.LastError)?"none":d.LastError)}");
        b.AppendLine($"Recreates: {d.RecreateCount}    recovery last/total: {d.LastRecoveryMs:F2}/{d.TotalRecoveryMs:F2} ms    last good frame: {(d.LastFrameAt is null?"N/A":(DateTimeOffset.Now-d.LastFrameAt.Value).TotalSeconds.ToString("F2")+" s ago")}");
        b.AppendLine($"Captured/submitted/replaced/dropped: {p.CapturedFrames}/{p.SubmittedFrames}/{p.ReplacedFrames}/{p.DroppedFrames}    timeouts/errors: {p.AcquireTimeouts}/{p.DxgiErrors}");
        b.AppendLine($"Accumulated sum/max: {p.AccumulatedFrames}/{p.MaxAccumulatedFrames}    queue current/max: {p.QueueLength}/{p.MaxQueueLength}    CSV lost: {d.CsvLostRows}");
        b.AppendLine($"GC 0/1/2: {d.Gen0}/{d.Gen1}/{d.Gen2}    allocated: {d.AllocatedBytes/1048576.0:F1} MiB    working set: {d.WorkingSetBytes/1048576.0:F1} MiB");
        AppendWindow(b,"RECENT 1 SECOND",p.OneSecond);AppendWindow(b,"RECENT 10 SECONDS",p.TenSeconds);AppendWindow(b,"WHOLE SESSION",p.Session);
        var next=b.ToString();if(_text.Text!=next){var selection=_text.SelectionStart;_text.Text=next;_text.SelectionStart=Math.Min(selection,_text.TextLength);}
    }
    private static void AppendWindow(StringBuilder b,string name,WindowPerformance w)
    {
        b.AppendLine();b.AppendLine($"=== {name} ===    capture {w.CaptureFps:F1} fps    submit {w.SubmitFps:F1} fps");
        b.AppendLine("metric (ms)                   current       avg       min       max       p50       p95       p99");
        Append(b,"software pipeline",w.Pipeline);Append(b,"desktop->submit estimate",w.DesktopToSubmit);Append(b,"acquire wait",w.AcquireWait);Append(b,"Map wait",w.MapWait);Append(b,"CPU frame copy",w.CpuCopy);Append(b,"UI queue",w.UiQueue);Append(b,"GetHbitmap",w.HBitmap);Append(b,"present/ULW submit",w.Submit);Append(b,"frame age at submit",w.FrameAge);Append(b,"submitted frame interval",w.FrameInterval);Append(b,"GPU source copy",w.GpuCopy);Append(b,"GPU shader",w.GpuShader);Append(b,"GPU total",w.GpuTotal);
    }
    private static void Append(StringBuilder b,string name,TimingStats s)=>b.AppendLine($"{name,-28}{F(s.Current),10}{F(s.Average),10}{F(s.Minimum),10}{F(s.Maximum),10}{F(s.P50),10}{F(s.P95),10}{F(s.P99),10}");
    private static string F(double value)=>double.IsNaN(value)?"N/A":value.ToString("F3");
    protected override void Dispose(bool disposing){if(disposing){_timer.Stop();_timer.Dispose();_text.Dispose();}base.Dispose(disposing);}
}
