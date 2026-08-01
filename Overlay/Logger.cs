using System;
using System.IO;

namespace Overlay;

internal static class Logger
{
    private static readonly object Sync = new();
    private static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "YarrOverlay.log");
    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message) => Write("ERROR", message);
    private static void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:O} [{level}] {message}";
        lock (Sync) File.AppendAllText(Path, line + Environment.NewLine);
        System.Diagnostics.Debug.WriteLine(line);
    }
}
