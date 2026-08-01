using System;

namespace Overlay;

internal sealed record CaptureDiagnostics(int CaptureFps, long DroppedFrames, int AcquireFailures, DateTimeOffset? LastFrameAt, bool BlackScreen, int RecreateCount, string LastError, PerformanceSnapshot? Performance = null, string PipelineMode = "Stopped", int CaptureThreadId = 0, int RenderThreadId = 0, string CaptureAdapter = "", string OutputAdapter = "", bool SameAdapter = false, long CsvLostRows = 0, int Gen0 = 0, int Gen1 = 0, int Gen2 = 0, long AllocatedBytes = 0, long WorkingSetBytes = 0, double LastRecoveryMs = 0, double TotalRecoveryMs = 0)
{
    public static readonly CaptureDiagnostics Empty = new(0, 0, 0, null, false, 0, "");
}
