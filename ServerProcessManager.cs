using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ZomboidManager;

public class ServerProcessManager
{
    private Process? _managedProcess;
    private string? _serverWorkingDirectory;
    private string? _lastStartBatPath;
    private readonly HashSet<int> _launchedConsolePids = new();
    private readonly object _sync = new();

    public event Action<string>? LogReceived;
    public event Action? ProcessExited;

    public bool IsManagedProcessRunning
    {
        get
        {
            lock (_sync)
            {
                return _managedProcess is { HasExited: false };
            }
        }
    }

    public int? ManagedProcessId
    {
        get
        {
            lock (_sync)
            {
                return _managedProcess is { HasExited: false } ? _managedProcess.Id : null;
            }
        }
    }

    /// <summary>True when at least one Java process looks like a Project Zomboid dedicated server.</summary>
    public bool IsPzServerJavaRunning()
    {
        foreach (Process java in EnumeratePzServerJavaProcesses())
        {
            java.Dispose();
            return true;
        }

        return false;
    }

    public Process StartServer(string batFilePath, bool embedConsole = true)
    {
        if (!File.Exists(batFilePath))
            throw new FileNotFoundException($"Start file not found: {batFilePath}", batFilePath);

        StopManagedProcess();

        string workingDir = Path.GetDirectoryName(batFilePath) ?? string.Empty;

        // Visible console: PZ/Java barely flush logs to redirected pipes, so users saw an empty
        // live console and thought Start did nothing. ConsoleGuard keeps the manager open.
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/s /c \"\"{batFilePath}\"\"",
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = false
        };

        Process? process = Process.Start(startInfo);
        if (process is null)
            throw new InvalidOperationException($"Failed to start process: {batFilePath}");

        ConsoleGuard.DetachFromConsole();

        lock (_sync)
        {
            _managedProcess = process;
            _serverWorkingDirectory = workingDir;
            _lastStartBatPath = batFilePath;
            _launchedConsolePids.Add(process.Id);
        }

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            try
            {
                bool isCurrent;
                lock (_sync)
                {
                    // Ignore exits from a process we already replaced/stopped so a late
                    // Exited callback cannot clear boot/Discord state for a newer launch.
                    isCurrent = ReferenceEquals(_managedProcess, process);
                    if (isCurrent)
                        _managedProcess = null;
                    _launchedConsolePids.Remove(process.Id);
                }

                if (!isCurrent)
                    return;

                LogReceived?.Invoke("[server console process exited]");
                ProcessExited?.Invoke();
            }
            catch (Exception ex)
            {
                try { LogReceived?.Invoke("ProcessExited handler error: " + ex.Message); }
                catch { /* ignore */ }
            }
        };

        _ = embedConsole;
        LogReceived?.Invoke("Server .bat launched in a separate console window.");
        LogReceived?.Invoke($"File: {batFilePath}");

        return process;
    }

    public void StopManagedProcess()
    {
        Process? process;
        lock (_sync)
        {
            process = _managedProcess;
            _managedProcess = null;
            if (process is not null)
                _launchedConsolePids.Remove(process.Id);
        }

        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                RunTaskKill(process.Id);
                process.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            try { LogReceived?.Invoke("StopManagedProcess failed: " + ex.Message); }
            catch { /* ignore */ }
        }
        finally
        {
            try
            {
                process.Dispose();
            }
            catch
            {
                // ignore
            }

            ConsoleGuard.DetachFromConsole();
        }
    }

    public List<string> KillServerTree()
    {
        var messages = new List<string>();
        string? serverDir;
        string? batPath;
        lock (_sync)
        {
            serverDir = _serverWorkingDirectory;
            batPath = _lastStartBatPath;
        }

        StopManagedProcess();

        List<Process> pzJava = EnumeratePzServerJavaProcesses(serverDir, batPath).ToList();
        if (pzJava.Count == 0)
        {
            messages.Add("No running Project Zomboid Java processes found.");
        }
        else
        {
            foreach (Process javaProcess in pzJava)
            {
                try
                {
                    int pid = javaProcess.Id;
                    RunTaskKill(pid);
                    messages.Add($"Terminated Project Zomboid Java process (PID {pid}).");
                }
                catch (Exception ex)
                {
                    messages.Add($"Could not terminate PZ Java process (PID {javaProcess.Id}): {ex.Message}");
                }
                finally
                {
                    javaProcess.Dispose();
                }
            }
        }

        messages.AddRange(CloseOrphanServerConsoles());
        ConsoleGuard.DetachFromConsole();
        return messages;
    }

    /// <summary>
    /// Closes leftover manager-launched cmd consoles, or cmd processes whose command line
    /// clearly references this server's StartServer .bat. Title-only matching is not used.
    /// </summary>
    public List<string> CloseOrphanServerConsoles()
    {
        var messages = new List<string>();
        HashSet<int> launched;
        string? batPath;
        string? serverDir;
        lock (_sync)
        {
            launched = new HashSet<int>(_launchedConsolePids);
            batPath = _lastStartBatPath;
            serverDir = _serverWorkingDirectory;
        }

        try
        {
            foreach (Process cmd in Process.GetProcessesByName("cmd"))
            {
                try
                {
                    int pid = cmd.Id;
                    bool ours = launched.Contains(pid);
                    if (!ours)
                    {
                        string? commandLine = TryGetWmicField(pid, "CommandLine");
                        if (!IsOurServerConsoleCommandLine(commandLine, batPath, serverDir))
                            continue;
                    }

                    string title = cmd.MainWindowTitle ?? string.Empty;
                    RunTaskKill(pid);
                    lock (_sync)
                        _launchedConsolePids.Remove(pid);
                    messages.Add(
                        string.IsNullOrWhiteSpace(title)
                            ? $"Closed leftover server console (PID {pid})."
                            : $"Closed leftover server console (PID {pid}, title=\"{title}\").");
                }
                catch (Exception ex)
                {
                    messages.Add($"Could not close cmd PID {cmd.Id}: {ex.Message}");
                }
                finally
                {
                    cmd.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            messages.Add($"CloseOrphanServerConsoles failed: {ex.Message}");
        }

        return messages;
    }

    /// <summary>
    /// Remember the configured server folder even before the first Start from this session,
    /// so Stop can still identify PZ Java started earlier / outside the manager.
    /// </summary>
    public void SetServerContext(string? serverPath, string? startBatNameOrPath)
    {
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(serverPath))
                _serverWorkingDirectory = serverPath.Trim();

            if (string.IsNullOrWhiteSpace(startBatNameOrPath))
                return;

            string bat = startBatNameOrPath.Trim();
            if (Path.IsPathRooted(bat))
                _lastStartBatPath = bat;
            else if (!string.IsNullOrWhiteSpace(_serverWorkingDirectory))
                _lastStartBatPath = Path.Combine(_serverWorkingDirectory, bat);
        }
    }

    private IEnumerable<Process> EnumeratePzServerJavaProcesses(
        string? serverDir = null,
        string? batPath = null)
    {
        lock (_sync)
        {
            serverDir ??= _serverWorkingDirectory;
            batPath ??= _lastStartBatPath;
        }

        Process[] javaProcesses = Process.GetProcessesByName("java");
        foreach (Process javaProcess in javaProcesses)
        {
            bool keep = false;
            try
            {
                keep = IsPzServerJavaProcess(javaProcess, serverDir, batPath);
            }
            catch
            {
                keep = false;
            }

            if (keep)
                yield return javaProcess;
            else
                javaProcess.Dispose();
        }
    }

    internal static bool IsPzServerJavaProcess(Process process, string? serverDir, string? batPath)
    {
        string? imagePath = null;
        try
        {
            imagePath = process.MainModule?.FileName;
        }
        catch
        {
            // Access denied for some system processes — fall through to WMIC.
        }

        if (LooksLikePzServerPath(imagePath, serverDir))
            return true;

        string? commandLine = TryGetWmicField(process.Id, "CommandLine");
        if (LooksLikePzServerCommandLine(commandLine, serverDir, batPath))
            return true;

        string? executablePath = TryGetWmicField(process.Id, "ExecutablePath");
        return LooksLikePzServerPath(executablePath, serverDir);
    }

    private static bool LooksLikePzServerPath(string? path, string? serverDir)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (ContainsPzFingerprint(path))
            return true;

        if (string.IsNullOrWhiteSpace(serverDir))
            return false;

        try
        {
            string normalizedServer = Path.GetFullPath(serverDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedPath = Path.GetFullPath(path);
            return normalizedPath.StartsWith(
                normalizedServer + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikePzServerCommandLine(string? commandLine, string? serverDir, string? batPath)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return false;

        if (ContainsPzFingerprint(commandLine))
            return true;

        if (!string.IsNullOrWhiteSpace(batPath)
            && commandLine.Contains(Path.GetFileName(batPath), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(serverDir)
            && commandLine.Contains(serverDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsOurServerConsoleCommandLine(string? commandLine, string? batPath, string? serverDir)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return false;

        if (!string.IsNullOrWhiteSpace(batPath)
            && commandLine.Contains(batPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // StartServer64.bat / StartServer32.bat under our known server folder.
        if (!string.IsNullOrWhiteSpace(serverDir)
            && commandLine.Contains(serverDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            && (commandLine.Contains("StartServer", StringComparison.OrdinalIgnoreCase)
                || commandLine.Contains("ProjectZomboid", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return commandLine.Contains("StartServer64", StringComparison.OrdinalIgnoreCase)
               || commandLine.Contains("StartServer32", StringComparison.OrdinalIgnoreCase)
               || ContainsPzFingerprint(commandLine);
    }

    private static bool ContainsPzFingerprint(string value) =>
        value.Contains("zombie.network.GameServer", StringComparison.OrdinalIgnoreCase)
        || value.Contains("ProjectZomboid", StringComparison.OrdinalIgnoreCase)
        || value.Contains("Project Zomboid", StringComparison.OrdinalIgnoreCase)
        || value.Contains("ZomboidDedicatedServer", StringComparison.OrdinalIgnoreCase);

    private static string? TryGetWmicField(int pid, string fieldName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wmic",
                Arguments = $"process where processid={pid} get {fieldName} /VALUE",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            using Process? probe = Process.Start(psi);
            if (probe is null)
                return null;

            string output = probe.StandardOutput.ReadToEnd();
            probe.WaitForExit(3000);
            if (probe.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
                return null;

            Regex regex = new(
                $"^{Regex.Escape(fieldName)}=(.*)$",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);
            Match match = regex.Match(output);
            if (!match.Success)
                return null;

            string value = match.Groups[1].Value.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    private static void RunTaskKill(int pid)
    {
        var killInfo = new ProcessStartInfo
        {
            FileName = "taskkill",
            Arguments = $"/F /T /PID {pid}",
            CreateNoWindow = true,
            UseShellExecute = false
        };

        using Process? killProcess = Process.Start(killInfo);
        killProcess?.WaitForExit(5000);
    }
}
