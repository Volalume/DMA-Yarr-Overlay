using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace Overlay;

internal sealed class AppState : IDisposable
{
    private readonly DuplicationCapture _capture = new();
    private readonly OverlayHost _overlayHost;
    private readonly KernelCaptureProtection _kernelProtection = new();
    private readonly AppSettings _settings;
    private IntPtr _settingsWindowHandle;
    private string? _lastKernelAffinityLog;
    private int _recoveryQueued;
    private bool _disposed;

    public AppState()
    {
        _settings = SettingsStore.Load();
        NormalizeBranding();
        ActiveWindowClassName = _settings.WindowClassName;
        _overlayHost = new OverlayHost(ActiveWindowClassName);
        if (!Enum.IsDefined(_settings.AntiCapture)) _settings.AntiCapture = AntiCaptureMode.Off;
        if (!Enum.IsDefined(_settings.ProtectionLevel)) _settings.ProtectionLevel = CaptureProtectionLevel.Off;
        // Migrate settings written before the unified protection level was persisted.
        if (_settings.ProtectionLevel == CaptureProtectionLevel.Off)
        {
            if (_settings.GpuProtection != GpuProtectionMode.Off)
                _settings.ProtectionLevel = CaptureProtectionLevel.Hardware;
            else if (_settings.AntiCapture != AntiCaptureMode.Off)
                _settings.ProtectionLevel = CaptureProtectionLevel.Software;
        }
        // Unknown nonzero protection settings stay fail-closed in the capture worker.
        ApplyEffectiveCaptureProtection();
        _overlayHost.SetBranding(_settings.AppDisplayName, _settings.WindowTitle, _settings.IconPath);
        Monitors = MonitorInfo.Enumerate();
        if (Monitors.Count == 0) throw new InvalidOperationException("No active DXGI outputs detected.");
        SelectedInputIndex = Resolve(_settings.CaptureDisplay, capture: true);
        SelectedOutputIndex = Resolve(_settings.OutputDisplay, capture: false);
        ChromaThreshold = Math.Clamp(_settings.ChromaThreshold, 0, 80);
        Sharpness = Math.Clamp(_settings.Sharpness, 0, 100);
        ScalingMode = _settings.ScalingMode;
        var savedHotkey = new HotkeyBinding((Keys)_settings.UiHotkeyKey, _settings.UiHotkeyModifiers);
        UiHotkey = savedHotkey.Key is Keys.None or Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin
            ? new HotkeyBinding(Keys.Insert, 0)
            : savedHotkey;
        _capture.FrameReady += HandleFrameReady;
        _capture.DiagnosticsChanged += diagnostics => { Diagnostics = diagnostics; StateChanged?.Invoke(); };
        _capture.RecreateRequested += HandleCaptureRecreate;
        _capture.ProtectedOutputBlocked += HandleProtectedOutputBlocked;
        UpdateOutputMonitor();
        _overlayHost.SetHudMode(_settings.PerformanceHud, false);
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
    public string AppDisplayName => _settings.AppDisplayName;
    public string WindowTitle => _settings.WindowTitle;
    public string WindowClassName => _settings.WindowClassName;
    public string ActiveWindowClassName { get; }
    public string IconPath => _settings.IconPath;
    public AntiCaptureMode AntiCapture => _settings.AntiCapture;
    public GpuProtectionMode GpuProtection => _settings.GpuProtection;
    public CaptureProtectionLevel ProtectionLevel => _settings.ProtectionLevel;
    public GpuProtectionStatus GpuOutputStatus => _settings.GpuProtection == GpuProtectionMode.Off
        ? GpuProtectionStatus.Off
        : _capture.ProtectionStatus.Blocked || IsRunning ? _capture.ProtectionStatus
        : new(false, false, "Not started", "Hardware support is checked when you start the overlay.");
    public string OverlayCaptureStatus => _overlayHost.OverlayCaptureStatus;
    public string HudCaptureStatus => _overlayHost.HudCaptureStatus;
    public bool KernelDriverLoaded => _kernelProtection.DriverLoaded;
    public HotkeyBinding UiHotkey { get; private set; }
    private bool _running;
    public bool IsRunning => _running && !_capture.ProtectionStatus.Blocked;
    public CaptureDiagnostics Diagnostics { get; private set; } = CaptureDiagnostics.Empty;
    public int CaptureFps => Diagnostics.CaptureFps;
    public string StatusText => _capture.ProtectionStatus.Blocked ? "Output blocked: GPU protection failed. See Settings > Anti-Capture."
        : IsRunning ? $"Running | Capture: {CurrentInputMonitor.Name} -> Output: {CurrentOutputMonitor.Name} | FPS: {CaptureFps}" : "Stopped";
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
    public void SetBranding(string appDisplayName, string windowTitle, string windowClassName, string iconPath)
    {
        _settings.AppDisplayName = NormalizeLabel(appDisplayName, "Overlay", 48);
        _settings.WindowTitle = NormalizeLabel(windowTitle, $"{_settings.AppDisplayName} Control", 96);
        _settings.WindowClassName = NormalizeWindowClassName(windowClassName);
        iconPath = (iconPath ?? "").Trim();
        if (iconPath.Length > 0)
        {
            if (!System.IO.File.Exists(iconPath)) throw new System.IO.FileNotFoundException("ICO file not found.", iconPath);
            if (!string.Equals(System.IO.Path.GetExtension(iconPath), ".ico", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The app icon must be an .ico file.");
            using var testIcon = new System.Drawing.Icon(iconPath);
        }
        _settings.IconPath = iconPath;
        _overlayHost.SetBranding(_settings.AppDisplayName, _settings.WindowTitle, _settings.IconPath);
        Save(immediate: true);
        StateChanged?.Invoke();
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
    public void RegisterSettingsWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !NativeMethods.IsWindow(handle)) return;
        _settingsWindowHandle = handle;
        if (ProtectionLevel == CaptureProtectionLevel.Kernel)
            ApplyEffectiveCaptureProtection();
        StateChanged?.Invoke();
    }
    public void UnregisterSettingsWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero || handle != _settingsWindowHandle) return;
        if (ProtectionLevel == CaptureProtectionLevel.Kernel)
        {
            try { _kernelProtection.Disable(); }
            catch { }
        }
        _settingsWindowHandle = IntPtr.Zero;
        StateChanged?.Invoke();
    }
    public void SetAntiCaptureMode(AntiCaptureMode value)
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
        if (value == _settings.AntiCapture && ProtectionLevel == CaptureProtectionLevel.Software) return;
        var restart = IsRunning;
        Logger.Info($"Protection change requested: softwareResult={value}; previousLevel={ProtectionLevel}; previousSoftwareResult={_settings.AntiCapture}; running={restart}");
        Stop();
        _settings.AntiCapture = value;
        _settings.ProtectionLevel = value == AntiCaptureMode.Off
            ? CaptureProtectionLevel.Off
            : CaptureProtectionLevel.Software;
        // DWM can retain the capture behavior of an existing layered/
        // DirectComposition surface even though GetWindowDisplayAffinity already
        // reports the new value. Recreate the HWND and its renderer so the new
        // policy takes effect immediately, just as it does after an app restart.
        _overlayHost.ResetOutputWindow(CurrentOutputMonitor, EffectiveOverlayAntiCaptureMode);
        ApplyEffectiveCaptureProtection();
        Save(immediate: true);
        if (restart) Start();
        LogProtectionState("software result change completed");
        StateChanged?.Invoke();
    }
    public void SetProtectionLevel(CaptureProtectionLevel value)
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
        if (value == ProtectionLevel) return;
        var restart = IsRunning;
        Logger.Info($"Protection change requested: level={value}; previousLevel={ProtectionLevel}; softwareResult={_settings.AntiCapture}; running={restart}");
        Stop();
        if (ProtectionLevel == CaptureProtectionLevel.Kernel)
            _kernelProtection.Disable();
        _capture.ResetProtectionStatus();
        _settings.ProtectionLevel = value;
        _settings.GpuProtection = value == CaptureProtectionLevel.Hardware ? GpuProtectionMode.HardwareRequired : GpuProtectionMode.Off;
        // Hardware protection is provided by the protected/display-only swap
        // chain. Window display affinity is an optional compatibility layer.
        if (value == CaptureProtectionLevel.Off) _settings.AntiCapture = AntiCaptureMode.Off;
        else if (value == CaptureProtectionLevel.Software && _settings.AntiCapture == AntiCaptureMode.Off)
            _settings.AntiCapture = AntiCaptureMode.Exclude;
        else if (value == CaptureProtectionLevel.Kernel)
            _settings.AntiCapture = AntiCaptureMode.Off;
        // A new HWND guarantees that a protected DirectComposition target and its
        // display-affinity state cannot leak across protection-level transitions.
        _overlayHost.ResetOutputWindow(CurrentOutputMonitor, EffectiveOverlayAntiCaptureMode);
        ApplyEffectiveCaptureProtection(requireKernel: value == CaptureProtectionLevel.Kernel);
        Save(immediate: true);
        if (restart) Start();
        LogProtectionState("protection level change completed");
        StateChanged?.Invoke();
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
        if (_running) Stop(); // Reap a worker that failed closed before retrying.
        if (CurrentInputMonitor.Identity == CurrentOutputMonitor.Identity && !_settings.AllowSameDisplay)
            throw new InvalidOperationException("Capture Display and Output Display are identical. Change one selection to prevent a feedback loop.");
        UpdateOutputMonitor();
        if (_settings.GpuProtection != GpuProtectionMode.Off)
            _overlayHost.ResetOutputWindow(CurrentOutputMonitor, EffectiveOverlayAntiCaptureMode);
        Logger.Info($"Starting: capture={CurrentInputMonitor.Identity}; output={CurrentOutputMonitor.Identity}; different={CurrentInputMonitor.Identity != CurrentOutputMonitor.Identity}; captureSize={CurrentInputMonitor.Bounds.Size}; outputSize={CurrentOutputMonitor.Bounds.Size}; scaling={ScalingMode}");
        _overlayHost.ShowOverlay();
        try { ApplyEffectiveCaptureProtection(requireKernel: ProtectionLevel == CaptureProtectionLevel.Kernel); }
        catch { _overlayHost.HideOverlay(); throw; }
        _capture.Start(CurrentInputMonitor, CurrentOutputMonitor, ChromaThreshold, Sharpness / 100f, ScalingMode, _overlayHost.OverlayHandle, CaptureOptions.From(_settings));
        _running = true; _overlayHost.SetHudMode(_settings.LatencyTestMode ? PerformanceHudMode.Detailed : _settings.PerformanceHud, IsRunning);
        if (_capture.ProtectionStatus.Blocked) HandleProtectedOutputBlocked();
        StateChanged?.Invoke();
    }
    public void Stop() { if (!_running) return; _capture.Stop(); _overlayHost.ClearFrame(); _overlayHost.HideOverlay(); _overlayHost.SetHudMode(_settings.PerformanceHud, false); _running = false; StateChanged?.Invoke(); }
    public void Dispose() { if (_disposed) return; _disposed = true; _capture.FrameReady -= HandleFrameReady; _capture.RecreateRequested -= HandleCaptureRecreate; _capture.ProtectedOutputBlocked -= HandleProtectedOutputBlocked; Stop(); _kernelProtection.Dispose(); _capture.Dispose(); _overlayHost.Dispose(); SettingsStore.Flush(); }

    private void HandleFrameReady(FrameEnvelope frame) => _overlayHost.SetFrame(frame);
    private void HandleProtectedOutputBlocked() => _overlayHost.HideBlockedOutput(() => _capture.ProtectionStatus.Blocked);
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
    private AntiCaptureMode EffectiveOverlayAntiCaptureMode => ProtectionLevel switch
    {
        CaptureProtectionLevel.Software => _settings.AntiCapture,
        _ => AntiCaptureMode.Off
    };
    private AntiCaptureMode EffectiveHudAntiCaptureMode => ProtectionLevel switch
    {
        CaptureProtectionLevel.Software => _settings.AntiCapture,
        _ => AntiCaptureMode.Off
    };
    private void ApplyEffectiveCaptureProtection(bool requireKernel = false)
    {
        _overlayHost.SetAntiCaptureModes(EffectiveOverlayAntiCaptureMode, EffectiveHudAntiCaptureMode);
        if (ProtectionLevel != CaptureProtectionLevel.Kernel) return;

        try
        {
            _kernelProtection.Enable(_overlayHost.OverlayHandle, _overlayHost.HudHandle, _settingsWindowHandle);
        }
        catch (Exception ex)
        {
            _kernelProtection.MarkFailure(ex);
            if (requireKernel)
                throw new InvalidOperationException($"Kernel protection failed: {ex.Message}", ex);
        }
    }
    private void LogProtectionState(string reason)
    {
        try
        {
            var verification = InspectCaptureProtection();
            Logger.Info($"Protection state: reason='{reason}'; level={ProtectionLevel}; softwareResult={_settings.AntiCapture}; verified={verification.Verified}; failed={verification.Failed}; label='{verification.Label}'; {verification.AffinityLabel}");
        }
        catch (Exception ex)
        {
            Logger.Error($"Protection state inspection failed after '{reason}': {ex.GetType().Name}: {ex.Message}");
        }
    }
    public CaptureProtectionVerification InspectCaptureProtection()
    {
        var windows = _overlayHost.InspectAntiCapture();
        var gpu = GpuOutputStatus;
        var windowVerified = windows.Overlay.Verified && windows.Hud.Verified;
        var actual = $"Overlay {windows.Overlay.ActualLabel} / HUD {windows.Hud.ActualLabel}";
        var offModesMatch = windows.Overlay.Requested == AntiCaptureMode.Off && windows.Hud.Requested == AntiCaptureMode.Off;
        var softwareModesMatch = windows.Overlay.Requested == _settings.AntiCapture && windows.Hud.Requested == _settings.AntiCapture;
        var hardwareModesMatch = windows.Overlay.Requested == AntiCaptureMode.Off && windows.Hud.Requested == AntiCaptureMode.Off;
        var requested = $"requested Overlay {windows.Overlay.Requested} / HUD {windows.Hud.Requested}";
        var settingsAffinity = ReadDisplayAffinity(_settingsWindowHandle);
        var affinityLabel = $"Affinity: Overlay {windows.Overlay.ActualLabel} | HUD {windows.Hud.ActualLabel} | Settings {settingsAffinity.Label}";
        if (ProtectionLevel == CaptureProtectionLevel.Kernel && !string.Equals(_lastKernelAffinityLog, affinityLabel, StringComparison.Ordinal))
        {
            _lastKernelAffinityLog = affinityLabel;
            Logger.Info($"Kernel Anti-Capture verification: {affinityLabel}");
        }
        var kernelAffinityIsNone = windows.Overlay.Verified && windows.Hud.Verified && settingsAffinity.Success &&
                                   windows.Overlay.ActualAffinity == NativeMethods.WdaNone &&
                                   windows.Hud.ActualAffinity == NativeMethods.WdaNone &&
                                   settingsAffinity.Value == NativeMethods.WdaNone;
        return ProtectionLevel switch
        {
            CaptureProtectionLevel.Off => new(windowVerified && offModesMatch, !windowVerified || !offModesMatch,
                $"OFF · {actual}", $"{requested}; {windows.Detail}", affinityLabel),
            CaptureProtectionLevel.Software => new(windowVerified && softwareModesMatch, !windowVerified || !softwareModesMatch,
                $"SOFTWARE {_settings.AntiCapture.ToString().ToUpperInvariant()} · {actual}", $"{requested}; {windows.Detail}", affinityLabel),
            CaptureProtectionLevel.Hardware => new(windowVerified && hardwareModesMatch && gpu.Active, gpu.Blocked || !windowVerified || !hardwareModesMatch,
                $"HARDWARE {(gpu.Active ? "ACTIVE" : gpu.Summary.ToUpperInvariant())} · {actual}", $"GPU protected/display-only. {gpu.Detail} | {requested}; {windows.Detail}", affinityLabel),
            CaptureProtectionLevel.Kernel => new(_kernelProtection.State.Active && kernelAffinityIsNone,
                _kernelProtection.State.Failed || !kernelAffinityIsNone,
                $"KERNEL · {_kernelProtection.State.Summary}",
                $"{_kernelProtection.State.Detail} {affinityLabel}",
                affinityLabel),
            _ => new(false, true, "UNKNOWN PROTECTION LEVEL", "The saved protection level is invalid.")
        };
    }
    private static DisplayAffinityReading ReadDisplayAffinity(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !NativeMethods.IsWindow(handle))
            return new(false, 0, "UNAVAILABLE");
        if (!NativeMethods.GetWindowDisplayAffinity(handle, out var value))
            return new(false, 0, $"ERROR ({System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
        var label = value switch
        {
            NativeMethods.WdaNone => "OFF (0x0)",
            NativeMethods.WdaMonitor => "BLACK (0x1)",
            NativeMethods.WdaExcludeFromCapture => "EXCLUDE (0x11)",
            _ => $"UNKNOWN (0x{value:X})"
        };
        return new(true, value, label);
    }
    private void UpdateOutputMonitor() => _overlayHost.SetMonitor(CurrentOutputMonitor);
    private int Resolve(DisplaySelection? selected, bool capture)
    {
        if (selected is not null) { var exact = Monitors.Select((m, i) => (m, i)).FirstOrDefault(x => selected.Matches(x.m)); if (exact.m is not null) return exact.i; Logger.Info($"Saved {(capture ? "capture" : "output")} display was not found; using fallback."); }
        var fallback = capture ? Monitors.Select((m, i) => (m, i)).FirstOrDefault(x => !x.m.IsPrimary).i : Monitors.Select((m, i) => (m, i)).FirstOrDefault(x => x.m.IsPrimary).i;
        return fallback >= 0 && fallback < Monitors.Count ? fallback : 0;
    }
    private int Wrap(int index) => index < 0 ? Monitors.Count - 1 : index >= Monitors.Count ? 0 : index;
    private void Save(bool immediate = false) { _settings.CaptureDisplay = DisplaySelection.From(CurrentInputMonitor); _settings.OutputDisplay = DisplaySelection.From(CurrentOutputMonitor); _settings.ChromaThreshold = ChromaThreshold; _settings.Sharpness = Sharpness; _settings.ScalingMode = ScalingMode; if (immediate) SettingsStore.SaveNow(_settings); else SettingsStore.Save(_settings); }
    private void NormalizeBranding()
    {
        // Upgrade only the former built-in defaults. User-entered branding is
        // intentionally preserved.
        if (string.Equals(_settings.AppDisplayName, "YarrOverlay", StringComparison.OrdinalIgnoreCase))
            _settings.AppDisplayName = "Overlay";
        if (string.Equals(_settings.WindowTitle, "YarrOverlay Control", StringComparison.OrdinalIgnoreCase))
            _settings.WindowTitle = "Overlay";
        if (string.Equals(_settings.WindowClassName, "YarrOverlay.Window", StringComparison.OrdinalIgnoreCase))
            _settings.WindowClassName = "Overlay.Window";
        _settings.AppDisplayName = NormalizeLabel(_settings.AppDisplayName, "Overlay", 48);
        _settings.WindowTitle = NormalizeLabel(_settings.WindowTitle, $"{_settings.AppDisplayName} Control", 96);
        try { _settings.WindowClassName = NormalizeWindowClassName(_settings.WindowClassName); }
        catch { _settings.WindowClassName = "Overlay.Window"; }
        if (!string.IsNullOrWhiteSpace(_settings.IconPath) && !System.IO.File.Exists(_settings.IconPath))
            _settings.IconPath = "";
    }
    private static string NormalizeLabel(string? value, string fallback, int maximumLength)
    {
        var cleaned = new string((value ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length == 0) cleaned = fallback;
        return cleaned.Length <= maximumLength ? cleaned : cleaned[..maximumLength];
    }
    private static string NormalizeWindowClassName(string? value)
    {
        var name = (value ?? "").Trim();
        if (name.Length is < 1 or > 128)
            throw new InvalidOperationException("Window class name must contain 1 to 128 characters.");
        if (name.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '_' or '-')))
            throw new InvalidOperationException("Window class name may contain only letters, numbers, dots, underscores and hyphens.");
        return name;
    }
}

internal readonly record struct CaptureProtectionVerification(bool Verified, bool Failed, string Label, string Detail, string? AffinityLabel = null);
internal readonly record struct DisplayAffinityReading(bool Success, uint Value, string Label);
