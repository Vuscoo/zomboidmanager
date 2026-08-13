using System.Diagnostics;

namespace ZomboidManager;

public class ServerProcessManager
{
    private Process? _managedProcess;
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
        }

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            try
            {
                LogReceived?.Invoke("[server console process exited]");
                ProcessExited?.Invoke();
            }
            catch
            {
                // ignore
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
        catch
        {
            // ignore
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
        StopManagedProcess();

        Process[] javaProcesses = Process.GetProcessesByName("java");
        if (javaProcesses.Length == 0)
        {
            messages.Add("No running Java processes found.");
        }
        else
        {
            foreach (Process javaProcess in javaProcesses)
            {
                try
                {
                    int pid = javaProcess.Id;
                    RunTaskKill(pid);
                    messages.Add($"Terminated Java process (PID {pid}).");
                }
                catch (Exception ex)
                {
                    messages.Add($"Could not terminate Java process (PID {javaProcess.Id}): {ex.Message}");
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

    public List<string> CloseOrphanServerConsoles()
    {
        var messages = new List<string>();

        try
        {
            foreach (Process cmd in Process.GetProcessesByName("cmd"))
            {
                try
                {
                    string title = cmd.MainWindowTitle ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(title))
                        continue;

                    bool looksLikeServerConsole =
                        title.Contains("StartServer", StringComparison.OrdinalIgnoreCase)
                        || title.Contains("ProjectZomboid", StringComparison.OrdinalIgnoreCase)
                        || title.Contains("Project Zomboid", StringComparison.OrdinalIgnoreCase)
                        || title.Contains("Press any key", StringComparison.OrdinalIgnoreCase)
                        || title.Contains("Drücken Sie eine Taste", StringComparison.OrdinalIgnoreCase)
                        || title.Contains("Taste", StringComparison.OrdinalIgnoreCase)
                        || title.Contains("pause", StringComparison.OrdinalIgnoreCase);

                    if (!looksLikeServerConsole)
                        continue;

                    RunTaskKill(cmd.Id);
                    messages.Add($"Closed leftover console (PID {cmd.Id}, title=\"{title}\").");
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
