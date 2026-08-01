using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace Overlay;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        try
        {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
        }
        catch
        {
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        using var state = new AppState();
        using var settingsWindow = new SettingsWindow(state);
        if (Array.Exists(Environment.GetCommandLineArgs(), a => string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase)))
            settingsWindow.Shown += (_, _) => state.Start();
        Application.Run(settingsWindow);
    }
}
