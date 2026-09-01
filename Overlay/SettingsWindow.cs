using System;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Hexa.NET.ImGui;

namespace Overlay;

internal sealed class SettingsWindow : Form
{
    private readonly AppState _state;
    private readonly System.Windows.Forms.Timer _renderTimer;
    private ImGuiD3D11Controller? _controller;
    private HotkeyManager? _hotkeys;
    private PerformanceSnapshot? _performance;
    private long _nextPerformanceRefresh;
    private bool _bindingHotkey;
    private string? _notification;
    private long _thresholdHoldStarted;
    private long _lastThresholdRepeat;
    private int _lastThresholdHotkeyId;
    private int _requestedClientHeight = 630;
    private float _uiScale = 1f;
    private bool _disposed;

    public SettingsWindow(AppState state)
    {
        _state = state;
        Text = "YarrOverlay Control";
        ClientSize = new Size(1120, _requestedClientHeight);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = true;
        MaximizeBox = false;
        MinimizeBox = false;
        KeyPreview = true;
        BackColor = Color.FromArgb(10, 12, 17);
        TopMost = _state.AlwaysOnTop;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque | ControlStyles.UserPaint, true);

        _renderTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _renderTimer.Tick += RenderFrame;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _uiScale = Math.Max(1f, DeviceDpi / 96f);
        _controller = new ImGuiD3D11Controller(Handle, ClientSize.Width, ClientSize.Height, DeviceDpi / 96f);
        _hotkeys = new HotkeyManager(Handle);
        _hotkeys.RegisterThresholdHotkeys();
        if (!_hotkeys.TrySetUiHotkey(_state.UiHotkey)) _notification = _hotkeys.LastError;

        var cornerPreference = NativeMethods.DwmWindowCornerPreferenceRound;
        NativeMethods.DwmSetWindowAttribute(
            Handle,
            NativeMethods.DwmwaWindowCornerPreference,
            ref cornerPreference,
            sizeof(int));

        if (Visible && WindowState != FormWindowState.Minimized) _renderTimer.Start();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        WindowState = FormWindowState.Normal;
        TopMost = _state.AlwaysOnTop;
        _renderTimer.Start();
        BeginInvoke(new Action(() =>
        {
            if (IsDisposed) return;
            Show();
            BringToFront();
            Activate();
            NativeMethods.SetForegroundWindow(Handle);
        }));
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!_state.AlwaysOnTop || !Visible || !IsHandleCreated) return;
        NativeMethods.SetWindowPos(
            Handle,
            new IntPtr(NativeMethods.HwndTopmost),
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _renderTimer.Stop();
        _hotkeys?.Dispose();
        _hotkeys = null;
        _controller?.Dispose();
        _controller = null;
        base.OnHandleDestroyed(e);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (_controller is null) return;
        if (Visible && WindowState != FormWindowState.Minimized) _renderTimer.Start();
        else _renderTimer.Stop();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized)
        {
            _renderTimer.Stop();
            return;
        }

        if (Visible) _renderTimer.Start();
        _controller?.Resize(ClientSize.Width, ClientSize.Height);
    }

    protected override void OnPaint(PaintEventArgs e) => _controller?.Render(DrawUi);

    protected override void OnPaintBackground(PaintEventArgs e)
    {
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && e.Y < 52 && e.X < ClientSize.Width - 104)
        {
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(Handle, NativeMethods.WmNcLButtonDown, new IntPtr(NativeMethods.HtCaption), IntPtr.Zero);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_bindingHotkey)
        {
            if (e.KeyCode == Keys.Escape)
            {
                _bindingHotkey = false;
                _notification = "Hotkey binding cancelled.";
                e.Handled = true;
                return;
            }

            if (e.KeyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return;
            var binding = new HotkeyBinding(e.KeyCode, GetHotkeyModifiers(e));
            if (_hotkeys?.TrySetUiHotkey(binding) == true)
            {
                _state.SetUiHotkey(binding);
                _notification = $"UI hotkey changed to {binding.DisplayText}.";
            }
            else
            {
                _notification = _hotkeys?.LastError ?? "Could not register that hotkey.";
            }

            _bindingHotkey = false;
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode == Keys.Space)
        {
            RunUiAction(_state.ToggleRunning);
            e.Handled = true;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WmHotkey)
        {
            var id = m.WParam.ToInt32();
            if (id == NativeMethods.HotkeyIncrease) _state.IncreaseThreshold(GetThresholdHotkeyStep(id));
            else if (id == NativeMethods.HotkeyDecrease) _state.IncreaseThreshold(-GetThresholdHotkeyStep(id));
            else if (_hotkeys?.IsUiHotkey(id) == true) ToggleSettingsVisibility();
        }

        _controller?.ProcessWindowMessage(ref m);
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _renderTimer.Stop();
            _renderTimer.Tick -= RenderFrame;
            _renderTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void RenderFrame(object? sender, EventArgs e)
    {
        if (Visible && WindowState != FormWindowState.Minimized) _controller?.Render(DrawUi);
    }

    private void DrawUi()
    {
        if (_controller is null) return;
        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(ClientSize.Width, ClientSize.Height), ImGuiCond.Always);
        var flags = ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.NoSavedSettings;

        ImGui.PushFont(_controller.BodyFont, 0);
        ImGui.Begin("YarrOverlay##Root", flags);
        DrawHeader();

        if (ImGui.BeginTabBar("MainTabs", ImGuiTabBarFlags.FittingPolicyShrink | ImGuiTabBarFlags.DrawSelectedOverline))
        {
            if (ImGui.BeginTabItem("Overlay"))
            {
                RequestClientHeight(700);
                DrawOverlayTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Performance"))
            {
                RequestClientHeight(1000);
                DrawPerformanceTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Settings"))
            {
                RequestClientHeight(680);
                DrawSettingsTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        DrawFooter();
        ImGui.End();
        ImGui.PopFont();
    }

    private void DrawHeader()
    {
        var headerY = ImGui.GetCursorPosY();
        ImGui.TextColored(ImGuiTheme.Accent, "YARROVERLAY");
        var status = _state.IsRunning ? "RUNNING" : "STOPPED";
        var statusColor = _state.IsRunning ? ImGuiTheme.Good : ImGuiTheme.Warning;
        ImGui.SameLine(ImGui.GetWindowWidth() - 138);
        ImGui.TextColored(statusColor, status);

        ImGui.SetCursorPos(new Vector2(ImGui.GetWindowWidth() - 54, headerY - 4));
        if (ImGui.Button("X##CloseWindow", new Vector2(32, 28))) BeginInvoke(new Action(Close));
        ImGui.SetCursorPosY(headerY + 32);
        ImGui.Separator();
    }

    private void DrawOverlayTab()
    {
        ImGui.Spacing();
        BeginCard("RoutingCard", 170, "DISPLAY");
        ImGui.SetNextItemWidth(-190);
        if (ImGui.BeginCombo("Capture Display", _state.CurrentInputMonitor.Name))
        {
            for (var i = 0; i < _state.Monitors.Count; i++)
            {
                var selected = i == _state.SelectedInputIndex;
                if (ImGui.Selectable(_state.Monitors[i].Name, selected)) RunUiAction(() => _state.SetInputIndex(i));
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.SetNextItemWidth(-190);
        if (ImGui.BeginCombo("Output Display", _state.CurrentOutputMonitor.Name))
        {
            for (var i = 0; i < _state.Monitors.Count; i++)
            {
                var selected = i == _state.SelectedOutputIndex;
                if (ImGui.Selectable(_state.Monitors[i].Name, selected)) RunUiAction(() => _state.SetOutputIndex(i));
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.EndChild();

        ImGui.Spacing();
        BeginCard("ImageCard", 250, "IMAGE PROCESSING");
        var scaling = (int)_state.ScalingMode;
        ImGui.SetNextItemWidth(-190);
        if (ImGui.Combo("Scaling", ref scaling, "Stretch\0Fit\0Fill\0")) RunUiAction(() => _state.SetScalingMode((ScalingMode)scaling));

        var threshold = _state.ChromaThreshold;
        ImGui.SetNextItemWidth(-190);
        if (ImGui.SliderInt("Black Threshold", ref threshold, 0, 80, "%d")) _state.SetThreshold(threshold);
        var sharpness = _state.Sharpness;
        ImGui.SetNextItemWidth(-190);
        if (ImGui.SliderInt("Sharpness", ref sharpness, 0, 100, "%d%%")) _state.SetSharpness(sharpness);
        ImGui.EndChild();

        ImGui.Spacing();
        if (ImGui.Button(_state.IsRunning ? "Stop Overlay" : "Start Overlay", new Vector2(168, 42))) RunUiAction(_state.ToggleRunning);
        ImGui.SameLine();
        if (ImGui.Button("Refresh Displays", new Vector2(168, 42)))
        {
            RunUiAction(_state.RefreshMonitors);
            _notification ??= "Display topology refreshed.";
        }
        ImGui.SameLine();
        ImGui.TextDisabled(_state.StatusText);
    }

    private void DrawPerformanceTab()
    {
        RefreshPerformanceSnapshot();
        var p = _performance;
        var recent = p?.TenSeconds ?? WindowPerformance.Empty;

        ImGui.Spacing();
        BeginCard("LatencyCard", 150, "APP LATENCY");
        ImGui.PushFont(_controller!.MonoFont, 0);
        ImGui.TextColored(ImGuiTheme.AccentBright, $"{Format(p?.TotalAppLatencyEstimateMs),7} ms");
        ImGui.SameLine();
        ImGui.Text($"AVG {Format(recent.DesktopToSubmit.Average)}   P95 {Format(recent.DesktopToSubmit.P95)}   P99 {Format(recent.DesktopToSubmit.P99)}");
        ImGui.PopFont();
        ImGui.TextDisabled($"Submit {recent.SubmitFps:F1} FPS  |  CPU submit {Format(recent.Pipeline.Current)} ms");
        ImGui.EndChild();

        ImGui.Spacing();
        BeginCard("TuningCard", 245, "PIPELINE");
        if (ImGui.BeginTable("TuningGrid", 2, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableNextColumn();
            var pipeline = (int)_state.PipelineMode;
            ImGui.SetNextItemWidth(-145);
            if (ImGui.Combo("Pipeline", ref pipeline, "Auto\0GPU Native\0Legacy CPU\0")) RunUiAction(() => _state.SetPipelineMode((PipelineMode)pipeline));

            var maxLatency = _state.MaximumFrameLatency;
            ImGui.SetNextItemWidth(-145);
            if (ImGui.SliderInt("Maximum Frame Latency", ref maxLatency, 1, 3, "%d")) RunUiAction(() => _state.SetMaximumFrameLatency(maxLatency));
            ImGui.TextDisabled("Present mode: Immediate");

            ImGui.TableNextColumn();
            var priority = (int)_state.CapturePriority;
            ImGui.SetNextItemWidth(-145);
            if (ImGui.Combo("Capture Priority", ref priority, "Normal\0Above Normal\0Highest\0")) RunUiAction(() => _state.SetCapturePriority((CapturePriority)priority));

            var mmcss = _state.MmcssEnabled;
            if (ImGui.Checkbox("MMCSS Games profile", ref mmcss)) RunUiAction(() => _state.SetMmcssEnabled(mmcss));

            var metrics = (int)_state.MetricsMode;
            ImGui.SetNextItemWidth(-145);
            if (ImGui.Combo("Metrics", ref metrics, "Off\0Lightweight\0Detailed GPU\0")) RunUiAction(() => _state.SetMetricsMode((MetricsMode)metrics));
            ImGui.EndTable();
        }
        ImGui.EndChild();

        ImGui.Spacing();
        BeginCard("MetricsCard", 390, "METRICS");
        if (ImGui.BeginTable("StageMetrics", 3, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Stage");
            ImGui.TableSetupColumn("Current");
            ImGui.TableSetupColumn("10 s average");
            ImGui.TableHeadersRow();
            DrawMetricRow("App pipeline", recent.Pipeline);
            DrawMetricRow("Desktop -> submit", recent.DesktopToSubmit);
            DrawMetricRow("Frame age", recent.FrameAge);
            DrawMetricRow("Acquire wait", recent.AcquireWait);
            DrawMetricRow("CPU copy", recent.CpuCopy);
            DrawMetricRow("UI queue", recent.UiQueue);
            DrawMetricRow("GPU copy", recent.GpuCopy);
            DrawMetricRow("GPU shader / total", recent.GpuShader, recent.GpuTotal);
            ImGui.EndTable();
        }
        ImGui.TextDisabled($"Queue {p?.QueueLength ?? 0}/{p?.MaxQueueLength ?? 0}  |  dropped {p?.DroppedFrames ?? 0}  |  replaced {p?.ReplacedFrames ?? 0}  |  DXGI errors {p?.DxgiErrors ?? 0}");
        ImGui.EndChild();

        ImGui.Spacing();
        var hud = (int)_state.PerformanceHud;
        ImGui.SetNextItemWidth(170);
        if (ImGui.Combo("Latency HUD", ref hud, "Off\0Basic\0Detailed\0")) RunUiAction(() => _state.SetHudMode((PerformanceHudMode)hud));
        ImGui.SameLine();
        var csv = _state.CsvEnabled;
        if (ImGui.Checkbox("CSV", ref csv)) RunUiAction(() => _state.SetCsvEnabled(csv));
        ImGui.SameLine();
        var csvInterval = _state.CsvIntervalFrames;
        ImGui.SetNextItemWidth(130);
        if (ImGui.SliderInt("CSV interval", ref csvInterval, 1, 240, "%d frames")) RunUiAction(() => _state.SetCsvIntervalFrames(csvInterval));
        ImGui.SameLine();
        var latencyTest = _state.LatencyTestMode;
        if (ImGui.Checkbox("Latency test", ref latencyTest)) RunUiAction(() => _state.SetLatencyTestMode(latencyTest));
    }

    private void DrawSettingsTab()
    {
        ImGui.Spacing();
        BeginCard("WindowCard", 120, "WINDOW");
        var topMost = _state.AlwaysOnTop;
        if (ImGui.Checkbox("Always On Top", ref topMost))
        {
            _state.SetAlwaysOnTop(topMost);
            TopMost = topMost;
            _notification = topMost ? "Control UI pinned above other apps." : "Control UI uses normal z-order.";
        }
        ImGui.EndChild();

        ImGui.Spacing();
        BeginCard("HotkeyCard", 190, "UI HOTKEY");
        ImGui.Text("UI Hotkey");
        ImGui.SameLine();
        var buttonText = _bindingHotkey ? "Press a key..." : $"[ {_state.UiHotkey.DisplayText} ]";
        if (ImGui.Button(buttonText, new Vector2(220, 38)))
        {
            _bindingHotkey = true;
            _notification = "Press a key combination; Escape cancels.";
        }
        if (!string.IsNullOrWhiteSpace(_hotkeys?.LastError)) ImGui.TextColored(ImGuiTheme.Warning, _hotkeys.LastError);
        ImGui.EndChild();

        ImGui.Spacing();
        BeginCard("ShortcutsCard", 170, "SHORTCUTS");
        ImGui.PushFont(_controller!.MonoFont, 0);
        ImGui.Text("Space        Start / stop overlay");
        ImGui.Text("Global +/-   Adjust black threshold by one");
        ImGui.Text($"{_state.UiHotkey.DisplayText,-12} Hide / show this control surface");
        ImGui.PopFont();
        ImGui.EndChild();
    }

    private void BeginCard(string id, float height, string title)
    {
        ImGui.BeginChild(
            id,
            new Vector2(0, height * _uiScale),
            ImGuiChildFlags.Borders,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.TextColored(ImGuiTheme.Accent, title);
        ImGui.Separator();
    }

    private void DrawMetricRow(string label, TimingStats stats, TimingStats? secondary = null)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.Text(label);
        ImGui.TableNextColumn();
        ImGui.PushFont(_controller!.MonoFont, 0);
        ImGui.Text(secondary is null ? $"{Format(stats.Current)} ms" : $"{Format(stats.Current)} / {Format(secondary.Current)} ms");
        ImGui.PopFont();
        ImGui.TableNextColumn();
        ImGui.PushFont(_controller.MonoFont, 0);
        ImGui.Text(secondary is null ? $"{Format(stats.Average)} ms" : $"{Format(stats.Average)} / {Format(secondary.Average)} ms");
        ImGui.PopFont();
    }

    private void DrawFooter()
    {
        var height = ImGui.GetFrameHeightWithSpacing() + 2;
        ImGui.SetCursorPosY(Math.Max(ImGui.GetCursorPosY(), ImGui.GetWindowHeight() - height - 10));
        ImGui.Separator();
        if (!string.IsNullOrWhiteSpace(_notification)) ImGui.TextDisabled(_notification);
    }

    private void RefreshPerformanceSnapshot()
    {
        var now = Stopwatch.GetTimestamp();
        if (now < _nextPerformanceRefresh) return;
        _performance = LatencyMetrics.Global.Snapshot();
        _nextPerformanceRefresh = now + Stopwatch.Frequency / 4;
    }

    private void ToggleSettingsVisibility()
    {
        if (Visible)
        {
            Hide();
            return;
        }

        Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
        NativeMethods.SetForegroundWindow(Handle);
    }

    private void RequestClientHeight(int height)
    {
        height = (int)Math.Round(height * _uiScale);
        if (_requestedClientHeight == height || !IsHandleCreated) return;
        _requestedClientHeight = height;
        BeginInvoke(new Action(() =>
        {
            if (IsDisposed || ClientSize.Height == height) return;
            var center = new Point(Left + Width / 2, Top + Height / 2);
            ClientSize = new Size(ClientSize.Width, height);
            var workingArea = Screen.FromHandle(Handle).WorkingArea;
            Left = Math.Clamp(center.X - Width / 2, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - Width));
            Top = Math.Clamp(center.Y - Height / 2, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - Height));
        }));
    }

    private int GetThresholdHotkeyStep(int hotkeyId)
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastThresholdHotkeyId != hotkeyId || _lastThresholdRepeat == 0 ||
            now - _lastThresholdRepeat > Stopwatch.Frequency / 5)
        {
            _thresholdHoldStarted = now;
        }

        _lastThresholdHotkeyId = hotkeyId;
        _lastThresholdRepeat = now;
        var heldMs = (now - _thresholdHoldStarted) * 1000.0 / Stopwatch.Frequency;
        return heldMs switch
        {
            < 600 => 1,
            < 1200 => 2,
            < 2000 => 4,
            _ => 8
        };
    }

    private void RunUiAction(Action action)
    {
        try
        {
            action();
            _notification = null;
        }
        catch (Exception ex)
        {
            _notification = ex.Message;
        }
    }

    private static string Format(double? value) => value is null || double.IsNaN(value.Value) ? "N/A" : value.Value.ToString("F2");

    private static uint GetHotkeyModifiers(KeyEventArgs e)
    {
        var modifiers = 0u;
        if (e.Control) modifiers |= NativeMethods.ModControl;
        if (e.Shift) modifiers |= NativeMethods.ModShift;
        if (e.Alt) modifiers |= NativeMethods.ModAlt;
        return modifiers;
    }
}
