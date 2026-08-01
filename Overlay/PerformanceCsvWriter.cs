using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Overlay;

internal sealed class PerformanceCsvWriter : IDisposable
{
    private const int Capacity = 4096;
    private readonly ConcurrentQueue<CsvRow> _queue = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private volatile bool _stopping;
    private int _queued;
    private long _lost;
    public long LostRows => Interlocked.Read(ref _lost);

    public PerformanceCsvWriter()
    {
        _thread = new Thread(WriteLoop) { IsBackground = true, Name = "YarrOverlayMetricsCsv", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }
    public void TryWrite(FrameTiming t, string pipeline, string inputAdapter, string outputAdapter, int dxgiResult, int presentResult)
    {
        if (_stopping) return;
        if (Interlocked.Increment(ref _queued) > Capacity) { Interlocked.Decrement(ref _queued); Interlocked.Increment(ref _lost); return; }
        _queue.Enqueue(new CsvRow(t, pipeline, inputAdapter, outputAdapter, dxgiResult, presentResult)); _signal.Set();
    }
    private void WriteLoop()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"YarrOverlay-metrics-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.csv");
        using var writer = new StreamWriter(path, false);
        writer.WriteLine("FrameId,AcquireStart,AcquireReturned,DesktopPresent,MouseUpdate,SourceCopySubmitted,ShaderSubmitted,StagingCopySubmitted,MapStarted,MapReturned,CpuCopyStarted,CpuCopyFinished,FrameEventRaised,OverlayReceived,UiRenderStarted,HBitmapStarted,HBitmapReturned,SubmitStarted,SubmitReturned,DroppedOrReplaced,AccumulatedFrames,MetadataBytes,PointerVisible,ProtectedMasked,AcquireWaitMs,PipelineMs,DesktopToSubmitMs,MapWaitMs,CpuCopyMs,UiQueueMs,HBitmapMs,SubmitMs,GpuCopyMs,GpuShaderMs,GpuTotalMs,PresentCount,PresentRefreshCount,SyncRefreshCount,PresentSyncQpc,Pipeline,InputAdapter,OutputAdapter,DxgiResult,PresentResult");
        while (!_stopping || Volatile.Read(ref _queued) > 0)
        {
            while (_queue.TryDequeue(out var row)) { Interlocked.Decrement(ref _queued); writer.WriteLine(row.Format()); }
            writer.Flush(); _signal.WaitOne(250);
        }
    }
    public void Dispose() { _stopping = true; _signal.Set(); _thread.Join(); _signal.Dispose(); }
    private readonly record struct CsvRow(FrameTiming T, string Pipeline, string InputAdapter, string OutputAdapter, int Dxgi, int Present)
    {
        public string Format() => string.Join(',', T.FrameId, T.AcquireCallStart, T.AcquireReturned, T.DesktopPresentTime, T.DesktopMouseUpdateTime,
            T.SourceCopySubmitted, T.ShaderSubmitted, T.StagingCopySubmitted, T.MapStarted, T.MapReturned, T.CpuCopyStarted, T.CpuCopyFinished,
            T.FrameEventRaised, T.OverlayFrameReceived, T.UiRenderStarted, T.HBitmapStarted,T.HBitmapReturned,T.SubmitStarted, T.SubmitReturned, T.FrameDroppedOrReplaced,
            T.AccumulatedFrames, T.TotalMetadataBufferSize, T.PointerVisible ? 1 : 0, T.ProtectedContentMaskedOut ? 1 : 0,
            M(T.AcquireCallStart,T.AcquireReturned),M(T.AcquireReturned,T.SubmitReturned),M(T.DesktopPresentTime,T.SubmitReturned),M(T.MapStarted,T.MapReturned),M(T.CpuCopyStarted,T.CpuCopyFinished),M(T.OverlayFrameReceived,T.UiRenderStarted),M(T.HBitmapStarted,T.HBitmapReturned),M(T.SubmitStarted,T.SubmitReturned),D(T.GpuCopyMs),D(T.GpuShaderMs),D(T.GpuTotalMs),T.PresentCount,T.PresentRefreshCount,T.SyncRefreshCount,T.PresentSyncQpcTime,
            Escape(Pipeline), Escape(InputAdapter), Escape(OutputAdapter), Dxgi, Present);
        private static string Escape(string s) => '"' + s.Replace("\"", "\"\"") + '"';
        private static string M(long a,long b)=>D(FrameTiming.Ms(a,b));
        private static string D(double v)=>double.IsNaN(v)?"":v.ToString("F6",CultureInfo.InvariantCulture);
    }
}
