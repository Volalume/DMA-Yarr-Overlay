using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Vortice.DXGI;

namespace Overlay;

internal sealed record MonitorInfo(
    string Name,
    string DeviceName,
    string AdapterName,
    string AdapterLuid,
    uint OutputIndex,
    Rectangle Bounds,
    int RefreshRate,
    string Rotation,
    bool IsPrimary,
    bool IsActive,
    bool IsProbablyVirtual)
{
    public string Identity => $"{DeviceName}|{AdapterLuid}|{OutputIndex}";

    public static IReadOnlyList<MonitorInfo> Enumerate()
    {
        var screens = Screen.AllScreens.ToDictionary(s => s.DeviceName, StringComparer.OrdinalIgnoreCase);
        var monitors = new List<MonitorInfo>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            var adapterResult = factory.EnumAdapters1(adapterIndex, out var adapter);
            if (adapterResult.Failure || adapter is null) break;
            using (adapter)
            {
                var adapterDesc = adapter.Description1;
                var luid = adapterDesc.Luid.ToString();
                for (uint outputIndex = 0; ; outputIndex++)
                {
                    var outputResult = adapter.EnumOutputs(outputIndex, out var output);
                    if (outputResult.Failure || output is null) break;
                    using (output)
                    {
                        var desc = output.Description;
                        if (!desc.AttachedToDesktop) continue;
                        screens.TryGetValue(desc.DeviceName, out var screen);
                        var bounds = screen?.Bounds ?? new Rectangle(desc.DesktopCoordinates.Left, desc.DesktopCoordinates.Top,
                            desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left, desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top);
                        var device = desc.DeviceName.TrimEnd('\0');
                        var virtualHint = IsVirtualHint(adapterDesc.Description, device, screen);
                        var label = $"{device} — {bounds.Width}x{bounds.Height} @{GetRefreshRate(device)}Hz";
                        if (screen?.Primary == true) label += " [Primary]";
                        if (virtualHint) label += " [Virtual?]";
                        monitors.Add(new MonitorInfo(label, device, adapterDesc.Description.TrimEnd('\0'), luid, outputIndex,
                            bounds, GetRefreshRate(device), desc.Rotation.ToString(), screen?.Primary ?? false, true, virtualHint));
                    }
                }
            }
        }

        foreach (var monitor in monitors) Logger.Info($"Display: {monitor.Name}; device={monitor.DeviceName}; adapter={monitor.AdapterName}; luid={monitor.AdapterLuid}; output={monitor.OutputIndex}; bounds={monitor.Bounds}; rotation={monitor.Rotation}; active={monitor.IsActive}; virtualHint={monitor.IsProbablyVirtual}");
        return monitors;
    }

    private static bool IsVirtualHint(string adapterName, string deviceName, Screen? screen) =>
        screen is null || adapterName.Contains("virtual", StringComparison.OrdinalIgnoreCase) ||
        adapterName.Contains("indirect", StringComparison.OrdinalIgnoreCase) || deviceName.Contains("virtual", StringComparison.OrdinalIgnoreCase);

    private static int GetRefreshRate(string deviceName)
    {
        var mode = new NativeMethods.DevMode { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.DevMode>() };
        return NativeMethods.EnumDisplaySettings(deviceName, NativeMethods.EnumCurrentSettings, ref mode) ? Math.Max(0, mode.dmDisplayFrequency) : 0;
    }
}
