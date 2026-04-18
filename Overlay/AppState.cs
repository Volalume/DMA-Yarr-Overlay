using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace Overlay;

internal sealed class AppState : IDisposable
{
    private readonly DuplicationCapture _capture;
    private readonly OverlayHost _overlayHost;
    private bool _disposed;

    public AppState()
    {
        Monitors = MonitorInfo.Enumerate().ToList();
        SelectedInputIndex = 0;
        SelectedOutputIndex = Monitors.Count > 1 ? 1 : 0;
        ChromaThreshold = 45;
        Sharpness = 100;

        _overlayHost = new OverlayHost();
        _capture = new DuplicationCapture();
        _capture.FrameReady += HandleFrameReady;
        _capture.FpsChanged += fps =>
        {
            CaptureFps = fps;
            StateChanged?.Invoke();
        };

        UpdateOutputMonitor();
    }

    public event Action? StateChanged;

    public IReadOnlyList<MonitorInfo> Monitors { get; private set; }

    public int SelectedInputIndex { get; private set; }

    public int SelectedOutputIndex { get; private set; }

    public int ChromaThreshold { get; private set; }

    public int Sharpness { get; private set; }

    public bool IsRunning { get; private set; }

    public int CaptureFps { get; private set; }

    public string StatusText =>
        IsRunning
            ? $"Running | Input: {CurrentInputMonitor.Name} -> Output: {CurrentOutputMonitor.Name} | FPS: {CaptureFps}"
            : "Stopped";

    public MonitorInfo CurrentInputMonitor => Monitors[Math.Clamp(SelectedInputIndex, 0, Monitors.Count - 1)];

    public MonitorInfo CurrentOutputMonitor => Monitors[Math.Clamp(SelectedOutputIndex, 0, Monitors.Count - 1)];

    public void RefreshMonitors()
    {
        var currentInput = CurrentInputMonitor.DeviceName;
        var currentOutput = CurrentOutputMonitor.DeviceName;

        Monitors = MonitorInfo.Enumerate().ToList();
        if (Monitors.Count == 0)
        {
            throw new InvalidOperationException("No monitors detected.");
        }

        SelectedInputIndex = FindMonitorIndex(currentInput);
        SelectedOutputIndex = FindMonitorIndex(currentOutput);
        UpdateOutputMonitor();

        if (IsRunning)
        {
            RestartCapture();
        }

        StateChanged?.Invoke();
    }

    public void CycleInput(int direction)
    {
        SelectedInputIndex = WrapIndex(SelectedInputIndex + direction);
        if (IsRunning)
        {
            RestartCapture();
        }

        StateChanged?.Invoke();
    }

    public void CycleOutput(int direction)
    {
        SelectedOutputIndex = WrapIndex(SelectedOutputIndex + direction);
        UpdateOutputMonitor();
        if (IsRunning)
        {
            RestartCapture();
        }

        StateChanged?.Invoke();
    }

    public void IncreaseThreshold(int delta)
    {
        ChromaThreshold = Math.Clamp(ChromaThreshold + delta, 0, 80);
        _capture.SetThreshold(ChromaThreshold);
        StateChanged?.Invoke();
    }

    public void IncreaseSharpness(int delta)
    {
        Sharpness = Math.Clamp(Sharpness + delta, 0, 100);
        _capture.SetSharpness(Sharpness / 100.0f);
        StateChanged?.Invoke();
    }

    public void ToggleRunning()
    {
        if (IsRunning)
        {
            Stop();
            return;
        }

        Start();
    }

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        UpdateOutputMonitor();
        _overlayHost.ShowOverlay();
        _capture.Start(CurrentInputMonitor, CurrentOutputMonitor, ChromaThreshold, Sharpness / 100.0f);
        IsRunning = true;
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        _capture.Stop();
        _overlayHost.ClearFrame();
        _overlayHost.HideOverlay();
        CaptureFps = 0;
        IsRunning = false;
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _capture.FrameReady -= HandleFrameReady;
        Stop();
        _capture.Dispose();
        _overlayHost.Dispose();
    }

    private void RestartCapture()
    {
        _capture.Start(CurrentInputMonitor, CurrentOutputMonitor, ChromaThreshold, Sharpness / 100.0f);
    }

    private void UpdateOutputMonitor()
    {
        _overlayHost.SetMonitor(CurrentOutputMonitor);
    }

    private int FindMonitorIndex(string deviceName)
    {
        var index = Monitors
            .Select((monitor, i) => (monitor, i))
            .FirstOrDefault(x => string.Equals(x.monitor.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            .i;

        return index >= 0 && index < Monitors.Count ? index : 0;
    }

    private int WrapIndex(int index)
    {
        if (Monitors.Count == 0)
        {
            return 0;
        }

        if (index < 0)
        {
            return Monitors.Count - 1;
        }

        if (index >= Monitors.Count)
        {
            return 0;
        }

        return index;
    }

    private void HandleFrameReady(Bitmap frame)
    {
        _overlayHost.SetFrame(frame);
    }
}
