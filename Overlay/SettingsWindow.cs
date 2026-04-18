using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Overlay;

internal sealed class SettingsWindow : Form
{
    private readonly AppState _state;
    private readonly Dictionary<string, Rectangle> _buttons = new();
    private readonly Font _heroFont = new("Segoe UI Semibold", 28f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _titleFont = new("Segoe UI Semibold", 18f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _bodyFont = new("Segoe UI", 15f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _smallFont = new("Consolas", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly StringFormat _ellipsisFormat = new()
    {
        Trimming = StringTrimming.EllipsisCharacter,
        FormatFlags = StringFormatFlags.NoWrap
    };
    private readonly StringFormat _centerFormat = new()
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center
    };
    private string? _hoveredButton;

    public SettingsWindow(AppState state)
    {
        _state = state;
        _state.StateChanged += HandleStateChanged;

        Text = "YarrOverlay Control";
        ClientSize = new Size(1260, 780);
        MinimumSize = new Size(1260, 780);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        DoubleBuffered = true;
        KeyPreview = true;
        BackColor = Color.FromArgb(10, 12, 16);
        ForeColor = Color.Gainsboro;

        RegisterGlobalHotkeys();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        _buttons.Clear();

        DrawBackdrop(g);
        DrawHeader(g);
        DrawLayout(g);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var previous = _hoveredButton;
        _hoveredButton = null;

        foreach (var (key, rect) in _buttons)
        {
            if (rect.Contains(e.Location))
            {
                _hoveredButton = key;
                break;
            }
        }

        if (previous != _hoveredButton)
        {
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        foreach (var (key, rect) in _buttons)
        {
            if (rect.Contains(e.Location))
            {
                ActivateButton(key);
                return;
            }
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.KeyCode == Keys.Oemplus || e.KeyCode == Keys.Add)
        {
            _state.IncreaseThreshold(1);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.OemMinus || e.KeyCode == Keys.Subtract)
        {
            _state.IncreaseThreshold(-1);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Space)
        {
            _state.ToggleRunning();
            e.Handled = true;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WmHotkey)
        {
            if (m.WParam.ToInt32() == NativeMethods.HotkeyIncrease)
            {
                _state.IncreaseThreshold(1);
            }
            else if (m.WParam.ToInt32() == NativeMethods.HotkeyDecrease)
            {
                _state.IncreaseThreshold(-1);
            }
        }

        base.WndProc(ref m);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        UnregisterGlobalHotkeys();
        _state.StateChanged -= HandleStateChanged;
        base.OnFormClosed(e);
    }

    private void DrawBackdrop(Graphics g)
    {
        using var topGlow = new LinearGradientBrush(
            new Rectangle(0, 0, ClientSize.Width, ClientSize.Height),
            Color.FromArgb(20, 24, 34),
            Color.FromArgb(8, 10, 14),
            90f);
        g.FillRectangle(topGlow, ClientRectangle);

        using var accentBrush = new SolidBrush(Color.FromArgb(28, 255, 214, 102));
        g.FillEllipse(accentBrush, new Rectangle(-80, -120, 420, 240));
        g.FillEllipse(accentBrush, new Rectangle(ClientSize.Width - 260, 18, 220, 140));
    }

    private void DrawHeader(Graphics g)
    {
        using var accentBrush = new SolidBrush(Color.FromArgb(255, 255, 214, 102));
        using var subBrush = new SolidBrush(Color.FromArgb(184, 190, 198));
        using var chipBrush = new SolidBrush(_state.IsRunning ? Color.FromArgb(28, 46, 34) : Color.FromArgb(44, 25, 25));
        using var chipTextBrush = new SolidBrush(_state.IsRunning ? Color.FromArgb(120, 255, 170) : Color.FromArgb(255, 140, 140));

        g.DrawString("YarrOverlay", _heroFont, accentBrush, 42, 30);
        g.DrawString("Monitor chroma overlay routing with click-through output", _bodyFont, subBrush, 44, 74);

        var chip = new Rectangle(ClientSize.Width - 208, 34, 150, 38);
        FillRoundedRect(g, chipBrush, chip, 18);
        g.DrawString(_state.IsRunning ? "LIVE OUTPUT" : "STOPPED", _smallFont, chipTextBrush, chip.X + 21, chip.Y + 12);
    }

    private void DrawLayout(Graphics g)
    {
        var left = new Rectangle(40, 120, 760, 610);
        var rightTop = new Rectangle(828, 120, 392, 292);
        var rightBottom = new Rectangle(828, 438, 392, 292);

        DrawRoutingCard(g, left);
        DrawStatusCard(g, rightTop);
        DrawTipsCard(g, rightBottom);
    }

    private void DrawRoutingCard(Graphics g, Rectangle card)
    {
        DrawCard(g, card, "Routing + Keying", "Input/output assignment and black-key strength");

        var x = card.X + 28;
        var y = card.Y + 88;

        DrawChooser(g, "Input Monitor", _state.CurrentInputMonitor.Name, "inputPrev", "inputNext", x, y, card.Width - 56);
        y += 108;
        DrawChooser(g, "Output Monitor", _state.CurrentOutputMonitor.Name, "outputPrev", "outputNext", x, y, card.Width - 56);
        y += 118;
        DrawThreshold(g, x, y, card.Width - 56);
        y += 128;
        DrawSharpness(g, x, y, card.Width - 56);
        y += 128;
        DrawTransport(g, x, y, card.Width - 56);
    }

    private void DrawStatusCard(Graphics g, Rectangle card)
    {
        DrawCard(g, card, "Live Status", "Current route and transport health");

        using var mainBrush = new SolidBrush(Color.FromArgb(232, 236, 240));
        using var dimBrush = new SolidBrush(Color.FromArgb(165, 172, 184));
        using var okBrush = new SolidBrush(Color.FromArgb(120, 255, 170));
        using var warnBrush = new SolidBrush(Color.FromArgb(255, 140, 140));
        using var panelBrush = new SolidBrush(Color.FromArgb(18, 21, 27));

        var inner = new Rectangle(card.X + 24, card.Y + 88, card.Width - 48, 146);
        FillRoundedRect(g, panelBrush, inner, 18);

        g.DrawString("Route", _smallFont, dimBrush, inner.X + 18, inner.Y + 18);
        g.DrawString($"{_state.CurrentInputMonitor.Name}", _bodyFont, mainBrush, new RectangleF(inner.X + 18, inner.Y + 40, inner.Width - 36, 24), _ellipsisFormat);
        g.DrawString("to", _smallFont, dimBrush, inner.X + 18, inner.Y + 70);
        g.DrawString($"{_state.CurrentOutputMonitor.Name}", _bodyFont, mainBrush, new RectangleF(inner.X + 18, inner.Y + 92, inner.Width - 36, 24), _ellipsisFormat);

        g.DrawString($"FPS  {_state.CaptureFps}", _titleFont, _state.IsRunning ? okBrush : warnBrush, card.X + 24, card.Y + 242);
    }

    private void DrawTipsCard(Graphics g, Rectangle card)
    {
        DrawCard(g, card, "Controls", "Shortcuts and behavior");

        using var textBrush = new SolidBrush(Color.FromArgb(210, 214, 220));
        using var dimBrush = new SolidBrush(Color.FromArgb(160, 168, 180));

        var lines = new[]
        {
            "Space  : start or stop overlay transport",
            "+ / -  : raise or lower black-key threshold globally",
            "Refresh: rescan monitors after display topology changes",
            "Output window stays topmost and click-through for in-game use"
        };

        var y = card.Y + 92;
        foreach (var line in lines)
        {
            g.DrawString(line, _bodyFont, textBrush, new RectangleF(card.X + 24, y, card.Width - 48, 34));
            y += 42;
        }

        g.DrawString($"Status: {_state.StatusText}", _smallFont, dimBrush, new RectangleF(card.X + 24, card.Bottom - 44, card.Width - 48, 22), _ellipsisFormat);
    }

    private void DrawCard(Graphics g, Rectangle rect, string title, string subtitle)
    {
        using var fillBrush = new SolidBrush(Color.FromArgb(26, 30, 38));
        using var borderPen = new Pen(Color.FromArgb(66, 72, 84));
        using var titleBrush = new SolidBrush(Color.FromArgb(245, 246, 248));
        using var subtitleBrush = new SolidBrush(Color.FromArgb(152, 160, 170));

        FillRoundedRect(g, fillBrush, rect, 26);
        DrawRoundedRect(g, borderPen, rect, 26);
        g.DrawString(title, _titleFont, titleBrush, rect.X + 24, rect.Y + 24);
        g.DrawString(subtitle, _smallFont, subtitleBrush, rect.X + 24, rect.Y + 56);
    }

    private void DrawChooser(Graphics g, string label, string value, string leftButtonKey, string rightButtonKey, int x, int y, int width)
    {
        using var labelBrush = new SolidBrush(Color.FromArgb(210, 214, 220));
        using var boxBrush = new SolidBrush(Color.FromArgb(16, 19, 24));
        using var strokePen = new Pen(Color.FromArgb(74, 80, 94));
        using var textBrush = new SolidBrush(Color.FromArgb(244, 245, 247));

        g.DrawString(label, _bodyFont, labelBrush, x, y);

        var rowY = y + 34;
        var leftRect = new Rectangle(x, rowY, 52, 52);
        var rightRect = new Rectangle(x + width - 52, rowY, 52, 52);
        var valueRect = new Rectangle(x + 68, rowY, width - 136, 52);

        DrawButton(g, leftRect, "<", leftButtonKey, true);
        FillRoundedRect(g, boxBrush, valueRect, 16);
        DrawRoundedRect(g, strokePen, valueRect, 16);
        g.DrawString(value, _bodyFont, textBrush, new RectangleF(valueRect.X + 16, valueRect.Y + 15, valueRect.Width - 32, 24), _ellipsisFormat);
        DrawButton(g, rightRect, ">", rightButtonKey, true);
    }

    private void DrawThreshold(Graphics g, int x, int y, int width)
    {
        using var labelBrush = new SolidBrush(Color.FromArgb(210, 214, 220));
        using var dimBrush = new SolidBrush(Color.FromArgb(160, 168, 180));
        using var trackBrush = new SolidBrush(Color.FromArgb(16, 19, 24));
        using var fillBrush = new LinearGradientBrush(
            new Rectangle(x, y, width, 52),
            Color.FromArgb(255, 255, 214, 102),
            Color.FromArgb(255, 255, 166, 76),
            0f);
        using var borderPen = new Pen(Color.FromArgb(74, 80, 94));

        g.DrawString("Black Threshold", _bodyFont, labelBrush, x, y);

        var rowY = y + 34;
        var leftRect = new Rectangle(x, rowY, 52, 52);
        var rightRect = new Rectangle(x + width - 52, rowY, 52, 52);
        var trackRect = new Rectangle(x + 68, rowY + 15, width - 216, 22);
        var pillRect = new Rectangle(trackRect.Right + 16, rowY, 80, 52);
        var fillWidth = Math.Max(10, (int)(trackRect.Width * (_state.ChromaThreshold / 80f)));

        DrawButton(g, leftRect, "-", "thresholdDown", true);
        FillRoundedRect(g, trackBrush, new Rectangle(trackRect.X, trackRect.Y, trackRect.Width, trackRect.Height), 11);
        FillRoundedRect(g, fillBrush, new Rectangle(trackRect.X, trackRect.Y, fillWidth, trackRect.Height), 11);
        DrawRoundedRect(g, borderPen, new Rectangle(trackRect.X, trackRect.Y, trackRect.Width, trackRect.Height), 11);

        FillRoundedRect(g, trackBrush, pillRect, 16);
        DrawRoundedRect(g, borderPen, pillRect, 16);
        g.DrawString($"{_state.ChromaThreshold}", _titleFont, Brushes.WhiteSmoke, new RectangleF(pillRect.X, pillRect.Y, pillRect.Width, pillRect.Height), _centerFormat);
        DrawButton(g, rightRect, "+", "thresholdUp", true);

        g.DrawString("0 keeps more shadow detail, 80 removes darker pixels more aggressively.", _smallFont, dimBrush, x, rowY + 66);
    }

    private void DrawTransport(Graphics g, int x, int y, int width)
    {
        DrawButton(g, new Rectangle(x, y, 180, 54), _state.IsRunning ? "Stop Overlay" : "Start Overlay", "toggle", false);
        DrawButton(g, new Rectangle(x + 198, y, 150, 54), "Refresh Displays", "refresh", false);

        using var hintBrush = new SolidBrush(Color.FromArgb(160, 168, 180));
        g.DrawString("Overlay stays on the selected output monitor and forwards clicks to the game window below.", _smallFont, hintBrush, new RectangleF(x, y + 70, width - 10, 40));
    }

    private void DrawSharpness(Graphics g, int x, int y, int width)
    {
        using var labelBrush = new SolidBrush(Color.FromArgb(210, 214, 220));
        using var dimBrush = new SolidBrush(Color.FromArgb(160, 168, 180));
        using var trackBrush = new SolidBrush(Color.FromArgb(16, 19, 24));
        using var fillBrush = new LinearGradientBrush(
            new Rectangle(x, y, width, 52),
            Color.FromArgb(120, 196, 255),
            Color.FromArgb(92, 150, 255),
            0f);
        using var borderPen = new Pen(Color.FromArgb(74, 80, 94));

        g.DrawString("GPU Sharpness", _bodyFont, labelBrush, x, y);

        var rowY = y + 34;
        var leftRect = new Rectangle(x, rowY, 52, 52);
        var rightRect = new Rectangle(x + width - 52, rowY, 52, 52);
        var trackRect = new Rectangle(x + 68, rowY + 15, width - 216, 22);
        var pillRect = new Rectangle(trackRect.Right + 16, rowY, 80, 52);
        var fillWidth = Math.Max(10, (int)(trackRect.Width * (_state.Sharpness / 100f)));

        DrawButton(g, leftRect, "-", "sharpnessDown", true);
        FillRoundedRect(g, trackBrush, new Rectangle(trackRect.X, trackRect.Y, trackRect.Width, trackRect.Height), 11);
        FillRoundedRect(g, fillBrush, new Rectangle(trackRect.X, trackRect.Y, fillWidth, trackRect.Height), 11);
        DrawRoundedRect(g, borderPen, new Rectangle(trackRect.X, trackRect.Y, trackRect.Width, trackRect.Height), 11);

        FillRoundedRect(g, trackBrush, pillRect, 16);
        DrawRoundedRect(g, borderPen, pillRect, 16);
        g.DrawString($"{_state.Sharpness}", _titleFont, Brushes.WhiteSmoke, new RectangleF(pillRect.X, pillRect.Y, pillRect.Width, pillRect.Height), _centerFormat);
        DrawButton(g, rightRect, "+", "sharpnessUp", true);

        g.DrawString("0 is pure linear scaling, 100 pushes the GPU sharpening pass hardest.", _smallFont, dimBrush, x, rowY + 66);
    }

    private void DrawButton(Graphics g, Rectangle rect, string text, string key, bool compact)
    {
        var hovered = key == _hoveredButton;
        var baseColor = compact ? Color.FromArgb(255, 255, 214, 102) : Color.FromArgb(255, 255, 185, 88);
        var hoverColor = compact ? Color.FromArgb(255, 255, 228, 138) : Color.FromArgb(255, 255, 204, 118);

        using var fillBrush = new SolidBrush(hovered ? hoverColor : baseColor);
        using var borderPen = new Pen(Color.FromArgb(35, 35, 35));
        using var textBrush = new SolidBrush(Color.FromArgb(20, 20, 20));

        FillRoundedRect(g, fillBrush, rect, 16);
        DrawRoundedRect(g, borderPen, rect, 16);

        var font = compact ? _titleFont : _bodyFont;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.DrawString(text, font, textBrush, new RectangleF(rect.X, rect.Y, rect.Width, rect.Height), _centerFormat);
        _buttons[key] = rect;
    }

    private void ActivateButton(string key)
    {
        switch (key)
        {
            case "inputPrev":
                _state.CycleInput(-1);
                break;
            case "inputNext":
                _state.CycleInput(1);
                break;
            case "outputPrev":
                _state.CycleOutput(-1);
                break;
            case "outputNext":
                _state.CycleOutput(1);
                break;
            case "thresholdDown":
                _state.IncreaseThreshold(-1);
                break;
            case "thresholdUp":
                _state.IncreaseThreshold(1);
                break;
            case "toggle":
                _state.ToggleRunning();
                break;
            case "refresh":
                _state.RefreshMonitors();
                break;
            case "sharpnessDown":
                _state.IncreaseSharpness(-2);
                break;
            case "sharpnessUp":
                _state.IncreaseSharpness(2);
                break;
        }
    }

    private void HandleStateChanged()
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(Invalidate));
            return;
        }

        Invalidate();
    }

    private void RegisterGlobalHotkeys()
    {
        NativeMethods.RegisterHotKey(Handle, NativeMethods.HotkeyIncrease, NativeMethods.ModNorepeat, NativeMethods.VkOemplus);
        NativeMethods.RegisterHotKey(Handle, NativeMethods.HotkeyDecrease, NativeMethods.ModNorepeat, NativeMethods.VkOemMinus);
    }

    private void UnregisterGlobalHotkeys()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        NativeMethods.UnregisterHotKey(Handle, NativeMethods.HotkeyIncrease);
        NativeMethods.UnregisterHotKey(Handle, NativeMethods.HotkeyDecrease);
    }

    private static void FillRoundedRect(Graphics g, Brush brush, Rectangle rect, int radius)
    {
        using var path = CreateRoundedRectPath(rect, radius);
        g.FillPath(brush, path);
    }

    private static void DrawRoundedRect(Graphics g, Pen pen, Rectangle rect, int radius)
    {
        using var path = CreateRoundedRectPath(rect, radius);
        g.DrawPath(pen, path);
    }

    private static GraphicsPath CreateRoundedRectPath(Rectangle rect, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
