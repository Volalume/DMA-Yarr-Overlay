using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

var useDefaultProcessFilter = args.Length == 0;
var filter = useDefaultProcessFilter ? "Overlay" : string.Join(' ', args);
var matches = new List<WindowRecord>();

NativeMethods.EnumWindows((hwnd, _) =>
{
    NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
    if (processId == 0) return true;

    try
    {
        using var process = Process.GetProcessById((int)processId);
        var title = ReadText(hwnd);
        var className = ReadClass(hwnd);
        var path = TryGetPath(process);
        var processName = process.ProcessName;
        var matchesFilter = useDefaultProcessFilter
            ? string.Equals(processName, filter, StringComparison.OrdinalIgnoreCase)
            : Contains(processName, filter) || Contains(title, filter) || Contains(className, filter) || Contains(path, filter);
        if (!matchesFilter)
            return true;

        var affinityAvailable = NativeMethods.GetWindowDisplayAffinity(hwnd, out var affinity);
        var version = TryGetVersion(path);
        matches.Add(new WindowRecord(
            process.Id,
            processName,
            path,
            version?.FileDescription ?? "",
            version?.ProductName ?? "",
            title,
            className,
            NativeMethods.IsWindowVisible(hwnd),
            affinityAvailable,
            affinity,
            hwnd));
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
    {
        // The process may exit or deny access while windows are enumerated.
    }

    return true;
}, IntPtr.Zero);

Console.WriteLine("WINDOW IDENTITY DIAGNOSTICS");
Console.WriteLine($"Filter : {filter}");
Console.WriteLine(new string('=', 72));

if (matches.Count == 0)
{
    Console.WriteLine("No matching top-level windows were found.");
    Console.WriteLine("Start the program first, or pass another process/title/class filter.");
}
else
{
    foreach (var item in matches.OrderBy(x => x.ProcessId).ThenBy(x => x.Handle))
    {
        Console.WriteLine($"PID / process : {item.ProcessId} / {item.ProcessName}");
        Console.WriteLine($"EXE path      : {Value(item.Path)}");
        Console.WriteLine($"File desc.    : {Value(item.FileDescription)}");
        Console.WriteLine($"Product name  : {Value(item.ProductName)}");
        Console.WriteLine($"Window title  : {Value(item.WindowTitle)}");
        Console.WriteLine($"Window class  : {Value(item.WindowClass)}");
        Console.WriteLine($"HWND          : 0x{item.Handle.ToInt64():X}");
        Console.WriteLine($"Visible       : {item.Visible}");
        Console.WriteLine($"Affinity      : {(item.AffinityAvailable ? $"{FormatAffinity(item.DisplayAffinity)} (0x{item.DisplayAffinity:X})" : "Unavailable")}");
        Console.WriteLine(new string('-', 72));
    }
}

Console.WriteLine("Press Enter to exit.");
Console.ReadLine();

static bool Contains(string? value, string filter) =>
    !string.IsNullOrWhiteSpace(value) && value.Contains(filter, StringComparison.OrdinalIgnoreCase);

static string ReadText(IntPtr hwnd)
{
    var length = NativeMethods.GetWindowTextLength(hwnd);
    var buffer = new StringBuilder(length + 1);
    NativeMethods.GetWindowText(hwnd, buffer, buffer.Capacity);
    return buffer.ToString();
}

static string ReadClass(IntPtr hwnd)
{
    var buffer = new StringBuilder(512);
    return NativeMethods.GetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
}

static string TryGetPath(Process process)
{
    try { return process.MainModule?.FileName ?? ""; }
    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { return ""; }
}

static FileVersionInfo? TryGetVersion(string path)
{
    try { return string.IsNullOrWhiteSpace(path) ? null : FileVersionInfo.GetVersionInfo(path); }
    catch (Exception ex) when (ex is FileNotFoundException or Win32Exception) { return null; }
}

static string Value(string value) => string.IsNullOrWhiteSpace(value) ? "(empty)" : value;

static string FormatAffinity(uint affinity) => affinity switch
{
    0x00 => "OFF",
    0x01 => "MONITOR",
    0x11 => "EXCLUDE_FROM_CAPTURE",
    _ => "UNKNOWN"
};

internal sealed record WindowRecord(
    int ProcessId,
    string ProcessName,
    string Path,
    string FileDescription,
    string ProductName,
    string WindowTitle,
    string WindowClass,
    bool Visible,
    bool AffinityAvailable,
    uint DisplayAffinity,
    IntPtr Handle);

internal static class NativeMethods
{
    internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
}
