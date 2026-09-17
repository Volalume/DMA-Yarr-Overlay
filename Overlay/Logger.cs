using System;
using System.IO;

namespace Overlay;

internal static class Logger
{
    private static readonly object Sync = new();
    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Debug(string message)
    {
        var line = $"{DateTimeOffset.Now:O} [DEBUG] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        if (string.Equals(Environment.GetEnvironmentVariable("OVERLAY_VERBOSE_LOG"), "1", StringComparison.Ordinal))
            Write("DEBUG", message);
    }
    private static void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:O} [{level}] {message}";
        try
        {
            lock (Sync)
            {
                AppPaths.EnsureDataDirectory();
                File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine);
            }
        }
        catch
        {
        }
        System.Diagnostics.Debug.WriteLine(line);
    }
}
