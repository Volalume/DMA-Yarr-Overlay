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
    private long _nextProtectionVerification;
    private CaptureProtectionVerification _protectionVerification = new(false, false, "CHECKING CURRENT STATE", "Waiting for the first verification.");
    private bool _bindingHotkey;
    private string? _notification;
    private long _thresholdHoldStarted;
    private long _lastThresholdRepeat;
    private int _lastThresholdHotkeyId;
    private const int ControlWidth = 1120;
    private const int ControlHeight = 700;
    private IntPtr _previousForegroundWindow;
    private bool _changingVisibility;
    private bool _disposed;
    private Icon? _customIcon;
    private PerformanceDetailsWindow? _performanceDetails;
    private bool _gpuScanoutOperationPending;
    private long _nextGpuScanoutProbe;
    private GpuScanoutProtection.ProbeResult _gpuScanoutProbe;
    private readonly string _windowClassName;
    private static readonly string[] ProtectionLevelNames = { "Off", "Software Level", "Hardware Level", "Kernel Level" };

    public SettingsWindow(AppState state)
    {
        _state = state;
        _windowClassName = state.ActiveWindowClassName;
        ApplyBranding();
        ClientSize = new Size(ControlWidth, ControlHeight);
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

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassName = _windowClassName;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            _controller = new ImGuiD3D11Controller(Handle, ClientSize.Width, ClientSize.Height, DeviceDpi / 96f);
        }
        catch (Exception ex)
        {
            Logger.Error($"ImGui initialization failed: {ex}");
            throw;
        }
        _hotkeys = new HotkeyManager(Handle);
        _hotkeys.RegisterThresholdHotkeys();
        if (!_hotkeys.TrySetUiHotkey(_state.UiHotkey)) _notification = _hotkeys.LastError;

        var cornerPreference = NativeMethods.DwmWindowCornerPreferenceRound;
        NativeMethods.DwmSetWindowAttribute(
            Handle,
            NativeMethods.DwmwaWindowCornerPreference,
            ref cornerPreference,
            sizeof(int));

        _state.RegisterSettingsWindow(Handle);

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
            ActivateForInteraction();
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
        else
        {
            _renderTimer.Stop();
            Capture = false;
            NativeMethods.ReleaseCapture();
            Cursor.Clip = Rectangle.Empty;
        }
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
        if (m.Msg == NativeMethods.WmShowSettings)
        {
            ShowSettings();
            return;
        }

        if (m.Msg == NativeMethods.WmHotkey)
        {
            var id = m.WParam.ToInt32();
            if (id == NativeMethods.HotkeyIncrease) _state.IncreaseThreshold(GetThresholdHotkeyStep(id));
            else if (id == NativeMethods.HotkeyDecrease) _state.IncreaseThreshold(-GetThresholdHotkeyStep(id));
            else if (_hotkeys?.IsUiHotkey(id) == true) ToggleSettingsVisibility();
            return;
        }

        _controller?.ProcessWindowMessage(ref m);
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            if (IsHandleCreated) _state.UnregisterSettingsWindow(Handle);
            _disposed = true;
            _renderTimer.Stop();
            _renderTimer.Tick -= RenderFrame;
            _renderTimer.Dispose();
            _customIcon?.Dispose();
            _customIcon = null;
            if (_performanceDetails is { IsDisposed: false }) _performanceDetails.Close();
            _performanceDetails = null;
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
        ImGui.Begin("Overlay##Root", flags);
        DrawHeader();

        if (ImGui.BeginTabBar("MainTabs", ImGuiTabBarFlags.FittingPolicyShrink | ImGuiTabBarFlags.DrawSelectedOverline))
        {
            if (ImGui.BeginTabItem("Overlay"))
            {
                DrawOverlayTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Performance"))
            {
                DrawPerformanceTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Settings"))
            {
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
        ImGui.SameLine();
        ImGui.TextDisabled("https://github.com/Volalume/DMA-Yarr-Overlay");
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
        BeginCard("ImageCard", 220, "IMAGE PROCESSING");
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
    }

    private void DrawPerformanceTab()
    {
        RefreshPerformanceSnapshot();
        var p = _performance;
        var recent = p?.TenSeconds ?? WindowPerformance.Empty;

        ImGui.Spacing();
        BeginCard("PerformanceCard", 470, "PERFORMANCE");

        if (ImGui.BeginTable("LatencySummary", 3, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn();
            ImGui.TextDisabled("TOTAL LATENCY");
            ImGui.PushFont(_controller!.MonoFont, 0);
            ImGui.TextColored(ImGuiTheme.AccentBright, $"{Format(p?.TotalAppLatencyEstimateMs)} ms");
            ImGui.PopFont();

            ImGui.TableNextColumn();
            ImGui.TextDisabled("10 SECOND WINDOW");
            ImGui.Text($"AVG {Format(recent.DesktopToSubmit.Average)} ms  |  P95 {Format(recent.DesktopToSubmit.P95)} ms");

            ImGui.TableNextColumn();
            ImGui.TextDisabled("OUTPUT");
            ImGui.Text($"{recent.SubmitFps:F1} FPS  |  CPU {Format(recent.Pipeline.Current)} ms");
            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.SeparatorText("TUNING");
        if (ImGui.BeginTable("TuningGrid", 2, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableNextColumn();
            if (ImGui.BeginTable("TuningLeft", 2, ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("Control", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding(); ImGui.Text("Pipeline");
                ImGui.TableNextColumn();
                var pipeline = (int)_state.PipelineMode;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.Combo("##Pipeline", ref pipeline, "Auto\0GPU Native\0Legacy CPU\0")) RunUiAction(() => _state.SetPipelineMode((PipelineMode)pipeline));

                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding(); ImGui.Text("Frame Latency");
                ImGui.TableNextColumn();
                var maxLatency = _state.MaximumFrameLatency;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.SliderInt("##MaximumFrameLatency", ref maxLatency, 1, 3, "%d")) RunUiAction(() => _state.SetMaximumFrameLatency(maxLatency));

                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding(); ImGui.Text("Latency HUD");
                ImGui.TableNextColumn();
                var hud = (int)_state.PerformanceHud;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.Combo("##LatencyHud", ref hud, "Off\0Basic\0Detailed\0")) RunUiAction(() => _state.SetHudMode((PerformanceHudMode)hud));
                ImGui.EndTable();
            }

            ImGui.TableNextColumn();
            if (ImGui.BeginTable("TuningRight", 2, ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("Control", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding(); ImGui.Text("Priority");
                ImGui.TableNextColumn();
                var priority = (int)_state.CapturePriority;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.Combo("##CapturePriority", ref priority, "Normal\0Above Normal\0Highest\0")) RunUiAction(() => _state.SetCapturePriority((CapturePriority)priority));

                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding(); ImGui.Text("MMCSS");
                ImGui.TableNextColumn();
                var mmcss = _state.MmcssEnabled;
                if (ImGui.Checkbox("Games profile", ref mmcss)) RunUiAction(() => _state.SetMmcssEnabled(mmcss));

                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding(); ImGui.Text("Present");
                ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding(); ImGui.TextDisabled("Immediate");
                ImGui.EndTable();
            }
            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.SeparatorText("TOOLS");
        if (ImGui.BeginTable("ToolsGrid", 2, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableNextColumn();
            var showDetails = _performanceDetails is { IsDisposed: false, Visible: true };
            if (ImGui.Checkbox("Detailed Metrics Window", ref showDetails)) SetPerformanceDetailsVisible(showDetails);
            var csv = _state.CsvEnabled;
            if (ImGui.Checkbox("CSV logging", ref csv)) RunUiAction(() => _state.SetCsvEnabled(csv));
            var latencyTest = _state.LatencyTestMode;
            if (ImGui.Checkbox("Latency test mode", ref latencyTest)) RunUiAction(() => _state.SetLatencyTestMode(latencyTest));

            ImGui.TableNextColumn();
            if (ImGui.BeginTable("ToolsRight", 2, ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("Control", ImGuiTableColumnFlags.WidthStretch);

                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding(); ImGui.Text("Collection");
                ImGui.TableNextColumn();
                var metrics = (int)_state.MetricsMode;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.Combo("##MetricsCollection", ref metrics, "Off\0Lightweight\0Detailed GPU\0")) RunUiAction(() => _state.SetMetricsMode((MetricsMode)metrics));

                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding(); ImGui.Text("CSV interval");
                ImGui.TableNextColumn();
                var csvInterval = _state.CsvIntervalFrames;
                ImGui.BeginDisabled(!csv);
                ImGui.SetNextItemWidth(-1);
                if (ImGui.SliderInt("##CsvWriteInterval", ref csvInterval, 1, 240, "%d frames")) RunUiAction(() => _state.SetCsvIntervalFrames(csvInterval));
                ImGui.EndDisabled();

                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding(); ImGui.Text("Health");
                ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding();
                ImGui.TextDisabled($"Q {p?.QueueLength ?? 0}/{p?.MaxQueueLength ?? 0} | Drop {p?.DroppedFrames ?? 0} | DXGI {p?.DxgiErrors ?? 0}");
                ImGui.EndTable();
            }
            ImGui.EndTable();
        }
        ImGui.EndChild();
    }

    private void SetPerformanceDetailsVisible(bool visible)
    {
        if (!visible)
        {
            _performanceDetails?.Close();
            return;
        }

        if (_performanceDetails is null || _performanceDetails.IsDisposed)
        {
            var details = new PerformanceDetailsWindow(_state)
            {
                StartPosition = FormStartPosition.CenterParent,
                TopMost = _state.AlwaysOnTop
            };
            details.FormClosed += (_, _) =>
            {
                if (ReferenceEquals(_performanceDetails, details)) _performanceDetails = null;
            };
            _performanceDetails = details;
        }

        _performanceDetails.Show(this);
        _performanceDetails.Activate();
    }

    private void DrawSettingsTab()
    {
        // Leave a dedicated footer lane below the tab instead of allowing this
        // table to push the persistent status text outside the client area.
        const float settingsContentHeight = 460f;
        ImGui.Spacing();
        if (ImGui.BeginTable("SettingsColumns", 2, ImGuiTableFlags.SizingStretchProp))
        {
        ImGui.TableSetupColumn("Protection", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("General", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        DrawAntiCaptureSettings(settingsContentHeight);
        ImGui.TableNextColumn();
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
        BeginCard("HotkeyCard", 120, "UI HOTKEY");
        ImGui.Text("UI Hotkey");
        ImGui.SameLine();
        var buttonText = _bindingHotkey ? "Press a key..." : $"[ {_state.UiHotkey.DisplayText} ]";
        if (ImGui.Button(buttonText, new Vector2(80, 38)))
        {
            _bindingHotkey = true;
            _notification = "Press a key; Escape cancels.";
        }
        if (!string.IsNullOrWhiteSpace(_hotkeys?.LastError)) ImGui.TextColored(ImGuiTheme.Warning, _hotkeys.LastError);
        ImGui.EndChild();

        ImGui.Spacing();
        BeginCard("ShortcutsCard", 150, "SHORTCUTS");
        ImGui.PushFont(_controller!.MonoFont, 0);
        ImGui.TextWrapped("Space        Start / stop overlay");
        ImGui.TextWrapped("Global +/-   Adjust black threshold by one");
        ImGui.TextWrapped($"{_state.UiHotkey.DisplayText,-12} Hide / show this control surface");
        ImGui.PopFont();
        ImGui.EndChild();
        ImGui.EndTable();
        }
    }

    private void DrawAntiCaptureSettings(float height)
    {
        BeginCard("AntiCaptureCard", height, "PROTECTION");
        ImGui.SeparatorText("ANTI-CAPTURE");
        var level = _state.ProtectionLevel;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##ProtectionLevel", ProtectionLevelNames[(int)level]))
        {
            for (var i = 0; i < ProtectionLevelNames.Length; i++)
            {
                var candidate = (CaptureProtectionLevel)i;
                if (ImGui.Selectable(ProtectionLevelNames[i], candidate == level))
                    RunProtectionUiAction(() => _state.SetProtectionLevel(candidate));
                if (candidate == level) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        level = _state.ProtectionLevel;
        if (level == CaptureProtectionLevel.Software)
        {
            var appearance = _state.AntiCapture == AntiCaptureMode.Black ? 0 : 1;
            ImGui.SetNextItemWidth(180);
            if (ImGui.Combo("Capture result", ref appearance, "Black\0Exclude\0"))
                RunProtectionUiAction(() => _state.SetAntiCaptureMode(appearance == 0 ? AntiCaptureMode.Black : AntiCaptureMode.Exclude));
        }
        var scanoutProtection = _state.GpuScanoutCaptureProtection;
        ImGui.BeginDisabled(_gpuScanoutOperationPending);
        if (ImGui.Checkbox("GPU Scanout Capture Protection (VGK)", ref scanoutProtection))
        {
            var requestedState = scanoutProtection;
            BeginInvoke(new Action(() => RequestGpuScanoutProtection(requestedState)));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Blocks hardware capture at the GPU scanout buffer level through the GPU driver.\n\nWhile this option is enabled the related driver file under Win32 is safely backed up and temporarily removed.\n\nEven if you delete the backup theres no need to worry since the file will be restored after a reboot or by reinstalling the GPU driver.");
        ImGui.EndDisabled();
        if (!_gpuScanoutOperationPending)
            RefreshGpuScanoutProbe();
        DrawGpuScanoutStatus();
        RefreshProtectionVerification();
        var verificationColor = _protectionVerification.Failed ? ImGuiTheme.Warning
            : _protectionVerification.Verified ? ImGuiTheme.Good : ImGuiTheme.Accent;
        ImGui.PushTextWrapPos(0);
        ImGui.TextColored(verificationColor, $"● {_protectionVerification.Label}");
        ImGui.PopTextWrapPos();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(_protectionVerification.Detail);

        if (!string.IsNullOrWhiteSpace(_protectionVerification.AffinityLabel))
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextDisabled(_protectionVerification.AffinityLabel);
            ImGui.PopTextWrapPos();
        }

        if (level == CaptureProtectionLevel.Kernel)
        {
            var driverLoaded = _state.KernelDriverLoaded;
            ImGui.TextColored(driverLoaded ? ImGuiTheme.Good : ImGuiTheme.Warning,
                driverLoaded ? "● Driver is Loaded" : "● Driver is Not Loaded");
        }

        var status = _state.GpuOutputStatus;
        if (level == CaptureProtectionLevel.Hardware && status.Blocked && ImGui.Button("Retry")) RunProtectionUiAction(_state.Start);

        ImGui.Spacing();
        ImGui.SeparatorText("APPLICATION DATA");
        ImGui.TextDisabled($"Name: {_state.AppDisplayName}");
        ImGui.TextDisabled($"Title: {_state.WindowTitle}");
        ImGui.TextDisabled($"Class base: {_state.WindowClassName}{(_state.WindowClassName == _state.ActiveWindowClassName ? "" : " (restart pending)")}");
        ImGui.TextDisabled($"Icon: {(string.IsNullOrWhiteSpace(_state.IconPath) ? "Default" : System.IO.Path.GetFileName(_state.IconPath))}");
        if (ImGui.Button("Edit Data", new Vector2(132, 34))) BeginInvoke(new Action(EditBranding));

        ImGui.EndChild();
    }

    private void BeginCard(string id, float height, string title, bool scrollable = false)
    {
        ImGui.BeginChild(
            id,
            new Vector2(0, height),
            ImGuiChildFlags.Borders,
            scrollable ? ImGuiWindowFlags.AlwaysVerticalScrollbar
                : ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.TextColored(ImGuiTheme.Accent, title);
        ImGui.Separator();
    }

    private void RefreshProtectionVerification()
    {
        var now = Stopwatch.GetTimestamp();
        if (now < _nextProtectionVerification) return;
        _nextProtectionVerification = now + Stopwatch.Frequency / 2;
        try { _protectionVerification = _state.InspectCaptureProtection(); }
        catch (Exception ex) { _protectionVerification = new(false, true, "VERIFICATION FAILED", ex.Message); }
    }

    private void RefreshGpuScanoutProbe()
    {
        var now = Stopwatch.GetTimestamp();
        if (now < _nextGpuScanoutProbe) return;
        _nextGpuScanoutProbe = now + Stopwatch.Frequency * 4;
        _gpuScanoutProbe = GpuScanoutProtection.ProbeLoadLibrary();
    }

    private void DrawGpuScanoutStatus()
    {
        if (_gpuScanoutOperationPending)
        {
            ImGui.TextColored(ImGuiTheme.Accent, "● NVAPI CHANGE IN PROGRESS");
            return;
        }
        if (!_gpuScanoutProbe.FilePresent && !_gpuScanoutProbe.Loadable)
        {
            ImGui.TextColored(ImGuiTheme.Good, "● NVAPI NOT LOADED · SCANOUT PATH BLOCKED");
            return;
        }
        if (!_gpuScanoutProbe.FilePresent)
        {
            ImGui.TextColored(ImGuiTheme.Warning, "● NVAPI STILL LOADED · RESTART APPS");
            return;
        }
        if (_gpuScanoutProbe.Loadable)
        {
            ImGui.TextColored(ImGuiTheme.Warning, "● NVAPI LOADED · CAPTURABLE");
            return;
        }
        ImGui.TextColored(ImGuiTheme.Warning, $"● NVAPI PRESENT · LOAD FAILED ({_gpuScanoutProbe.ErrorCode})");
    }

    private void RequestGpuScanoutProtection(bool enabled)
    {
        if (_gpuScanoutOperationPending) return;
        if (enabled)
        {
            var result = MessageBox.Show(
                this,
                "This option temporarily backs up and removes NVIDIA's nvapi64.dll from System32. It can interrupt NVIDIA-dependent applications. Continue?",
                "GPU Scanout Capture Protection",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (result != DialogResult.Yes) return;
        }

        _gpuScanoutOperationPending = true;
        _notification = enabled ? "Removing nvapi64.dll..." : "Restoring nvapi64.dll...";
        _ = Task.Run(() =>
        {
            Exception? failure = null;
            try { _state.SetGpuScanoutCaptureProtection(enabled); }
            catch (Exception ex) { failure = ex; }
            try
            {
                BeginInvoke(new Action(() =>
                {
                    _gpuScanoutOperationPending = false;
                    _nextGpuScanoutProbe = 0;
                    _notification = failure is null
                        ? enabled ? "GPU scanout capture protection enabled." : "GPU scanout capture protection disabled."
                        : failure.Message;
                    if (failure is not null)
                        MessageBox.Show(
                            this,
                            failure.Message,
                            "GPU Scanout Capture Protection",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                }));
            }
            catch { }
        });
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
        // Keep enough room for both the optional notification and the persistent
        // status line. Reserving only one line pushed the status below the window.
        const int reservedLines = 2;
        var height = ImGui.GetTextLineHeightWithSpacing() * reservedLines + ImGui.GetStyle().ItemSpacing.Y + 2;
        ImGui.SetCursorPosY(ImGui.GetWindowHeight() - height - 10);
        ImGui.Separator();
        if (!string.IsNullOrWhiteSpace(_notification)) ImGui.TextDisabled(_notification);
        ImGui.TextDisabled(_state.StatusText);
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
        if (_changingVisibility) return;
        if (Visible) HideSettings();
        else ShowSettings();
    }

    private void HideSettings()
    {
        _changingVisibility = true;
        try
        {
            Capture = false;
            NativeMethods.ReleaseCapture();
            Cursor.Clip = Rectangle.Empty;
            Hide();
            if (_previousForegroundWindow != IntPtr.Zero && NativeMethods.IsWindow(_previousForegroundWindow))
                NativeMethods.SetForegroundWindow(_previousForegroundWindow);
        }
        finally
        {
            _changingVisibility = false;
        }
    }

    private void ShowSettings()
    {
        if (_changingVisibility) return;
        _changingVisibility = true;
        try
        {
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground != IntPtr.Zero && foreground != Handle) _previousForegroundWindow = foreground;
            Cursor.Clip = Rectangle.Empty;
            Show();
            WindowState = FormWindowState.Normal;
            TopMost = _state.AlwaysOnTop;
            ActivateForInteraction();
        }
        finally
        {
            _changingVisibility = false;
        }
    }

    private void ActivateForInteraction()
    {
        if (!Visible || !IsHandleCreated) return;

        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground != IntPtr.Zero && foreground != Handle) _previousForegroundWindow = foreground;
        var foregroundThread = foreground == IntPtr.Zero
            ? 0u
            : NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var currentThread = NativeMethods.GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != currentThread &&
                       NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);

        try
        {
            Cursor.Clip = Rectangle.Empty;
            NativeMethods.SetWindowPos(
                Handle,
                new IntPtr(_state.AlwaysOnTop ? NativeMethods.HwndTopmost : NativeMethods.HwndTop),
                0,
                0,
                0,
                0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpShowWindow);
            NativeMethods.BringWindowToTop(Handle);
            NativeMethods.SetForegroundWindow(Handle);
            NativeMethods.SetFocus(Handle);
            Activate();
            Focus();
        }
        finally
        {
            if (attached) NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
        }
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

    private void EditBranding()
    {
        using var dialog = new BrandingDialog(_state.AppDisplayName, _state.WindowTitle, _state.WindowClassName, _state.IconPath);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var restartRequired = !string.Equals(dialog.WindowClassName.Trim(), _state.ActiveWindowClassName, StringComparison.Ordinal);
        try
        {
            _state.SetBranding(dialog.AppDisplayName, dialog.WindowTitle, dialog.WindowClassName, dialog.IconPath);
            ApplyBranding();
            _notification = restartRequired ? "Window class saved; restart pending." : "Branding updated.";
        }
        catch (Exception ex)
        {
            _notification = ex.Message;
            return;
        }
        if (restartRequired)
        {
            using var restart = new RestartRequiredDialog(_state.WindowClassName);
            if (restart.ShowDialog(this) == DialogResult.OK) RestartApplication();
        }
    }

    private void ApplyBranding()
    {
        Text = _state.WindowTitle;
        _customIcon?.Dispose();
        _customIcon = BrandingIcon.TryLoad(_state.IconPath);
        Icon = _customIcon;
    }

    private void RestartApplication()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Current executable path is unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = true };
        start.ArgumentList.Add("--restart-wait");
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase)))
            start.ArgumentList.Add("--autostart");
        Process.Start(start);
        BeginInvoke(new Action(Close));
    }

    private void RunProtectionUiAction(Action action)
    {
        RunUiAction(action);
        _nextProtectionVerification = 0;
        RefreshProtectionVerification();
    }

    private static string Format(double? value) => LatencyFormatter.Milliseconds(value);

    private static uint GetHotkeyModifiers(KeyEventArgs e)
    {
        var modifiers = 0u;
        if (e.Control) modifiers |= NativeMethods.ModControl;
        if (e.Shift) modifiers |= NativeMethods.ModShift;
        if (e.Alt) modifiers |= NativeMethods.ModAlt;
        return modifiers;
    }
}
