using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace Overlay;

internal sealed class AppState : IDisposable
{
    private readonly DuplicationCapture _capture = new();
    private readonly OverlayHost _overlayHost = new();
    private readonly AppSettings _settings;
    private int _recoveryQueued;
    private bool _disposed;

    public AppState()
    {
        _settings = SettingsStore.Load();
        Monitors = MonitorInfo.Enumerate();
        if (Monitors.Count == 0) throw new InvalidOperationException("No active DXGI outputs detected.");
        SelectedInputIndex = Resolve(_settings.CaptureDisplay, capture: true);
        SelectedOutputIndex = Resolve(_settings.OutputDisplay, capture: false);
        ChromaThreshold = Math.Clamp(_settings.ChromaThreshold, 0, 80);
        Sharpness = Math.Clamp(_settings.Sharpness, 0, 100);
        ScalingMode = _settings.ScalingMode;
        var savedHotkey = new HotkeyBinding((Keys)_settings.UiHotkeyKey, _settings.UiHotkeyModifiers);
        UiHotkey = savedHotkey.Key is Keys.None or Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin
            ? new HotkeyBinding(Keys.F8, 0)
            : savedHotkey;
        _capture.FrameReady += HandleFrameReady;
        _capture.DiagnosticsChanged += diagnostics => { Diagnostics = diagnostics; StateChanged?.Invoke(); };
        _capture.RecreateRequested += HandleCaptureRecreate;
        UpdateOutputMonitor();
        Save();
    }

    public event Action? StateChanged;
    public IReadOnlyList<MonitorInfo> Monitors { get; private set; }
    public int SelectedInputIndex { get; private set; }
    public int SelectedOutputIndex { get; private set; }
    public int ChromaThreshold { get; private set; }
    public int Sharpness { get; private set; }
    public ScalingMode ScalingMode { get; private set; }
    public PipelineMode PipelineMode => _settings.PipelineMode;
    public PerformanceHudMode PerformanceHud => _settings.PerformanceHud;
    public MetricsMode MetricsMode => _settings.MetricsCollection;
    public CapturePriority CapturePriority => _settings.CaptureThreadPriority;
    public int MaximumFrameLatency => _settings.MaximumFrameLatency;
    public PresentMode PresentMode => _settings.PresentMode;
    public int CsvIntervalFrames => _settings.CsvIntervalFrames;
    public bool MmcssEnabled => _settings.EnableMmcss;
    public bool CsvEnabled => _settings.CsvEnabled;
    public bool LatencyTestMode => _settings.LatencyTestMode;
    public bool AlwaysOnTop => _settings.AlwaysOnTop;
    public HotkeyBinding UiHotkey { get; private set; }
    public bool IsRunning { get; private set; }
    public CaptureDiagnostics Diagnostics { get; private set; } = CaptureDiagnostics.Empty;
    public int CaptureFps => Diagnostics.CaptureFps;
    public string StatusText => IsRunning ? $"Running | Capture: {CurrentInputMonitor.Name} -> Output: {CurrentOutputMonitor.Name} | FPS: {CaptureFps}" : "Stopped";
    public MonitorInfo CurrentInputMonitor => Monitors[Math.Clamp(SelectedInputIndex, 0, Monitors.Count - 1)];
    public MonitorInfo CurrentOutputMonitor => Monitors[Math.Clamp(SelectedOutputIndex, 0, Monitors.Count - 1)];

    public void RefreshMonitors()
    {
        var capture = DisplaySelection.From(CurrentInputMonitor); var output = DisplaySelection.From(CurrentOutputMonitor);
        Monitors = MonitorInfo.Enumerate();
        if (Monitors.Count == 0) throw new InvalidOperationException("No active DXGI outputs detected.");
        SelectedInputIndex = Resolve(capture, true); SelectedOutputIndex = Resolve(output, false);
        UpdateOutputMonitor();
        if (IsRunning) RestartCapture("display topology refresh");
        Save(); StateChanged?.Invoke();
    }

    public void SetInputIndex(int index)
    {
        index = Math.Clamp(index, 0, Monitors.Count - 1); if (index == SelectedInputIndex) return;
        SelectedInputIndex = index; Save(); if (IsRunning) RestartCapture("capture display changed"); StateChanged?.Invoke();
    }
    public void SetOutputIndex(int index)
    {
        index = Math.Clamp(index, 0, Monitors.Count - 1); if (index == SelectedOutputIndex) return;
        SelectedOutputIndex = index; UpdateOutputMonitor(); Save(); if (IsRunning) RestartCapture("output display changed"); StateChanged?.Invoke();
    }
    public void SetThreshold(int value)
    {
        value = Math.Clamp(value, 0, 80); if (value == ChromaThreshold) return;
        ChromaThreshold = value; _capture.SetThreshold(value); Save(); StateChanged?.Invoke();
    }
    public void SetSharpness(int value)
    {
        value = Math.Clamp(value, 0, 100); if (value == Sharpness) return;
        Sharpness = value; _capture.SetSharpness(value / 100f); Save(); StateChanged?.Invoke();
    }
    public void SetScalingMode(ScalingMode value)
    {
        if (value == ScalingMode) return; ScalingMode = value; Save(); if (IsRunning) RestartCapture("scaling mode changed"); StateChanged?.Invoke();
    }
    public void SetPipelineMode(PipelineMode value)
    {
        if (value == _settings.PipelineMode) return; _settings.PipelineMode = value; Save(); if (IsRunning) RestartCapture("pipeline mode changed"); StateChanged?.Invoke();
    }
    public void SetHudMode(PerformanceHudMode value)
    {
        if (value == _settings.PerformanceHud) return; _settings.PerformanceHud = value;
        _overlayHost.SetHudMode(_settings.LatencyTestMode ? PerformanceHudMode.Detailed : value, IsRunning);
        Save(); StateChanged?.Invoke();
    }
    public void SetMetricsMode(MetricsMode value)
    {
        if (value == _settings.MetricsCollection) return; _settings.MetricsCollection = value; Save(); if (IsRunning) RestartCapture("metrics mode changed"); StateChanged?.Invoke();
    }
    public void SetCapturePriority(CapturePriority value)
    {
        if (value == _settings.CaptureThreadPriority) return; _settings.CaptureThreadPriority = value; Save(); if (IsRunning) RestartCapture("capture priority changed"); StateChanged?.Invoke();
    }
    public void SetMmcssEnabled(bool value)
    {
        if (value == _settings.EnableMmcss) return; _settings.EnableMmcss = value; Save(); if (IsRunning) RestartCapture("MMCSS changed"); StateChanged?.Invoke();
    }
    public void SetCsvEnabled(bool value)
    {
        if (value == _settings.CsvEnabled) return; _settings.CsvEnabled = value; Save(); if (IsRunning) RestartCapture("CSV changed"); StateChanged?.Invoke();
    }
    public void SetMaximumFrameLatency(int value)
    {
        value = Math.Clamp(value, 1, 3); if (value == _settings.MaximumFrameLatency) return;
        _settings.MaximumFrameLatency = value; Save(); if (IsRunning) RestartCapture("maximum frame latency changed"); StateChanged?.Invoke();
    }
    public void SetCsvIntervalFrames(int value)
    {
        value = Math.Clamp(value, 1, 240); if (value == _settings.CsvIntervalFrames) return;
        _settings.CsvIntervalFrames = value; Save(); if (IsRunning && _settings.CsvEnabled) RestartCapture("CSV interval changed"); StateChanged?.Invoke();
    }
    public void SetLatencyTestMode(bool value)
    {
        if (value == _settings.LatencyTestMode) return; _settings.LatencyTestMode = value; Save(); if (IsRunning) RestartCapture("latency test mode changed"); StateChanged?.Invoke();
    }
    public void SetAlwaysOnTop(bool value)
    {
        if (value == _settings.AlwaysOnTop) return; _settings.AlwaysOnTop = value; Save(); StateChanged?.Invoke();
    }
    public void SetUiHotkey(HotkeyBinding value)
    {
        UiHotkey = value; _settings.UiHotkeyKey = (int)value.Key; _settings.UiHotkeyModifiers = value.Modifiers; Save(); StateChanged?.Invoke();
    }
    public void CycleInput(int direction) => SetInputIndex(Wrap(SelectedInputIndex + direction));
    public void CycleOutput(int direction) => SetOutputIndex(Wrap(SelectedOutputIndex + direction));
    public void IncreaseThreshold(int delta) => SetThreshold(ChromaThreshold + delta);
    public void IncreaseSharpness(int delta) => SetSharpness(Sharpness + delta);
    public void CycleScalingMode() => SetScalingMode((ScalingMode)(((int)ScalingMode + 1) % 3));
    public void CyclePipelineMode() => SetPipelineMode((PipelineMode)(((int)_settings.PipelineMode + 1) % 3));
    public void CycleHudMode() => SetHudMode((PerformanceHudMode)(((int)_settings.PerformanceHud + 1) % 3));
    public void CycleMetricsMode() => SetMetricsMode((MetricsMode)(((int)_settings.MetricsCollection + 1) % 3));
    public void CyclePriority() => SetCapturePriority((CapturePriority)(((int)_settings.CaptureThreadPriority + 1) % 3));
    public void ToggleMmcss() => SetMmcssEnabled(!_settings.EnableMmcss);
    public void ToggleCsv() => SetCsvEnabled(!_settings.CsvEnabled);
    public void CycleMaximumFrameLatency() => SetMaximumFrameLatency(_settings.MaximumFrameLatency >= 3 ? 1 : _settings.MaximumFrameLatency + 1);
    public void CycleCsvInterval() => SetCsvIntervalFrames(_settings.CsvIntervalFrames switch { < 10 => 10, < 30 => 30, < 60 => 60, _ => 1 });
    public void ToggleLatencyTest() => SetLatencyTestMode(!_settings.LatencyTestMode);
    public void ToggleRunning() { if (IsRunning) Stop(); else Start(); }
    public void Start()
    {
        if (IsRunning) return;
        if (CurrentInputMonitor.Identity == CurrentOutputMonitor.Identity && !_settings.AllowSameDisplay)
            throw new InvalidOperationException("Capture Display and Output Display are identical. Change one selection to prevent a feedback loop.");
        UpdateOutputMonitor();
        Logger.Info($"Starting: capture={CurrentInputMonitor.Identity}; output={CurrentOutputMonitor.Identity}; different={CurrentInputMonitor.Identity != CurrentOutputMonitor.Identity}; captureSize={CurrentInputMonitor.Bounds.Size}; outputSize={CurrentOutputMonitor.Bounds.Size}; scaling={ScalingMode}");
        _overlayHost.ShowOverlay();
        _capture.Start(CurrentInputMonitor, CurrentOutputMonitor, ChromaThreshold, Sharpness / 100f, ScalingMode, _overlayHost.OverlayHandle, CaptureOptions.From(_settings));
        IsRunning = true; _overlayHost.SetHudMode(_settings.LatencyTestMode ? PerformanceHudMode.Detailed : _settings.PerformanceHud, true); StateChanged?.Invoke();
    }
    public void Stop() { if (!IsRunning) return; _capture.Stop(); _overlayHost.ClearFrame(); _overlayHost.HideOverlay(); _overlayHost.SetHudMode(_settings.PerformanceHud, false); IsRunning = false; StateChanged?.Invoke(); }
    public void Dispose() { if (_disposed) return; _disposed = true; _capture.FrameReady -= HandleFrameReady; _capture.RecreateRequested -= HandleCaptureRecreate; Stop(); _capture.Dispose(); _overlayHost.Dispose(); SettingsStore.Flush(); }

    private void HandleFrameReady(FrameEnvelope frame) => _overlayHost.SetFrame(frame);
    private void HandleCaptureRecreate(string reason)
    {
        if (!IsRunning) return;
        if (System.Threading.Interlocked.Exchange(ref _recoveryQueued, 1) != 0) return;
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            Logger.Info($"Refreshing DXGI duplication after: {reason}");
            try { System.Threading.Thread.Sleep(300); RefreshMonitors(); } catch (Exception ex) { Logger.Error($"Display recovery failed: {ex.Message}"); }
            finally { System.Threading.Volatile.Write(ref _recoveryQueued, 0); }
        });
    }
    private void RestartCapture(string reason) { Logger.Info($"Restarting duplication: {reason}"); _capture.Start(CurrentInputMonitor, CurrentOutputMonitor, ChromaThreshold, Sharpness / 100f, ScalingMode, _overlayHost.OverlayHandle, CaptureOptions.From(_settings)); }
    private void UpdateOutputMonitor() => _overlayHost.SetMonitor(CurrentOutputMonitor);
    private int Resolve(DisplaySelection? selected, bool capture)
    {
        if (selected is not null) { var exact = Monitors.Select((m, i) => (m, i)).FirstOrDefault(x => selected.Matches(x.m)); if (exact.m is not null) return exact.i; Logger.Info($"Saved {(capture ? "capture" : "output")} display was not found; using fallback."); }
        var fallback = capture ? Monitors.Select((m, i) => (m, i)).FirstOrDefault(x => !x.m.IsPrimary).i : Monitors.Select((m, i) => (m, i)).FirstOrDefault(x => x.m.IsPrimary).i;
        return fallback >= 0 && fallback < Monitors.Count ? fallback : 0;
    }
    private int Wrap(int index) => index < 0 ? Monitors.Count - 1 : index >= Monitors.Count ? 0 : index;
    private void Save() { _settings.CaptureDisplay = DisplaySelection.From(CurrentInputMonitor); _settings.OutputDisplay = DisplaySelection.From(CurrentOutputMonitor); _settings.ChromaThreshold = ChromaThreshold; _settings.Sharpness = Sharpness; _settings.ScalingMode = ScalingMode; SettingsStore.Save(_settings); }
}
