using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Overlay;

internal sealed class KernelCaptureProtection : IDisposable
{
    private const string DevicePath = @"\\.\NoScreen";
    private const uint FileDeviceNoScreen = 0x3138;
    private const uint FunctionProtectSpriteContent = 0x2056 + 0x10;
    private const uint MethodBuffered = 0;
    private const uint FileReadAccess = 0x0001;
    private const uint FileWriteAccess = 0x0002;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private static readonly uint IoctlProtectSpriteContent = CtlCode(
        FileDeviceNoScreen,
        FunctionProtectSpriteContent,
        MethodBuffered,
        FileReadAccess | FileWriteAccess);

    private SafeFileHandle? _device;
    private IntPtr _overlayHandle;
    private IntPtr _hudHandle;
    private IntPtr _settingsHandle;
    private bool _abiLogged;

    public KernelProtectionState State { get; private set; } = KernelProtectionState.Off;
    public bool DriverLoaded => _device is { IsInvalid: false, IsClosed: false };

    public void Enable(IntPtr overlayHandle, IntPtr hudHandle, IntPtr settingsHandle)
    {
        ValidateWindowHandle(overlayHandle, nameof(overlayHandle));
        ValidateWindowHandle(hudHandle, nameof(hudHandle));
        if (settingsHandle != IntPtr.Zero) ValidateWindowHandle(settingsHandle, nameof(settingsHandle));
        EnsureConnected();

        Send(overlayHandle, NativeMethods.WdaExcludeFromCapture);
        try
        {
            Send(hudHandle, NativeMethods.WdaExcludeFromCapture);
            if (settingsHandle != IntPtr.Zero)
                Send(settingsHandle, NativeMethods.WdaExcludeFromCapture);
        }
        catch
        {
            TrySend(overlayHandle, NativeMethods.WdaNone);
            TrySend(hudHandle, NativeMethods.WdaNone);
            TrySend(settingsHandle, NativeMethods.WdaNone);
            _overlayHandle = IntPtr.Zero;
            _hudHandle = IntPtr.Zero;
            _settingsHandle = IntPtr.Zero;
            throw;
        }

        _overlayHandle = overlayHandle;
        _hudHandle = hudHandle;
        _settingsHandle = settingsHandle;
        var protectedWindows = settingsHandle == IntPtr.Zero ? "Overlay and HUD" : "Overlay, HUD and Settings";
        State = new(true, false, "ACTIVE", $"Driver is loaded. NoScreen accepted Exclude for {protectedWindows} HWNDs.");
        Logger.Info($"Kernel Anti-Capture: active ({protectedWindows}).");
    }

    public void Disable()
    {
        var overlayHandle = _overlayHandle;
        var hudHandle = _hudHandle;
        var settingsHandle = _settingsHandle;

        if (overlayHandle == IntPtr.Zero && hudHandle == IntPtr.Zero && settingsHandle == IntPtr.Zero)
        {
            State = KernelProtectionState.Off;
            return;
        }

        try
        {
            EnsureConnected();
            if (overlayHandle != IntPtr.Zero) Send(overlayHandle, NativeMethods.WdaNone);
            if (hudHandle != IntPtr.Zero) Send(hudHandle, NativeMethods.WdaNone);
            if (settingsHandle != IntPtr.Zero) Send(settingsHandle, NativeMethods.WdaNone);

            _overlayHandle = IntPtr.Zero;
            _hudHandle = IntPtr.Zero;
            _settingsHandle = IntPtr.Zero;
            State = KernelProtectionState.Off;
            Logger.Info("Kernel Anti-Capture: disabled.");
        }
        catch (Exception ex)
        {
            State = new(false, true, "RESET FAILED", ex.Message);
            Logger.Error($"Kernel Anti-Capture reset failed: {ex.Message}");
            throw;
        }
    }

    public void MarkFailure(Exception ex)
    {
        State = new(false, true, "FAILED", ex.Message);
        Logger.Error($"Kernel Anti-Capture failed: {ex.Message}");
    }

    public void Dispose()
    {
        try { Disable(); }
        catch { }
        _device?.Dispose();
        _device = null;
    }

    private void EnsureConnected()
    {
        if (_device is { IsInvalid: false, IsClosed: false }) return;

        LogAbiOnce();
        var dosTarget = new StringBuilder(512);
        var queryLength = QueryDosDevice("NoScreen", dosTarget, dosTarget.Capacity);
        var queryError = queryLength == 0 ? Marshal.GetLastWin32Error() : 0;
        Logger.Debug($"Kernel probe: QueryDosDevice result={queryLength}, error={queryError}, target='{dosTarget}'");

        _device?.Dispose();
        Logger.Debug($"Kernel probe: opening {DevicePath}.");
        _device = CreateFile(
            DevicePath,
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (_device.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            var errorText = new Win32Exception(error).Message;
            Logger.Error($"Kernel Anti-Capture probe: CreateFile failed; error={error} (0x{error:X8}), message='{errorText}'");
            _device.Dispose();
            _device = null;
            throw new Win32Exception(error, $"NoScreen device open failed ({error}: {errorText}).");
        }

        Logger.Debug("Kernel probe: device opened.");
    }

    private void Send(IntPtr windowHandle, uint value)
    {
        var request = new ProtectSpriteContentRequest
        {
            Value = value,
            WindowHandle = unchecked((ulong)windowHandle.ToInt64())
        };

        var targetThread = NativeMethods.GetWindowThreadProcessId(windowHandle, out var targetProcess);
        Logger.Debug($"Kernel IOCTL: value=0x{value:X}, hwnd=0x{windowHandle:X}, pid={targetProcess}, tid={targetThread}");
        if (!DeviceIoControl(
                _device!,
                IoctlProtectSpriteContent,
                ref request,
                (uint)Marshal.SizeOf<ProtectSpriteContentRequest>(),
                IntPtr.Zero,
                0,
                out var bytesReturned,
                IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            var errorText = new Win32Exception(error).Message;
            Logger.Error($"Kernel Anti-Capture IOCTL failed: code=0x{IoctlProtectSpriteContent:X8}, error={error} (0x{error:X8}), message='{errorText}', bytes={bytesReturned}");
            throw new Win32Exception(error,
                $"NoScreen rejected value 0x{value:X} for HWND 0x{windowHandle:X} ({error}: {errorText}).");
        }

        Logger.Debug($"Kernel IOCTL succeeded: value=0x{value:X}, hwnd=0x{windowHandle:X}, bytes={bytesReturned}");
    }

    private void LogAbiOnce()
    {
        if (_abiLogged) return;
        _abiLogged = true;
        Logger.Debug(
            $"Kernel Anti-Capture ABI: process64={Environment.Is64BitProcess}, os64={Environment.Is64BitOperatingSystem}, " +
            $"device='{DevicePath}', ioctl=0x{IoctlProtectSpriteContent:X8}, requestSize={Marshal.SizeOf<ProtectSpriteContentRequest>()}, " +
            $"valueOffset={Marshal.OffsetOf<ProtectSpriteContentRequest>(nameof(ProtectSpriteContentRequest.Value))}, " +
            $"hwndOffset={Marshal.OffsetOf<ProtectSpriteContentRequest>(nameof(ProtectSpriteContentRequest.WindowHandle))}");
    }

    private bool TrySend(IntPtr windowHandle, uint value)
    {
        if (windowHandle == IntPtr.Zero) return true;
        try
        {
            Send(windowHandle, value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ValidateWindowHandle(IntPtr handle, string parameterName)
    {
        if (handle == IntPtr.Zero || !NativeMethods.IsWindow(handle))
            throw new ArgumentException("A valid window handle is required.", parameterName);
    }

    private static uint CtlCode(uint deviceType, uint function, uint method, uint access) =>
        (deviceType << 16) | (access << 14) | (function << 2) | method;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProtectSpriteContentRequest
    {
        public uint Value;
        public ulong WindowHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        ref ProtectSpriteContentRequest inputBuffer,
        uint inputBufferSize,
        IntPtr outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string deviceName, StringBuilder targetPath, int maximumLength);
}

internal readonly record struct KernelProtectionState(bool Active, bool Failed, string Summary, string Detail)
{
    public static readonly KernelProtectionState Off = new(false, false, "OFF", "Kernel protection is disabled.");
}
