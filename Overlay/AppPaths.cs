using System;
using System.IO;

namespace Overlay;

internal static class AppPaths
{
    public static readonly string DataDirectory = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YarrOverlay");

    public static string SettingsFile => System.IO.Path.Combine(DataDirectory, "settings.json");

    public static string LogFile => System.IO.Path.Combine(DataDirectory, "YarrOverlay.log");

    public static string LegacySettingsFile => System.IO.Path.Combine(AppContext.BaseDirectory, "YarrOverlay.settings.json");

    public static void EnsureDataDirectory() => Directory.CreateDirectory(DataDirectory);
}
