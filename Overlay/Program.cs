using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace Overlay;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        WaitForRestartParent(Environment.GetCommandLineArgs());
        using var singleInstance = new Mutex(true, @"Local\Overlay.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            NativeMethods.PostMessage(new IntPtr(NativeMethods.HwndBroadcast), NativeMethods.WmShowSettings, IntPtr.Zero, IntPtr.Zero);
            return;
        }

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

    private static void WaitForRestartParent(string[] args)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, "--restart-wait", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out var processId)) return;
        try
        {
            using var process = Process.GetProcessById(processId);
            process.WaitForExit(15000);
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }
}
