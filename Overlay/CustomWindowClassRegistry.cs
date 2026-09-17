using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Overlay;

// WinForms treats CreateParams.ClassName as an existing base class and looks it
// up with a null HINSTANCE. Registering it as CS_GLOBALCLASS makes that lookup
// reliable; WinForms then derives its managed window class from this base.
internal static class CustomWindowClassRegistry
{
    private const uint CsGlobalClass = 0x4000;
    private static readonly object Sync = new();
    private static readonly HashSet<string> Registered = new(StringComparer.Ordinal);
    private static readonly WindowProcedure Procedure = WindowProc;

    public static string EnsureRegistered(string className)
    {
        lock (Sync)
        {
            if (Registered.Contains(className)) return className;
            var data = new WindowClassEx
            {
                Size = (uint)Marshal.SizeOf<WindowClassEx>(),
                Style = CsGlobalClass,
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(Procedure),
                Instance = GetModuleHandle(null),
                ClassName = className
            };
            if (RegisterClassEx(ref data) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not register window class '{className}'. Choose a unique name.");
            Registered.Add(className);
            return className;
        }
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam) =>
        DefWindowProc(hwnd, message, wParam, lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
