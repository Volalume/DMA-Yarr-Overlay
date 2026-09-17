using System;
using System.IO;

namespace Overlay;

internal static class AppPaths
{
    public static readonly string DataDirectory = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Overlay");

    // Read-only migration source for installations made before the product rename.
    public static readonly string PreviousSettingsFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YarrOverlay",
        "settings.json");

    public static string SettingsFile => System.IO.Path.Combine(DataDirectory, "settings.json");

    public static string LogFile => System.IO.Path.Combine(DataDirectory, "Overlay.log");

    public static string LegacySettingsFile => System.IO.Path.Combine(AppContext.BaseDirectory, "Overlay.settings.json");

    public static void EnsureDataDirectory() => Directory.CreateDirectory(DataDirectory);
}
