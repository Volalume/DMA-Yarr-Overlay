namespace Overlay;

internal readonly record struct CaptureOptions(
    PipelineMode PipelineMode,
    PerformanceHudMode HudMode,
    MetricsMode MetricsMode,
    int MaximumFrameLatency,
    CapturePriority ThreadPriority,
    bool EnableMmcss,
    bool CsvEnabled,
    int CsvIntervalFrames,
    bool LatencyTestMode,
    GpuProtectionMode GpuProtection)
{
    public static CaptureOptions From(AppSettings s) => new(
        s.PipelineMode, s.PerformanceHud, s.LatencyTestMode ? MetricsMode.DetailedGpu : s.MetricsCollection,
        System.Math.Clamp(s.MaximumFrameLatency, 1, 3), s.CaptureThreadPriority,
        s.EnableMmcss, s.CsvEnabled, System.Math.Max(1, s.CsvIntervalFrames), s.LatencyTestMode, s.GpuProtection);
}
