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
        var state = ApplyToHandle(_window.Handle, _mode, _window.GetType().Name);
        _status = state.Verified
            ? state.Requested.ToString()
            : state.Detail;
    }

    internal static WindowCaptureState ApplyToHandle(IntPtr handle, AntiCaptureMode mode, string targetName)
    {
        mode = Enum.IsDefined(mode) ? mode : AntiCaptureMode.Off;
        var expected = GetExpectedAffinity(mode, out var excludeUnavailable);
        if (handle == IntPtr.Zero || !NativeMethods.IsWindow(handle))
            return new(mode, expected, 0, false, "No window handle");
        if (!NativeMethods.SetWindowDisplayAffinity(handle, expected))
            return Failure(mode, expected, $"Failed (Win32 {Marshal.GetLastWin32Error()})", targetName);
        if (!NativeMethods.GetWindowDisplayAffinity(handle, out var actual))
            return Failure(mode, expected, $"Unverified (Win32 {Marshal.GetLastWin32Error()})", targetName);
        if (actual != expected)
            return Failure(mode, expected, $"Mismatch (0x{actual:X})", targetName, actual);

        var detail = excludeUnavailable
            ? "Black (Exclude requires Windows 10 2004+)"
            : $"verified 0x{actual:X}";
        Logger.Debug($"Anti-Capture {targetName}: {mode}, affinity=0x{actual:X}");
        return new(mode, expected, actual, true, detail);
    }

    internal static uint GetExpectedAffinity(AntiCaptureMode mode, out bool excludeUnavailable)
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

    private static WindowCaptureState Failure(
        AntiCaptureMode mode,
        uint expected,
        string detail,
        string targetName,
        uint actual = 0)
    {
        Logger.Error($"Anti-Capture {targetName}: requested {mode}; {detail}");
        return new(mode, expected, actual, false, detail);
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
