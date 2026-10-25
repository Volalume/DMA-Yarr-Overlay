using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Overlay;

internal static class GpuScanoutProtection
{
    private const string HelperArgument = "--gpu-scanout-helper";
    private const string AdministratorsSid = "*S-1-5-32-544";
    private static string? _helperBackupDirectory;
    private static string? _watchdogSignalPath;

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
        if (!enabled && RequestWatchdogRestore("manual", TimeSpan.FromSeconds(10)))
        {
            Logger.Info("GPU scanout capture protection disabled by the elevated restore watchdog.");
            return;
        }

        AppPaths.EnsureDataDirectory();
        var resultPath = Path.Combine(AppPaths.DataDirectory, $"gpu-scanout-{Guid.NewGuid():N}.result");
        var signalPath = enabled
            ? Path.Combine(AppPaths.DataDirectory, $"gpu-scanout-{Environment.ProcessId}.restore")
            : "-";
        var completed = false;
        try
        {
            if (enabled)
            {
                try { if (File.Exists(signalPath)) File.Delete(signalPath); } catch { }
                _watchdogSignalPath = signalPath;
            }

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
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add(signalPath);

            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("The elevated protection helper did not start.");
            var result = WaitForHelperResult(process, resultPath, TimeSpan.FromMinutes(2));
            if (result.StartsWith("ERROR|", StringComparison.Ordinal))
                throw new InvalidOperationException(result[6..]);
            if (!result.StartsWith("OK|", StringComparison.Ordinal))
                throw new InvalidOperationException($"Invalid helper response: {result}");
            result = result[3..];
            if (enabled != IsActive)
                throw new InvalidOperationException($"The requested state was not verified. {result}");

            completed = true;
            Logger.Info($"GPU scanout capture protection {(enabled ? "enabled" : "disabled")}: {result}");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("The administrator prompt was cancelled.", ex);
        }
        finally
        {
            try { if (File.Exists(resultPath)) File.Delete(resultPath); } catch { }
            if (enabled && !completed)
            {
                try { if (File.Exists(signalPath)) File.Delete(signalPath); } catch { }
                _watchdogSignalPath = null;
            }
        }
    }

    public static void RestoreBeforeExit()
    {
        if (!IsActive) return;
        if (!RequestWatchdogRestore("exit", TimeSpan.FromSeconds(8)))
            Logger.Error("The GPU scanout restore watchdog did not confirm restoration before exit.");
    }

    private static bool RequestWatchdogRestore(string reason, TimeSpan timeout)
    {
        var signalPath = _watchdogSignalPath;
        if (!IsActive)
        {
            _watchdogSignalPath = null;
            return true;
        }
        if (string.IsNullOrWhiteSpace(signalPath)) return false;

        try { File.WriteAllText(signalPath, reason); }
        catch (Exception ex)
        {
            Logger.Error($"Could not signal the GPU scanout restore watchdog: {ex.Message}");
            return false;
        }

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (!IsActive)
            {
                _watchdogSignalPath = null;
                return true;
            }
            Thread.Sleep(50);
        }
        return !IsActive;
    }

    private static string WaitForHelperResult(Process process, string resultPath, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                if (File.Exists(resultPath))
                {
                    var result = File.ReadAllText(resultPath).Trim();
                    if (!string.IsNullOrWhiteSpace(result)) return result;
                }
            }
            catch (IOException)
            {
                // The helper may still be completing its atomic write.
            }

            if (process.HasExited)
                throw new InvalidOperationException($"The elevated helper exited without a result (code {process.ExitCode}).");
            Thread.Sleep(50);
        }
        throw new TimeoutException("The elevated protection helper did not respond within two minutes.");
    }

    public static bool TryRunHelper(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length < 2 || !string.Equals(args[1], HelperArgument, StringComparison.OrdinalIgnoreCase))
            return false;

        var resultPath = args.Length >= 4 ? args[3] : "";
        try
        {
            if (args.Length != 7 ||
                (!args[2].Equals("enable", StringComparison.OrdinalIgnoreCase) &&
                 !args[2].Equals("disable", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Invalid GPU scanout helper arguments.");
            if (string.IsNullOrWhiteSpace(resultPath))
                throw new ArgumentException("The helper result path is missing.");
            _helperBackupDirectory = Path.GetFullPath(args[4]);
            if (!int.TryParse(args[5], NumberStyles.None, CultureInfo.InvariantCulture, out var parentProcessId) || parentProcessId <= 0)
                throw new ArgumentException("The parent process ID is invalid.");

            var enable = args[2].Equals("enable", StringComparison.OrdinalIgnoreCase);
            var message = enable
                ? EnableElevated()
                : DisableElevated();
            WriteResult(resultPath, $"OK|{message}");
            if (enable) WatchParentAndRestore(parentProcessId, args[6]);
        }
        catch (Exception ex)
        {
            exitCode = 1;
            WriteResult(resultPath, $"ERROR|{ex.GetType().Name}: {ex.Message}");
        }
        return true;
    }

    private static void WatchParentAndRestore(int parentProcessId, string signalPath)
    {
        // The main app uses the normal shutdown priority. Keeping this helper at a
        // lower priority lets it restore NVAPI after the app exits during shutdown.
        SetProcessShutdownParameters(0x100, 0);
        var reason = "process-exit";
        try
        {
            Process? parent = null;
            try { parent = Process.GetProcessById(parentProcessId); }
            catch (ArgumentException) { }

            using (parent)
            {
                while (parent is { HasExited: false })
                {
                    if (File.Exists(SystemDllPath)) return;
                    if (File.Exists(signalPath))
                    {
                        try { reason = File.ReadAllText(signalPath).Trim(); } catch { }
                        break;
                    }
                    parent.WaitForExit(250);
                }
            }

            if (File.Exists(SystemDllPath)) return;
            var message = DisableElevated();
            Logger.Info($"Automatic NVAPI restore completed ({reason}): {message}");
            if (!string.Equals(reason, "manual", StringComparison.OrdinalIgnoreCase))
                ShowRestoreNotification(true, "NVIDIA NVAPI was restored automatically.");
        }
        catch (Exception ex)
        {
            Logger.Error($"Automatic NVAPI restore failed: {ex}");
            ShowRestoreNotification(false, $"Automatic NVAPI restore failed: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(signalPath)) File.Delete(signalPath); } catch { }
        }
    }

    private static void ShowRestoreNotification(bool success, string message)
    {
        try
        {
            if (success) SystemSounds.Asterisk.Play();
            else SystemSounds.Hand.Play();
            using var customIcon = LoadNotificationIcon();
            if (customIcon is not null && ShowNativeBalloon(customIcon, success, message)) return;

            using var notification = new NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Shield,
                Visible = true,
                BalloonTipTitle = success ? "YarrOverlay protection restored" : "YarrOverlay restore failed",
                BalloonTipText = message,
                BalloonTipIcon = ToolTipIcon.None
            };
            notification.ShowBalloonTip(3000);
            Thread.Sleep(1200);
        }
        catch
        {
            MessageBeep(success ? 0x40u : 0x10u);
        }
    }

    private static bool ShowNativeBalloon(System.Drawing.Icon icon, bool success, string message)
    {
        const uint NotifyIconAdd = 0x00000000;
        const uint NotifyIconModify = 0x00000001;
        const uint NotifyIconDelete = 0x00000002;
        const uint NotifyIconSetVersion = 0x00000004;
        const uint NotifyIconVersion4 = 4;
        const uint NotifyIconFlagMessage = 0x00000001;
        const uint NotifyIconFlagIcon = 0x00000002;
        const uint NotifyIconFlagTip = 0x00000004;
        const uint NotifyIconFlagInfo = 0x00000010;
        const uint NotifyInfoUser = 0x00000004;
        const uint NotifyInfoLargeIcon = 0x00000020;

        using var window = new NotificationMessageWindow();
        var data = new NativeNotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NativeNotifyIconData>(),
            WindowHandle = window.Handle,
            Id = 1,
            Flags = NotifyIconFlagMessage | NotifyIconFlagIcon | NotifyIconFlagTip,
            CallbackMessage = 0x8000 + 77,
            IconHandle = System.Drawing.SystemIcons.Shield.Handle,
            Tip = "YarrOverlay",
            Info = "",
            InfoTitle = ""
        };
        if (!ShellNotifyIcon(NotifyIconAdd, ref data)) return false;

        try
        {
            data.TimeoutOrVersion = NotifyIconVersion4;
            ShellNotifyIcon(NotifyIconSetVersion, ref data);
            data.Flags = NotifyIconFlagInfo;
            data.Info = message;
            data.InfoTitle = success ? "YarrOverlay protection restored" : "YarrOverlay restore failed";
            data.InfoFlags = NotifyInfoUser | NotifyInfoLargeIcon;
            data.BalloonIconHandle = icon.Handle;
            data.TimeoutOrVersion = 3000;
            if (!ShellNotifyIcon(NotifyIconModify, ref data)) return false;
            Thread.Sleep(1800);
            return true;
        }
        finally
        {
            data.Flags = 0;
            ShellNotifyIcon(NotifyIconDelete, ref data);
        }
    }

    private static System.Drawing.Icon? LoadNotificationIcon()
    {
        try
        {
            using var stream = typeof(GpuScanoutProtection).Assembly
                .GetManifestResourceStream("Overlay.NotificationIcon.ico");
            if (stream is null) return null;
            using var icon = new System.Drawing.Icon(stream);
            return (System.Drawing.Icon)icon.Clone();
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeNotifyIconData
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr IconHandle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint TimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid ItemGuid;
        public IntPtr BalloonIconHandle;
    }

    private sealed class NotificationMessageWindow : NativeWindow, IDisposable
    {
        public NotificationMessageWindow()
        {
            CreateHandle(new CreateParams { Caption = "YarrOverlay Notification" });
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
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
        var reusedBackup = File.Exists(backup) && FilesMatch(backup, source);
        if (!reusedBackup)
        {
            if (File.Exists(backup)) DeleteBackupForReplacement(backup);
            CreateHardLinkChecked(backup, source);
        }
        ForceDelete(source);
        return reusedBackup
            ? "Reused the existing NVAPI backup and removed the System32 link."
            : "Linked nvapi64.dll into Backup and removed the System32 link.";
    }

    private static bool FilesMatch(string firstPath, string secondPath)
    {
        try
        {
            var first = new FileInfo(firstPath);
            var second = new FileInfo(secondPath);
            if (first.Length != second.Length) return false;
            return string.Equals(
                FileVersionInfo.GetVersionInfo(firstPath).FileVersion,
                FileVersionInfo.GetVersionInfo(secondPath).FileVersion,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void DeleteBackupForReplacement(string path)
    {
        try
        {
            File.Delete(path);
            return;
        }
        catch (UnauthorizedAccessException)
        {
            // A restored DriverStore hard link may retain its protected ACL.
        }

        RunSystemTool("takeown.exe", "/f", path);
        RunSystemTool("icacls.exe", path, "/grant", $"{AdministratorsSid}:F");
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessShutdownParameters(uint level, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint type);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NativeNotifyIconData data);
}
