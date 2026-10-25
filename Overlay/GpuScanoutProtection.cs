using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Overlay;

internal static class GpuScanoutProtection
{
    private const string HelperArgument = "--gpu-scanout-helper";
    private const string AdministratorsSid = "*S-1-5-32-544";
    private static string? _helperBackupDirectory;

    public static string SystemDllPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "nvapi64.dll");

    public static string BackupDirectory => _helperBackupDirectory
        ?? Path.Combine(GetExecutableDirectory(), "Backup");
    public static string BackupDllPath => Path.Combine(BackupDirectory, "nvapi64.dll");
    public static bool IsActive => !File.Exists(SystemDllPath) && File.Exists(BackupDllPath);

    internal readonly record struct ProbeResult(bool FilePresent, bool Loadable, int ErrorCode);

    private static string GetExecutableDirectory()
    {
        var executable = Environment.ProcessPath;
        var directory = string.IsNullOrWhiteSpace(executable) ? null : Path.GetDirectoryName(executable);
        return string.IsNullOrWhiteSpace(directory) ? AppContext.BaseDirectory : directory;
    }

    public static ProbeResult ProbeLoadLibrary()
    {
        var filePresent = File.Exists(SystemDllPath);
        Marshal.SetLastPInvokeError(0);
        var module = LoadLibrary(SystemDllPath);
        var error = module == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        if (module != IntPtr.Zero) FreeLibrary(module);
        return new(filePresent, module != IntPtr.Zero, error);
    }

    public static void SetEnabled(bool enabled)
    {
        AppPaths.EnsureDataDirectory();
        var resultPath = Path.Combine(AppPaths.DataDirectory, $"gpu-scanout-{Guid.NewGuid():N}.result");
        try
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("Current executable path is unavailable.");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            start.ArgumentList.Add(HelperArgument);
            start.ArgumentList.Add(enabled ? "enable" : "disable");
            start.ArgumentList.Add(resultPath);
            start.ArgumentList.Add(BackupDirectory);

            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("The elevated protection helper did not start.");
            if (!process.WaitForExit(120_000))
                throw new TimeoutException("The elevated protection helper did not finish within two minutes.");

            var result = File.Exists(resultPath) ? File.ReadAllText(resultPath).Trim() : "No result was returned.";
            if (process.ExitCode != 0)
                throw new InvalidOperationException(result);
            if (enabled != IsActive)
                throw new InvalidOperationException($"The requested state was not verified. {result}");

            Logger.Info($"GPU scanout capture protection {(enabled ? "enabled" : "disabled")}: {result}");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("The administrator prompt was cancelled.", ex);
        }
        finally
        {
            try { if (File.Exists(resultPath)) File.Delete(resultPath); } catch { }
        }
    }

    public static bool TryRunHelper(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length < 2 || !string.Equals(args[1], HelperArgument, StringComparison.OrdinalIgnoreCase))
            return false;

        var resultPath = args.Length >= 4 ? args[3] : "";
        try
        {
            if (args.Length != 5 ||
                (!args[2].Equals("enable", StringComparison.OrdinalIgnoreCase) &&
                 !args[2].Equals("disable", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Invalid GPU scanout helper arguments.");
            if (string.IsNullOrWhiteSpace(resultPath))
                throw new ArgumentException("The helper result path is missing.");
            _helperBackupDirectory = Path.GetFullPath(args[4]);

            var message = args[2].Equals("enable", StringComparison.OrdinalIgnoreCase)
                ? EnableElevated()
                : DisableElevated();
            WriteResult(resultPath, message);
        }
        catch (Exception ex)
        {
            exitCode = 1;
            WriteResult(resultPath, $"{ex.GetType().Name}: {ex.Message}");
        }
        return true;
    }

    private static string EnableElevated()
    {
        var source = SystemDllPath;
        var backup = BackupDllPath;
        Directory.CreateDirectory(BackupDirectory);
        if (!File.Exists(source))
        {
            if (File.Exists(backup)) return "The NVIDIA DLL is already backed up and absent from System32.";
            throw new FileNotFoundException("The NVIDIA NVAPI DLL was not found in System32.", source);
        }

        EnsureSameVolume(source, backup);
        if (File.Exists(backup)) File.Delete(backup);
        CreateHardLinkChecked(backup, source);
        ForceDelete(source);
        return "Linked nvapi64.dll into Backup and removed the System32 link.";
    }

    private static string DisableElevated()
    {
        var source = SystemDllPath;
        var backup = BackupDllPath;
        if (!File.Exists(backup))
            throw new FileNotFoundException("The NVAPI backup was not found.", backup);
        if (File.Exists(source)) return "nvapi64.dll is already present in System32.";

        var restoreSource = FindDriverStoreSource(backup) ?? backup;
        EnsureSameVolume(restoreSource, source);
        CreateHardLinkChecked(source, restoreSource);
        return restoreSource.Equals(backup, StringComparison.OrdinalIgnoreCase)
            ? "Restored the System32 link from Backup."
            : "Restored the original DriverStore link to System32.";
    }

    private static string? FindDriverStoreSource(string referencePath)
    {
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var driverStore = Path.Combine(systemDirectory, "DriverStore", "FileRepository");
        if (!Directory.Exists(driverStore)) return null;

        var reference = new FileInfo(referencePath);
        var referenceVersion = FileVersionInfo.GetVersionInfo(referencePath).FileVersion;
        try
        {
            return Directory.EnumerateFiles(driverStore, "nvapi64.dll", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .Where(file => file.Length == reference.Length &&
                    string.Equals(
                        FileVersionInfo.GetVersionInfo(file.FullName).FileVersion,
                        referenceVersion,
                        StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Logger.Info($"DriverStore NVAPI lookup unavailable; using Backup link: {ex.Message}");
            return null;
        }
    }

    private static void EnsureSameVolume(string existingPath, string newPath)
    {
        var existingRoot = Path.GetPathRoot(Path.GetFullPath(existingPath));
        var newRoot = Path.GetPathRoot(Path.GetFullPath(newPath));
        if (!string.Equals(existingRoot, newRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Backup and System32 must be on the same NTFS volume.");
    }

    private static void CreateHardLinkChecked(string newPath, string existingPath)
    {
        if (CreateHardLink(newPath, existingPath, IntPtr.Zero)) return;
        throw new Win32Exception(Marshal.GetLastWin32Error(),
            $"Could not create the NVAPI hard link: {newPath}");
    }

    private static void ForceDelete(string path)
    {
        var scriptPath = Path.Combine(AppPaths.DataDirectory, $"delete-nvapi-{Guid.NewGuid():N}.cmd");
        var escapedPath = path.Replace("\"", "\"\"");
        var script = string.Join("\r\n",
            "@echo off",
            $"takeown /f \"{escapedPath}\"",
            "if errorlevel 1 exit /b %errorlevel%",
            $"icacls \"{escapedPath}\" /grant {AdministratorsSid}:F",
            "if errorlevel 1 exit /b %errorlevel%",
            $"del /f /q \"{escapedPath}\"",
            $"if exist \"{escapedPath}\" exit /b 5");
        Exception? commandFailure = null;
        try
        {
            File.WriteAllText(scriptPath, script, System.Text.Encoding.ASCII);
            try { RunSystemTool("cmd.exe", "/d", "/q", "/c", scriptPath); }
            catch (Exception ex) { commandFailure = ex; }
        }
        finally
        {
            try { if (File.Exists(scriptPath)) File.Delete(scriptPath); } catch { }
        }
        if (!File.Exists(path)) return;

        var moduleUsers = FindLockingProcesses(path);
        throw new IOException(
            $"The System32 file is locked by: {moduleUsers}. " +
            $"Close or restart those processes, then retry.{(commandFailure is null ? "" : $" Delete error: {commandFailure.Message}")}");
    }

    private static string FindLockingProcesses(string path)
    {
        const int ErrorMoreData = 234;
        var sessionKey = new StringBuilder(33);
        var startResult = RmStartSession(out var session, 0, sessionKey);
        if (startResult != 0) return $"unknown (Restart Manager error {startResult})";

        try
        {
            var resources = new[] { path };
            var registerResult = RmRegisterResources(session, 1, resources, 0, null, 0, null);
            if (registerResult != 0) return $"unknown (Restart Manager error {registerResult})";

            uint needed = 0;
            uint count = 0;
            uint rebootReasons = 0;
            var listResult = RmGetList(session, out needed, ref count, null, ref rebootReasons);
            if (listResult == 0 && needed == 0) return "an unidentified process";
            if (listResult != ErrorMoreData) return $"unknown (Restart Manager error {listResult})";

            var processes = new RmProcessInfo[needed];
            count = needed;
            listResult = RmGetList(session, out needed, ref count, processes, ref rebootReasons);
            if (listResult != 0) return $"unknown (Restart Manager error {listResult})";

            var blockers = processes.Take((int)count)
                .Select(info =>
                {
                    var name = string.IsNullOrWhiteSpace(info.ApplicationName)
                        ? TryGetProcessName(info.Process.ProcessId)
                        : info.ApplicationName;
                    return $"{name} (PID {info.Process.ProcessId})";
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return blockers.Length == 0 ? "an unidentified process" : string.Join(", ", blockers);
        }
        finally
        {
            RmEndSession(session);
        }
    }

    private static string TryGetProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return $"{process.ProcessName}.exe";
        }
        catch
        {
            return "unknown process";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    private enum RmAppType
    {
        Unknown = 0,
        MainWindow = 1,
        OtherWindow = 2,
        Service = 3,
        Explorer = 4,
        Console = 5,
        Critical = 1000
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string ApplicationName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string ServiceShortName;

        public RmAppType ApplicationType;
        public uint AppStatus;
        public uint TerminalSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint sessionHandle, int sessionFlags, StringBuilder sessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint sessionHandle,
        uint fileCount,
        string[] fileNames,
        uint applicationCount,
        RmUniqueProcess[]? applications,
        uint serviceCount,
        string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint sessionHandle,
        out uint processInfoNeeded,
        ref uint processInfoCount,
        [In, Out] RmProcessInfo[]? affectedApps,
        ref uint rebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);

    private static string RunSystemTool(string fileName, params string[] arguments)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), fileName);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        var combined = $"{error.Trim()} {output.Trim()}".Trim();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} failed ({process.ExitCode.ToString(CultureInfo.InvariantCulture)}): {combined}".Trim());
        return combined;
    }

    private static void WriteResult(string path, string message)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
            File.WriteAllText(path, message);
        }
        catch { }
    }

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string fileName);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);
}
