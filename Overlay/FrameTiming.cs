using System;
using System.Diagnostics;
using System.Threading;

namespace Overlay;

internal sealed class FrameTiming
{
    private static long _nextId;
    public long FrameId { get; } = Interlocked.Increment(ref _nextId);
    public long AcquireCallStart, AcquireReturned, DesktopPresentTime, DesktopMouseUpdateTime, SourceCopySubmitted, ShaderSubmitted, StagingCopySubmitted, MapStarted, MapReturned, CpuCopyStarted, CpuCopyFinished, FrameEventRaised, OverlayFrameReceived, UiRenderStarted, HBitmapStarted, HBitmapReturned, SubmitStarted, SubmitReturned, FrameDroppedOrReplaced;
    public long UpdateLayeredWindowStarted { get => SubmitStarted; set => SubmitStarted = value; }
    public long UpdateLayeredWindowReturned { get => SubmitReturned; set => SubmitReturned = value; }
    public int AccumulatedFrames;
    public uint TotalMetadataBufferSize;
    public bool PointerVisible, ProtectedContentMaskedOut;
    public uint PresentCount, PresentRefreshCount, SyncRefreshCount;
    public long PresentSyncQpcTime;
    public double GpuCopyMs = double.NaN, GpuShaderMs = double.NaN, GpuTotalMs = double.NaN;
    public static long Now => Stopwatch.GetTimestamp();
    public static double Ms(long start, long end) => start == 0 || end == 0 ? double.NaN : (end - start) * 1000.0 / Stopwatch.Frequency;
}

internal sealed class FrameEnvelope : IDisposable
{
    public FrameEnvelope(System.Drawing.Bitmap bitmap, FrameTiming timing, PerformanceCsvWriter? csv = null, string pipeline = "", string inputAdapter = "", string outputAdapter = "", int csvInterval = 1, bool metricsEnabled = true) { Bitmap = bitmap; Timing = timing; Csv=csv; Pipeline=pipeline; InputAdapter=inputAdapter; OutputAdapter=outputAdapter; CsvInterval=csvInterval; MetricsEnabled=metricsEnabled; }
    public System.Drawing.Bitmap Bitmap { get; private set; }
    public FrameTiming Timing { get; }
    public PerformanceCsvWriter? Csv { get; }
    public string Pipeline { get; }
    public string InputAdapter { get; }
    public string OutputAdapter { get; }
    public int CsvInterval { get; }
    public bool MetricsEnabled { get; }
    public void Dispose() { Bitmap.Dispose(); }
}

internal sealed class LatencyMetrics
{
    public static LatencyMetrics Global { get; } = new();
    private const int Capacity = 65536;
    private readonly object _sync = new();
    private readonly MetricSample[] _samples = new MetricSample[Capacity];
    private readonly long[] _captureTimestamps = new long[Capacity];
    private readonly SessionAccumulator _session = new();
    private int _write, _count, _captureWrite, _captureCount;
    private long _captured, _replaced, _dropped, _submitted, _timeouts, _dxgiErrors, _accumulated, _maxAccumulated, _queueLength, _maxQueue;
    private long _sessionStart = FrameTiming.Now, _lastSubmittedTimestamp;
    private int _renderThreadId;
    public void RecordTimeout() => Interlocked.Increment(ref _timeouts);
    public void RecordDxgiError() => Interlocked.Increment(ref _dxgiErrors);
    public void RecordReplaced() => Interlocked.Increment(ref _replaced);
    public void RecordDropped() => Interlocked.Increment(ref _dropped);
    public int RenderThreadId { get => Volatile.Read(ref _renderThreadId); set => Volatile.Write(ref _renderThreadId, value); }
    public void RecordCaptured(int accumulatedFrames)
    {
        lock(_sync){_captureTimestamps[_captureWrite]=FrameTiming.Now;_captureWrite=(_captureWrite+1)%Capacity;if(_captureCount<Capacity)_captureCount++;}
        Interlocked.Increment(ref _captured);
        Interlocked.Add(ref _accumulated, accumulatedFrames);
        long old; while (accumulatedFrames > (old = Volatile.Read(ref _maxAccumulated)) && Interlocked.CompareExchange(ref _maxAccumulated, accumulatedFrames, old) != old) { }
    }
    public void RecordQueueLength(int length) { long old; while (length > (old = Volatile.Read(ref _maxQueue)) && Interlocked.CompareExchange(ref _maxQueue, length, old) != old) { } }
    public void SetQueueLength(int length) { Volatile.Write(ref _queueLength, length); RecordQueueLength(length); }
    public void Reset() { lock (_sync) { _write = _count = _captureWrite = _captureCount = 0; _sessionStart = FrameTiming.Now; _lastSubmittedTimestamp = 0; _session.Reset(); } _captured = _replaced = _dropped = _submitted = _timeouts = _dxgiErrors = _accumulated = _maxAccumulated = _maxQueue = 0; _queueLength = 0; _renderThreadId = 0; }
    public void RecordSubmitted(FrameTiming t)
    {
        var now = FrameTiming.Now;
        lock (_sync)
        {
            var interval = _lastSubmittedTimestamp == 0 ? double.NaN : FrameTiming.Ms(_lastSubmittedTimestamp, now);
            _lastSubmittedTimestamp = now;
            var sample = new MetricSample(now, FrameTiming.Ms(t.AcquireCallStart, t.AcquireReturned), FrameTiming.Ms(t.AcquireReturned, t.SubmitReturned), FrameTiming.Ms(t.DesktopPresentTime, t.SubmitReturned), FrameTiming.Ms(t.MapStarted, t.MapReturned), FrameTiming.Ms(t.CpuCopyStarted, t.CpuCopyFinished), FrameTiming.Ms(t.OverlayFrameReceived, t.UiRenderStarted), FrameTiming.Ms(t.HBitmapStarted,t.HBitmapReturned), FrameTiming.Ms(t.SubmitStarted, t.SubmitReturned), FrameTiming.Ms(t.DesktopPresentTime, t.SubmitStarted), interval, t.GpuCopyMs, t.GpuShaderMs, t.GpuTotalMs);
            _samples[_write] = sample; _write = (_write + 1) % Capacity; if (_count < Capacity) _count++;
            _session.Add(sample);
        }
        Interlocked.Increment(ref _submitted);
    }
    public PerformanceSnapshot Snapshot()
    {
        MetricSample[] copy; long[] captures; lock (_sync) { copy = new MetricSample[_count]; for (var i = 0; i < _count; i++) copy[i] = _samples[(_write - _count + i + Capacity) % Capacity]; captures=new long[_captureCount];for(var i=0;i<_captureCount;i++)captures[i]=_captureTimestamps[(_captureWrite-_captureCount+i+Capacity)%Capacity]; }
        var now = FrameTiming.Now;
        var oneCutoff=now-Stopwatch.Frequency;var tenCutoff=now-Stopwatch.Frequency*10L;
        var one = DescribeWindow(copy, oneCutoff) with { CaptureFps=CaptureRate(captures,oneCutoff,now) };
        var ten = DescribeWindow(copy, tenCutoff) with { CaptureFps=CaptureRate(captures,tenCutoff,now) };
        var sessionSeconds=Math.Max(0.001,FrameTiming.Ms(_sessionStart,now)/1000.0);
        WindowPerformance session; lock (_sync) session = _session.Describe(sessionSeconds,Volatile.Read(ref _captured)/sessionSeconds);
        return new PerformanceSnapshot(Volatile.Read(ref _captured), Volatile.Read(ref _submitted), Volatile.Read(ref _replaced), Volatile.Read(ref _dropped), Volatile.Read(ref _timeouts), Volatile.Read(ref _dxgiErrors), Volatile.Read(ref _accumulated), Volatile.Read(ref _maxAccumulated), (int)Volatile.Read(ref _queueLength), Volatile.Read(ref _maxQueue), one, ten, session);
    }
    private static double CaptureRate(long[] timestamps,long cutoff,long now){var start=0;while(start<timestamps.Length&&timestamps[start]<cutoff)start++;if(start==timestamps.Length)return 0;var duration=Math.Max(.001,FrameTiming.Ms(Math.Max(cutoff,timestamps[start]),now)/1000.0);return (timestamps.Length-start)/duration;}
    private static WindowPerformance DescribeWindow(MetricSample[] samples, long cutoff)
    {
        var start = 0; while (start < samples.Length && samples[start].Timestamp < cutoff) start++;
        var count = samples.Length - start; if (count <= 0) return WindowPerformance.Empty;
        TimingStats D(Func<MetricSample,double> selector) { var values = new double[count]; var valid = 0; for (var i=start;i<samples.Length;i++){var v=selector(samples[i]);if(!double.IsNaN(v))values[valid++]=v;} return Describe(values, valid); }
        var duration = Math.Max(0.001, FrameTiming.Ms(samples[start].Timestamp, FrameTiming.Now) / 1000.0);
        return new WindowPerformance(0, count / duration, D(x=>x.Pipeline), D(x=>x.DesktopToSubmit), D(x=>x.AcquireWait), D(x=>x.MapWait), D(x=>x.CpuCopy), D(x=>x.UiQueue), D(x=>x.HBitmap), D(x=>x.Submit), D(x=>x.FrameAge), D(x=>x.FrameInterval), D(x=>x.GpuCopy), D(x=>x.GpuShader), D(x=>x.GpuTotal));
    }
    private static TimingStats Describe(double[] values, int valid)
    {
        if (valid == 0) return TimingStats.Empty;
        var current = values[valid - 1];
        Array.Sort(values, 0, valid);
        double P(double p) => values[Math.Min(valid - 1, (int)Math.Ceiling((valid - 1) * p))];
        var sum = 0d; for (var i = 0; i < valid; i++) sum += values[i];
        return new TimingStats(current, sum / valid, values[0], values[valid - 1], P(.5), P(.95), P(.99));
    }
    private readonly record struct MetricSample(long Timestamp, double AcquireWait, double Pipeline, double DesktopToSubmit, double MapWait, double CpuCopy, double UiQueue, double HBitmap, double Submit, double FrameAge, double FrameInterval, double GpuCopy, double GpuShader, double GpuTotal);

    // Whole-session percentiles use bounded fixed histograms: no unbounded sample list or hot-path allocation.
    private sealed class SessionAccumulator
    {
        private long _count;
        private readonly Histogram _pipeline=new(), _desktop=new(), _acquire=new(), _map=new(), _cpu=new(), _ui=new(), _hbitmap=new(), _submit=new(), _age=new(), _interval=new(), _gpuCopy=new(), _gpuShader=new(), _gpuTotal=new();
        public void Reset() { _count=0; _pipeline.Reset();_desktop.Reset();_acquire.Reset();_map.Reset();_cpu.Reset();_ui.Reset();_hbitmap.Reset();_submit.Reset();_age.Reset();_interval.Reset();_gpuCopy.Reset();_gpuShader.Reset();_gpuTotal.Reset(); }
        public void Add(MetricSample s) { _count++; _pipeline.Add(s.Pipeline);_desktop.Add(s.DesktopToSubmit);_acquire.Add(s.AcquireWait);_map.Add(s.MapWait);_cpu.Add(s.CpuCopy);_ui.Add(s.UiQueue);_hbitmap.Add(s.HBitmap);_submit.Add(s.Submit);_age.Add(s.FrameAge);_interval.Add(s.FrameInterval);_gpuCopy.Add(s.GpuCopy);_gpuShader.Add(s.GpuShader);_gpuTotal.Add(s.GpuTotal); }
        public WindowPerformance Describe(double seconds,double captureFps) => new(captureFps,_count/seconds,_pipeline.Describe(),_desktop.Describe(),_acquire.Describe(),_map.Describe(),_cpu.Describe(),_ui.Describe(),_hbitmap.Describe(),_submit.Describe(),_age.Describe(),_interval.Describe(),_gpuCopy.Describe(),_gpuShader.Describe(),_gpuTotal.Describe());
    }
    private sealed class Histogram
    {
        private const double Width=.05; private const int Buckets=8192;
        private readonly long[] _bins=new long[Buckets]; private long _count; private double _sum, _min=double.PositiveInfinity, _max=double.NegativeInfinity, _current=double.NaN;
        public void Reset(){Array.Clear(_bins);_count=0;_sum=0;_min=double.PositiveInfinity;_max=double.NegativeInfinity;_current=double.NaN;}
        public void Add(double value){if(double.IsNaN(value)||double.IsInfinity(value))return;_current=value;_count++;_sum+=value;_min=Math.Min(_min,value);_max=Math.Max(_max,value);var bucket=Math.Clamp((int)(Math.Max(0,value)/Width),0,Buckets-1);_bins[bucket]++;}
        public TimingStats Describe(){if(_count==0)return TimingStats.Empty;double P(double p){var target=(long)Math.Ceiling(_count*p);long seen=0;for(var i=0;i<Buckets;i++){seen+=_bins[i];if(seen>=target)return i*Width;}return _max;}return new(_current,_sum/_count,_min,_max,P(.5),P(.95),P(.99));}
    }
}
internal sealed record TimingStats(double Current, double Average, double Minimum, double Maximum, double P50, double P95, double P99)
{ public static readonly TimingStats Empty = new(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN); }
internal sealed record WindowPerformance(double CaptureFps, double SubmitFps, TimingStats Pipeline, TimingStats DesktopToSubmit, TimingStats AcquireWait, TimingStats MapWait, TimingStats CpuCopy, TimingStats UiQueue, TimingStats HBitmap, TimingStats Submit, TimingStats FrameAge, TimingStats FrameInterval, TimingStats GpuCopy, TimingStats GpuShader, TimingStats GpuTotal)
{ public static readonly WindowPerformance Empty = new(0, 0, TimingStats.Empty, TimingStats.Empty, TimingStats.Empty, TimingStats.Empty, TimingStats.Empty, TimingStats.Empty, TimingStats.Empty, TimingStats.Empty, TimingStats.Empty, TimingStats.Empty, TimingStats.Empty, TimingStats.Empty, TimingStats.Empty); }
internal sealed record PerformanceSnapshot(long CapturedFrames, long SubmittedFrames, long ReplacedFrames, long DroppedFrames, long AcquireTimeouts, long DxgiErrors, long AccumulatedFrames, long MaxAccumulatedFrames, int QueueLength, long MaxQueueLength, WindowPerformance OneSecond, WindowPerformance TenSeconds, WindowPerformance Session)
{
    public TimingStats Pipeline => TenSeconds.Pipeline; public TimingStats MapWait => TenSeconds.MapWait; public TimingStats CpuCopy => TenSeconds.CpuCopy; public TimingStats UiQueue => TenSeconds.UiQueue;
    // Closest non-blocking app-side latency estimate: source desktop present to
    // overlay submission. Physical output scan-out is intentionally not included.
    public double TotalAppLatencyEstimateMs => OneSecond.DesktopToSubmit.Current;
}
