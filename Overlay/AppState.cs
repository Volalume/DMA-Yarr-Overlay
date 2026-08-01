using System;
using System.Collections.Generic;
using System.Linq;

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

    public void CycleInput(int direction) { SelectedInputIndex = Wrap(SelectedInputIndex + direction); Save(); if (IsRunning) RestartCapture("capture display changed"); StateChanged?.Invoke(); }
    public void CycleOutput(int direction) { SelectedOutputIndex = Wrap(SelectedOutputIndex + direction); UpdateOutputMonitor(); Save(); if (IsRunning) RestartCapture("output display changed"); StateChanged?.Invoke(); }
    public void IncreaseThreshold(int delta) { ChromaThreshold = Math.Clamp(ChromaThreshold + delta, 0, 80); _capture.SetThreshold(ChromaThreshold); Save(); StateChanged?.Invoke(); }
    public void IncreaseSharpness(int delta) { Sharpness = Math.Clamp(Sharpness + delta, 0, 100); _capture.SetSharpness(Sharpness / 100f); Save(); StateChanged?.Invoke(); }
    public void CycleScalingMode() { ScalingMode = (ScalingMode)(((int)ScalingMode + 1) % 3); Save(); if (IsRunning) RestartCapture("scaling mode changed"); StateChanged?.Invoke(); }
    public void CyclePipelineMode() { _settings.PipelineMode = (PipelineMode)(((int)_settings.PipelineMode + 1) % 3); Save(); if (IsRunning) RestartCapture("pipeline mode changed"); StateChanged?.Invoke(); }
    public void CycleHudMode() { _settings.PerformanceHud = (PerformanceHudMode)(((int)_settings.PerformanceHud + 1) % 3); _overlayHost.SetHudMode(_settings.PerformanceHud, IsRunning); Save(); StateChanged?.Invoke(); }
    public void CycleMetricsMode() { _settings.MetricsCollection = (MetricsMode)(((int)_settings.MetricsCollection + 1) % 3); Save(); if (IsRunning) RestartCapture("metrics mode changed"); StateChanged?.Invoke(); }
    public void CyclePriority() { _settings.CaptureThreadPriority = (CapturePriority)(((int)_settings.CaptureThreadPriority + 1) % 3); Save(); if (IsRunning) RestartCapture("capture priority changed"); StateChanged?.Invoke(); }
    public void ToggleMmcss() { _settings.EnableMmcss = !_settings.EnableMmcss; Save(); if (IsRunning) RestartCapture("MMCSS changed"); StateChanged?.Invoke(); }
    public void ToggleCsv() { _settings.CsvEnabled = !_settings.CsvEnabled; Save(); if (IsRunning) RestartCapture("CSV changed"); StateChanged?.Invoke(); }
    public void CycleMaximumFrameLatency() { _settings.MaximumFrameLatency = _settings.MaximumFrameLatency >= 3 ? 1 : _settings.MaximumFrameLatency + 1; Save(); if (IsRunning) RestartCapture("maximum frame latency changed"); StateChanged?.Invoke(); }
    public void CycleCsvInterval() { _settings.CsvIntervalFrames = _settings.CsvIntervalFrames switch { < 10 => 10, < 30 => 30, < 60 => 60, _ => 1 }; Save(); if (IsRunning && _settings.CsvEnabled) RestartCapture("CSV interval changed"); StateChanged?.Invoke(); }
    public void ToggleLatencyTest() { _settings.LatencyTestMode = !_settings.LatencyTestMode; Save(); if (IsRunning) RestartCapture("latency test mode changed"); StateChanged?.Invoke(); }
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
    public void Dispose() { if (_disposed) return; _disposed = true; _capture.FrameReady -= HandleFrameReady; _capture.RecreateRequested -= HandleCaptureRecreate; Stop(); _capture.Dispose(); _overlayHost.Dispose(); }

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
