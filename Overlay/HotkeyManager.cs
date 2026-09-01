using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Forms;

namespace Overlay;

internal readonly record struct HotkeyBinding(Keys Key, uint Modifiers)
{
    public string DisplayText
    {
        get
        {
            var parts = new List<string>(4);
            if ((Modifiers & NativeMethods.ModControl) != 0) parts.Add("Ctrl");
            if ((Modifiers & NativeMethods.ModShift) != 0) parts.Add("Shift");
            if ((Modifiers & NativeMethods.ModAlt) != 0) parts.Add("Alt");
            if ((Modifiers & NativeMethods.ModWin) != 0) parts.Add("Win");
            parts.Add(Key.ToString());
            return string.Join(" + ", parts);
        }
    }
}

internal sealed class HotkeyManager : IDisposable
{
    private const int UiHotkeyA = 0x5901;
    private const int UiHotkeyB = 0x5902;

    private readonly IntPtr _windowHandle;
    private int _activeUiHotkeyId;
    private bool _thresholdIncreaseRegistered;
    private bool _thresholdDecreaseRegistered;
    private bool _disposed;

    public HotkeyManager(IntPtr windowHandle) => _windowHandle = windowHandle;

    public HotkeyBinding CurrentBinding { get; private set; }

    public string? LastError { get; private set; }

    public bool HasUiHotkey => _activeUiHotkeyId != 0;

    public void RegisterThresholdHotkeys()
    {
        _thresholdIncreaseRegistered = NativeMethods.RegisterHotKey(
            _windowHandle,
            NativeMethods.HotkeyIncrease,
            0,
            NativeMethods.VkOemplus);
        _thresholdDecreaseRegistered = NativeMethods.RegisterHotKey(
            _windowHandle,
            NativeMethods.HotkeyDecrease,
            0,
            NativeMethods.VkOemMinus);
    }

    public bool TrySetUiHotkey(HotkeyBinding binding)
    {
        var candidateId = _activeUiHotkeyId == UiHotkeyA ? UiHotkeyB : UiHotkeyA;
        var modifiers = binding.Modifiers | NativeMethods.ModNorepeat;
        if (!NativeMethods.RegisterHotKey(_windowHandle, candidateId, modifiers, (uint)binding.Key))
        {
            LastError = $"Could not register {binding.DisplayText}: {new Win32Exception().Message}";
            return false;
        }

        if (_activeUiHotkeyId != 0)
        {
            NativeMethods.UnregisterHotKey(_windowHandle, _activeUiHotkeyId);
        }

        _activeUiHotkeyId = candidateId;
        CurrentBinding = binding;
        LastError = null;
        return true;
    }

    public bool IsUiHotkey(int id) => id != 0 && id == _activeUiHotkeyId;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_activeUiHotkeyId != 0) NativeMethods.UnregisterHotKey(_windowHandle, _activeUiHotkeyId);
        if (_thresholdIncreaseRegistered) NativeMethods.UnregisterHotKey(_windowHandle, NativeMethods.HotkeyIncrease);
        if (_thresholdDecreaseRegistered) NativeMethods.UnregisterHotKey(_windowHandle, NativeMethods.HotkeyDecrease);
    }
}
