using System;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;

namespace Overlay;

internal enum ScalingMode { Stretch, Fit, Fill }
internal enum PipelineMode { Auto, GpuNative, LegacyCpu }
internal enum PerformanceHudMode { Off, Basic, Detailed }
internal enum MetricsMode { Off, Lightweight, DetailedGpu }
internal enum CapturePriority { Normal, AboveNormal, Highest }
internal enum PresentMode { Immediate }
internal sealed record DisplaySelection(string DeviceName, string AdapterLuid, uint OutputIndex, int Width, int Height, int X, int Y)
{
    public static DisplaySelection From(MonitorInfo m) => new(m.DeviceName, m.AdapterLuid, m.OutputIndex, m.Bounds.Width, m.Bounds.Height, m.Bounds.X, m.Bounds.Y);
    public bool Matches(MonitorInfo m) => string.Equals(DeviceName, m.DeviceName, StringComparison.OrdinalIgnoreCase) && AdapterLuid == m.AdapterLuid && OutputIndex == m.OutputIndex;
}
internal sealed class AppSettings
{
    public DisplaySelection? CaptureDisplay { get; set; }
    public DisplaySelection? OutputDisplay { get; set; }
    public int ChromaThreshold { get; set; } = 45;
    public int Sharpness { get; set; } = 100;
    public ScalingMode ScalingMode { get; set; } = ScalingMode.Stretch;
    public bool AllowSameDisplay { get; set; }
    public bool DebugOverlay { get; set; }
    public PipelineMode PipelineMode { get; set; } = PipelineMode.Auto;
    public PerformanceHudMode PerformanceHud { get; set; } = PerformanceHudMode.Off;
    public MetricsMode MetricsCollection { get; set; } = MetricsMode.Lightweight;
    public int MaximumFrameLatency { get; set; } = 1;
    public PresentMode PresentMode { get; set; } = PresentMode.Immediate;
    public CapturePriority CaptureThreadPriority { get; set; } = CapturePriority.AboveNormal;
    public bool EnableMmcss { get; set; } = true;
    public bool CsvEnabled { get; set; }
    public int CsvIntervalFrames { get; set; } = 1;
    public bool LatencyTestMode { get; set; }
    public bool AlwaysOnTop { get; set; } = true;
    public int UiHotkeyKey { get; set; } = (int)Keys.F8;
    public uint UiHotkeyModifiers { get; set; }
}
internal static class SettingsStore
{
    private static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "YarrOverlay.settings.json");
    private static readonly object SaveSync = new();
    private static readonly System.Threading.Timer SaveTimer = new(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    private static string? _pendingJson;

    public static AppSettings Load()
    {
        if (!File.Exists(Path)) return new AppSettings();
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path)) ?? new AppSettings(); }
        catch (Exception ex) { Logger.Error($"Settings load failed: {ex.Message}"); return new AppSettings(); }
    }
    public static void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        lock (SaveSync)
        {
            _pendingJson = json;
            SaveTimer.Change(350, Timeout.Infinite);
        }
    }

    public static void Flush()
    {
        string? json;
        lock (SaveSync)
        {
            json = _pendingJson;
            _pendingJson = null;
        }

        if (json is null) return;
        try { File.WriteAllText(Path, json); }
        catch (Exception ex) { Logger.Error($"Settings save failed: {ex.Message}"); }
    }
}
