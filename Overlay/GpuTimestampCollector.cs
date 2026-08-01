using System;
using Vortice.Direct3D11;

namespace Overlay;

internal sealed class GpuTimestampCollector : IDisposable
{
    private const int SlotCount = 8;
    private readonly Slot[] _slots = new Slot[SlotCount];
    private int _cursor;
    public GpuTimestampCollector(ID3D11Device device)
    {
        for (var i = 0; i < SlotCount; i++) _slots[i] = new Slot(
            device.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint)),
            device.CreateQuery(new QueryDescription(QueryType.Timestamp)),
            device.CreateQuery(new QueryDescription(QueryType.Timestamp)),
            device.CreateQuery(new QueryDescription(QueryType.Timestamp)));
    }
    public void Begin(ID3D11DeviceContext context)
    {
        var slot = _slots[_cursor]; context.Begin(slot.Disjoint); context.End(slot.Start); slot.Pending = true;
    }
    public void CopyFinished(ID3D11DeviceContext context) => context.End(_slots[_cursor].CopyEnd);
    public void ShaderFinished(ID3D11DeviceContext context) { context.End(_slots[_cursor].ShaderEnd); context.End(_slots[_cursor].Disjoint); _cursor = (_cursor + 1) % SlotCount; }
    public unsafe bool TryCollect(ID3D11DeviceContext context, out double copyMs, out double shaderMs, out double totalMs)
    {
        copyMs = shaderMs = totalMs = double.NaN;
        var slot = _slots[_cursor]; // eight frames old; never wait for it
        if (!slot.Pending) return false;
        QueryDataTimestampDisjoint disjoint = default; ulong start = 0, copyEnd = 0, shaderEnd = 0;
        if (context.GetData(slot.Disjoint, (IntPtr)(&disjoint), (uint)sizeof(QueryDataTimestampDisjoint), AsyncGetDataFlags.DoNotFlush).Failure ||
            context.GetData(slot.Start, (IntPtr)(&start), sizeof(ulong), AsyncGetDataFlags.DoNotFlush).Failure ||
            context.GetData(slot.CopyEnd, (IntPtr)(&copyEnd), sizeof(ulong), AsyncGetDataFlags.DoNotFlush).Failure ||
            context.GetData(slot.ShaderEnd, (IntPtr)(&shaderEnd), sizeof(ulong), AsyncGetDataFlags.DoNotFlush).Failure) return false;
        slot.Pending = false;
        if (disjoint.Disjoint || disjoint.Frequency == 0) return false;
        copyMs = (copyEnd - start) * 1000.0 / disjoint.Frequency;
        shaderMs = (shaderEnd - copyEnd) * 1000.0 / disjoint.Frequency;
        totalMs = (shaderEnd - start) * 1000.0 / disjoint.Frequency;
        return true;
    }
    public void Dispose() { foreach (var slot in _slots) slot.Dispose(); }
    private sealed class Slot : IDisposable
    {
        public Slot(ID3D11Query disjoint, ID3D11Query start, ID3D11Query copyEnd, ID3D11Query shaderEnd) { Disjoint=disjoint; Start=start; CopyEnd=copyEnd; ShaderEnd=shaderEnd; }
        public readonly ID3D11Query Disjoint, Start, CopyEnd, ShaderEnd; public bool Pending;
        public void Dispose() { ShaderEnd.Dispose(); CopyEnd.Dispose(); Start.Dispose(); Disjoint.Dispose(); }
    }
}
