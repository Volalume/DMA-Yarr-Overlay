using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Overlay;

// Called on the owning window thread; no work is added to the capture/render loop.
internal sealed class WindowCaptureProtection
{
    private readonly Form _window;
    private AntiCaptureMode _mode;
    private volatile string _status = "Pending";
    public string Status => _status;

    public WindowCaptureProtection(Form window)
    {
        _window = window;
        window.HandleCreated += (_, _) => Apply();
        window.HandleDestroyed += (_, _) => _status = "Pending";
        window.VisibleChanged += (_, _) => { if (window.Visible && window.IsHandleCreated) Apply(); };
    }

    public void SetMode(AntiCaptureMode mode)
    {
        _mode = Enum.IsDefined(mode) ? mode : AntiCaptureMode.Off;
        if (_window.IsHandleCreated) Apply();
        else _status = "Pending";
    }

    public WindowCaptureState Inspect()
    {
        if (!_window.IsHandleCreated)
            return new(_mode, 0, 0, false, "No window handle");
        var expected = GetExpectedAffinity(_mode, out _);
        if (!NativeMethods.GetWindowDisplayAffinity(_window.Handle, out var actual))
            return new(_mode, expected, 0, false, $"GetWindowDisplayAffinity failed (Win32 {Marshal.GetLastWin32Error()})");
        return new(_mode, expected, actual, actual == expected,
            actual == expected ? $"verified 0x{actual:X}" : $"expected 0x{expected:X}, actual 0x{actual:X}");
    }

    private void Apply()
    {
        // Older Windows silently treats Exclude as Black; report that fallback.
        var affinity = GetExpectedAffinity(_mode, out var excludeUnavailable);
        if (!NativeMethods.SetWindowDisplayAffinity(_window.Handle, affinity))
        {
            Fail($"Failed (Win32 {Marshal.GetLastWin32Error()})");
            return;
        }
        if (!NativeMethods.GetWindowDisplayAffinity(_window.Handle, out var actual))
        {
            Fail($"Unverified (Win32 {Marshal.GetLastWin32Error()})");
            return;
        }
        if (actual != affinity)
        {
            Fail($"Mismatch (0x{actual:X})");
            return;
        }
        _status = excludeUnavailable ? "Black (Exclude requires Windows 10 2004+)" : _mode.ToString();
        Logger.Info($"Anti-Capture {_window.GetType().Name}: {_status}, affinity=0x{actual:X}");
    }

    private static uint GetExpectedAffinity(AntiCaptureMode mode, out bool excludeUnavailable)
    {
        excludeUnavailable = mode == AntiCaptureMode.Exclude && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);
        return mode switch
        {
            AntiCaptureMode.Black => NativeMethods.WdaMonitor,
            AntiCaptureMode.Exclude when !excludeUnavailable => NativeMethods.WdaExcludeFromCapture,
            AntiCaptureMode.Exclude => NativeMethods.WdaMonitor,
            _ => NativeMethods.WdaNone
        };
    }

    private void Fail(string status)
    {
        _status = status;
        Logger.Error($"Anti-Capture {_window.GetType().Name}: requested {_mode}; {status}");
    }
}


internal readonly record struct WindowCaptureState(AntiCaptureMode Requested, uint ExpectedAffinity, uint ActualAffinity, bool Verified, string Detail)
{
    public static readonly WindowCaptureState Unavailable = new(AntiCaptureMode.Off, 0, 0, false, "Unavailable");
    public string ActualLabel => ActualAffinity switch
    {
        NativeMethods.WdaNone => "OFF (0x0)",
        NativeMethods.WdaMonitor => "BLACK (0x1)",
        NativeMethods.WdaExcludeFromCapture => "EXCLUDE (0x11)",
        _ => $"UNKNOWN (0x{ActualAffinity:X})"
    };
}

internal readonly record struct WindowCapturePair(WindowCaptureState Overlay, WindowCaptureState Hud)
{
    public string Detail => $"Overlay: {Overlay.Detail}; HUD: {Hud.Detail}";
}
