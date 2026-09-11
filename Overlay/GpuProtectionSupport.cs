using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Overlay;

internal static class GpuProtectionSupport
{
    // WDDM 2.4 requires supported HW-protected resources to be protected even
    // without DRM initialization. Older drivers can silently ignore the flag.
    // This version check is a compatibility gate, not hardware attestation.
    public static unsafe int RequireModernDriver(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        var luid = adapter.Description.Luid;
        var open = new OpenAdapter { LowPart = unchecked((uint)luid), HighPart = (int)(luid >> 32) };
        CheckStatus(D3DKMTOpenAdapterFromLuid(ref open), "Open GPU adapter");
        try
        {
            int version = 0;
            var query = new QueryAdapter
            {
                Adapter = open.Adapter, Type = 13, // KMTQAITYPE_DRIVERVERSION, d3dkmthk.h
                Data = (IntPtr)(&version), DataSize = sizeof(int)
            };
            CheckStatus(D3DKMTQueryAdapterInfo(ref query), "Query WDDM version");
            if (version < 2400)
                throw new NotSupportedException($"Hardware output requires WDDM 2.4+ (reported {version}).");
            return version;
        }
        finally
        {
            var close = new CloseAdapter { Adapter = open.Adapter };
            var result = D3DKMTCloseAdapter(ref close);
            if (result < 0) Logger.Error($"Close GPU adapter failed: NTSTATUS 0x{result:X8}");
        }
    }

    private static void CheckStatus(int result, string operation)
    {
        if (result < 0) throw new InvalidOperationException($"{operation}: NTSTATUS 0x{result:X8}");
    }

    // Native LUID has 4-byte alignment (not the 8-byte alignment of a C# long).
    [StructLayout(LayoutKind.Sequential)]
    private struct OpenAdapter { public uint LowPart; public int HighPart; public uint Adapter; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CloseAdapter { public uint Adapter; }
    [StructLayout(LayoutKind.Sequential)]
    private struct QueryAdapter { public uint Adapter; public int Type; public IntPtr Data; public uint DataSize; }

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int D3DKMTOpenAdapterFromLuid(ref OpenAdapter data);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapter data);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int D3DKMTCloseAdapter(ref CloseAdapter data);
}
