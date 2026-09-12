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
internal enum AntiCaptureMode { Off, Black, Exclude }
internal enum GpuProtectionMode { Off, HardwareRequired }
internal enum CaptureProtectionLevel { Off, Software, Hardware, Kernel }
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
    public PerformanceHudMode PerformanceHud { get; set; } = PerformanceHudMode.Basic;
    public MetricsMode MetricsCollection { get; set; } = MetricsMode.Lightweight;
    public int MaximumFrameLatency { get; set; } = 1;
    public PresentMode PresentMode { get; set; } = PresentMode.Immediate;
    public CapturePriority CaptureThreadPriority { get; set; } = CapturePriority.AboveNormal;
    public bool EnableMmcss { get; set; } = true;
    public bool CsvEnabled { get; set; }
    public int CsvIntervalFrames { get; set; } = 1;
    public bool LatencyTestMode { get; set; }
    public bool AlwaysOnTop { get; set; } = true;
    public AntiCaptureMode AntiCapture { get; set; } = AntiCaptureMode.Off;
    public GpuProtectionMode GpuProtection { get; set; } = GpuProtectionMode.Off;
    public bool HardwareMonitorOnly { get; set; }
    public CaptureProtectionLevel ProtectionLevel { get; set; } = CaptureProtectionLevel.Off;
    public int UiHotkeyKey { get; set; } = (int)Keys.Insert;
    public uint UiHotkeyModifiers { get; set; }
}
internal static class SettingsStore
{
    private static readonly object SaveSync = new();
    private static readonly System.Threading.Timer SaveTimer = new(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    private static string? _pendingJson;

    public static AppSettings Load()
    {
        var path = File.Exists(AppPaths.SettingsFile) ? AppPaths.SettingsFile : AppPaths.LegacySettingsFile;
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            if (!string.Equals(path, AppPaths.SettingsFile, StringComparison.OrdinalIgnoreCase))
            {
                AppPaths.EnsureDataDirectory();
                File.WriteAllText(AppPaths.SettingsFile, json);
            }
            return settings;
        }
        catch (Exception ex) { Logger.Error($"Settings load failed: {ex.Message}"); return new AppSettings(); }
    }
    public static void Save(AppSettings settings)
    {
        var json = Serialize(settings);
        lock (SaveSync)
        {
            _pendingJson = json;
            SaveTimer.Change(350, Timeout.Infinite);
        }
    }

    public static void SaveNow(AppSettings settings)
    {
        var json = Serialize(settings);
        lock (SaveSync)
        {
            SaveTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _pendingJson = null;
            if (Write(json))
                Logger.Info($"Settings saved immediately: path='{AppPaths.SettingsFile}', protection={settings.ProtectionLevel}, softwareResult={settings.AntiCapture}, gpu={settings.GpuProtection}, monitorOnly={settings.HardwareMonitorOnly}");
        }
    }

    public static void Flush()
    {
        lock (SaveSync)
        {
            var json = _pendingJson;
            _pendingJson = null;
            if (json is not null) Write(json);
        }
    }

    private static string Serialize(AppSettings settings) =>
        JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });

    private static bool Write(string json)
    {
        try
        {
            AppPaths.EnsureDataDirectory();
            File.WriteAllText(AppPaths.SettingsFile, json);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Settings save failed: path='{AppPaths.SettingsFile}', error={ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
