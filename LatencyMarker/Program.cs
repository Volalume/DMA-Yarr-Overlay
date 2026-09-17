using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace LatencyMarker;

internal static class Program
{
    [STAThread] private static void Main() { ApplicationConfiguration.Initialize(); Application.Run(new MarkerForm()); }
}
internal sealed class MarkerForm : Form
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 8 };
    private readonly Font _font = new("Consolas", 24, FontStyle.Bold);
    private long _frame;
    public MarkerForm()
    {
        Text="Overlay Latency Marker — place on Parsec host"; ClientSize=new Size(900,300); DoubleBuffered=true; TopMost=true;
        _timer.Tick+=(_,_)=>{_frame++;Invalidate();}; _timer.Start();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var gray=_frame^(_frame>>1); e.Graphics.Clear((_frame&1)==0?Color.Black:Color.White);
        var fg=(_frame&1)==0?Brushes.White:Brushes.Black;
        e.Graphics.DrawString($"FRAME {_frame}\nQPC {Stopwatch.GetTimestamp()}\nGRAY 0x{gray:X16}",_font,fg,24,20);
        for(var i=0;i<32;i++){var on=((gray>>i)&1)!=0; using var b=new SolidBrush(on?Color.White:Color.Black); e.Graphics.FillRectangle(b,24+i*26,210,24,64); e.Graphics.DrawRectangle(Pens.Magenta,24+i*26,210,24,64);}
    }
    protected override void Dispose(bool disposing){if(disposing){_timer.Dispose();_font.Dispose();}base.Dispose(disposing);}
}
