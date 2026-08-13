using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ZomboidManager;

public partial class MainForm : Form
{
    public static readonly string CurrentVersion = ResolveAppVersion();

    private const string DiscordInviteUrl = "https://discord.gg/2YJTzATKGn";
    private const string DonateUrl = "https://linktr.ee/vusco";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private bool _updateInProgress;

    private readonly ScheduleManager _scheduleManager = new();
    private readonly RconManager _rconManager = new();
    private readonly ServerProcessManager _serverProcessManager = new();
    private readonly BroadcastManager _broadcastManager = new();
    private readonly BackupScheduler _backupScheduler = new();
    private readonly HardwareMonitor _hardwareMonitor = new();
    private readonly ModUpdateRestartFlow _modUpdateRestartFlow = new();
    private readonly System.Windows.Forms.Timer _hardwareTimer;
    private readonly System.Windows.Forms.Timer _modUpdatePollTimer;
    private readonly System.Windows.Forms.Timer _modUpdateStatusTimer;
    private bool _hardwarePollingActive;
    private bool _modUpdateCheckInFlight;

    private AppConfig _config;
    private WebView2 _webView = null!;
    private DateTime? _lastRestartTime;
    private bool _restartInProgress;
    private readonly bool _isFirstStart;
    private readonly System.Windows.Forms.Timer _discordCustomTimer;
    private int? _lastDiscordCustomHour;
    private bool _backupInProgress;
    private readonly HashSet<int> _broadcastInFlight = new();

    public MainForm()
    {
        _config = ConfigManager.Load();
        _config.DiscordEvents = DiscordEventCatalog.Normalize(_config.DiscordEvents, _config.DiscordCustomMessage);
        _config.ModUpdateAutoRestart ??= new ModUpdateAutoRestartConfig();
        _isFirstStart = string.IsNullOrWhiteSpace(_config.ServerPath)
            || string.IsNullOrWhiteSpace(_config.UiLanguage);

        InitializeComponent();

        Text = "Zomboid Manager";
        Size = new Size(1100, 750);
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = AppTheme.BackgroundDark;
        ApplyAppIcon();

        _webView = new WebView2
        {
            Dock = DockStyle.Fill
        };
        Controls.Add(_webView);

        WireScheduleEvents();
        WireServerProcessEvents();
        WireBroadcastEvents();
        WireBackupSchedulerEvents();
        WireModUpdateRestartEvents();
        RestoreSchedulerFromConfig();
        RestoreBroadcastFromConfig();
        RestoreBackupScheduleFromConfig();

        _discordCustomTimer = new System.Windows.Forms.Timer { Interval = 20000 };
        _discordCustomTimer.Tick += OnDiscordCustomTimerTick;
        _discordCustomTimer.Start();

        _hardwareTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        _hardwareTimer.Tick += (_, _) =>
        {
            if (_hardwarePollingActive)
                PushHardwareStats();
        };

        _modUpdatePollTimer = new System.Windows.Forms.Timer { Interval = 15 * 60 * 1000 };
        _modUpdatePollTimer.Tick += (_, _) => _ = RunScheduledModUpdateCheckAsync(fromManual: false);

        _modUpdateStatusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _modUpdateStatusTimer.Tick += (_, _) =>
        {
            if (_modUpdateRestartFlow.IsActive)
                PushModUpdateRestartStatus();
            else
                _modUpdateStatusTimer.Stop();
        };

        ApplyModUpdatePollTimer();

        _ = InitializeWebView();
    }

    private void ApplyAppIcon()
    {
        try
        {
            // Prefer the EXE icon (ApplicationIcon) so taskbar/title match the desktop shortcut.
            Icon? fromExe = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (fromExe is not null)
            {
                Icon = fromExe;
                return;
            }
        }
        catch
        {
            // fall through
        }

        try
        {
            string[] candidates =
            [
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "ZMIcon.ico"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ZMIcon.ico")
            ];
            foreach (string path in candidates)
            {
                if (!File.Exists(path))
                    continue;
                Icon = new Icon(path);
                return;
            }
        }
        catch
        {
            // keep default icon
        }
    }

    private void WireBroadcastEvents()
    {
        _broadcastManager.BroadcastDue += (index, slot) =>
        {
            _ = HandleBroadcastDueAsync(index, slot);
        };
    }

    private void RestoreBroadcastFromConfig()
    {
        _config.BroadcastMessages = BroadcastManager.Normalize(_config.BroadcastMessages);
        _broadcastManager.UpdateSlots(_config.BroadcastMessages);
        // Persist recalculated next-send times from startup.
        _config.BroadcastMessages = _broadcastManager.Slots.ToList();
        ConfigManager.Save(_config);
        _broadcastManager.Start();
    }

    private void WireBackupSchedulerEvents()
    {
        _backupScheduler.BackupDue += () =>
        {
            SendToWeb("log", "Scheduled backup triggered.");
            _ = HandleCreateBackupAsync(fromScheduler: true);
        };
        _backupScheduler.ScheduleDisabled += () =>
        {
            _config.BackupSchedule = _backupScheduler.Schedule;
            ConfigManager.Save(_config);
            SendToWeb("backup_schedule_status", _backupScheduler.BuildStatusPayload());
            SendToWeb("log", "One-time backup schedule disabled after run.");
        };
    }

    private void RestoreBackupScheduleFromConfig()
    {
        _config.BackupSchedule = BackupScheduler.Normalize(_config.BackupSchedule);
        _backupScheduler.UpdateSchedule(_config.BackupSchedule);
        _backupScheduler.Start();
    }

    private void WireScheduleEvents()
    {
        _scheduleManager.LogMessage += message =>
        {
            SendToWeb("log", message);
            SendToWeb("server_console", new { line = message });
        };
        _scheduleManager.RestartTriggered += hour =>
        {
            string msg = $"Scheduled restart triggered (hour {hour:00}:00).";
            SendToWeb("log", msg);
            SendToWeb("server_console", new { line = msg });
            _ = NotifyDiscordEventAsync(
                DiscordEventCatalog.ScheduledRestart,
                new Dictionary<string, string> { ["hour"] = hour.ToString("00") });
            _ = ExecuteRestartRoutine();
        };
        _scheduleManager.WarningAnnouncementTriggered += minutesBefore =>
        {
            _ = SendPreRestartAnnouncementAsync(minutesBefore);
        };
    }

    private void WireServerProcessEvents()
    {
        _serverProcessManager.LogReceived += line =>
        {
            try
            {
                SendToWeb("server_console", new { line });
            }
            catch
            {
                // never crash host from console piping
            }
        };
        _serverProcessManager.ProcessExited += () =>
        {
            try
            {
                SendToWeb("server_console", new { line = "[server process exited]" });
                _ = HandleGetServerStatusAsync();
            }
            catch
            {
                // ignore
            }
        };
    }

    private void RestoreSchedulerFromConfig()
    {
        _scheduleManager.UpdateSelectedHours(_config.SelectedHours ?? new List<int>());
        _scheduleManager.UpdateWarningSettings(
            _config.Announce10MinBeforeRestart,
            _config.Announce5MinBeforeRestart);
        if (_config.SchedulerActive)
            _scheduleManager.Start();
    }

    private async Task InitializeWebView()
    {
        string webViewDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZomboidManager",
            "WebView2");
        Directory.CreateDirectory(webViewDataDir);

        CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: webViewDataDir);
        await _webView.EnsureCoreWebView2Async(env);

        string htmlPath = Path.Combine(ResolveContentRoot(), "wwwroot", "index.html");
        if (!File.Exists(htmlPath))
            throw new FileNotFoundException("UI files not found.", htmlPath);

        _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
        _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        _webView.CoreWebView2.NavigationCompleted += (_, args) =>
        {
            if (!args.IsSuccess)
                return;
            SendToWeb("app_info", new { version = CurrentVersion });
            if (Environment.GetCommandLineArgs().Any(a =>
                    string.Equals(a, "--selftest-admin-commands", StringComparison.OrdinalIgnoreCase)))
            {
                _ = RunAdminCommandsSelfTestAsync();
            }
        };

        if (_isFirstStart)
        {
            await Task.Delay(1500);
            SendToWeb("first_start", new
            {
                needsLanguage = string.IsNullOrWhiteSpace(_config.UiLanguage),
                needsSettings = string.IsNullOrWhiteSpace(_config.ServerPath),
                language = _config.UiLanguage ?? string.Empty
            });
        }
    }

    /// <summary>
    /// UI is loaded from LocalAppData (extracted from the EXE). Debug builds may use sidecar wwwroot.
    /// </summary>
    private static string ResolveContentRoot() => UiHost.EnsureContentRoot(CurrentVersion);

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            string? raw = null;

            try
            {
                raw = e.TryGetWebMessageAsString();
            }
            catch (ArgumentException)
            {
                // Message was posted as JSON object instead of a raw string.
            }

            if (string.IsNullOrWhiteSpace(raw))
                raw = e.WebMessageAsJson;

            if (string.IsNullOrWhiteSpace(raw))
                return;

            if (raw.Length >= 2 && raw[0] == '"')
                raw = JsonSerializer.Deserialize<string>(raw) ?? raw;

            WebMessage? message = JsonSerializer.Deserialize<WebMessage>(raw, JsonOptions);
            if (message is null || string.IsNullOrWhiteSpace(message.Action))
                return;

            switch (message.Action)
            {
                case "get_server_status":
                    _ = HandleGetServerStatusAsync();
                    break;

                case "manual_restart":
                    _ = ExecuteRestartRoutine();
                    break;

                case "save_hours":
                    HandleSaveHours(message.Data);
                    break;

                case "toggle_scheduler":
                    HandleToggleScheduler();
                    break;

                case "save_restart_warnings":
                    HandleSaveRestartWarnings(message.Data);
                    break;

                case "save_mod_update_auto_restart":
                    HandleSaveModUpdateAutoRestart(message.Data);
                    break;

                case "mod_update_check_now":
                    _ = RunScheduledModUpdateCheckAsync(fromManual: true);
                    break;

                case "mod_update_restart_now":
                    _ = HandleModUpdateRestartNowAsync();
                    break;

                case "mod_update_schedule_restart":
                    _ = HandleModUpdateScheduleRestartAsync(message.Data);
                    break;

                case "mod_update_cancel_restart":
                    HandleModUpdateCancelRestart();
                    break;

                case "save_broadcast_messages":
                    HandleSaveBroadcastMessages(message.Data);
                    break;

                case "get_broadcast_messages":
                    HandleGetBroadcastMessages();
                    break;

                case "send_broadcast_now":
                    _ = HandleSendBroadcastNowAsync(message.Data);
                    break;

                case "send_rcon_command":
                    _ = HandleSendRconCommandAsync(message.Data);
                    break;

                case "save_settings":
                    HandleSaveSettings(message.Data);
                    break;

                case "get_settings":
                    HandleGetSettings();
                    break;

                case "test_rcon_connection":
                    _ = HandleTestRconConnectionAsync();
                    break;

                case "check_updates":
                    _ = CheckForUpdatesAsync();
                    break;

                case "open_discord":
                    OpenDiscordInvite();
                    break;

                case "browse_ini_file":
                    BrowseIniFile();
                    break;

                case "browse_server_folder":
                    BrowseServerFolder();
                    break;

                case "browse_start_bat":
                    BrowseStartBat();
                    break;

                case "browse_zomboid_data_folder":
                    BrowseZomboidDataFolder();
                    break;

                case "load_ini_file":
                    HandleLoadIniFile(message.Data);
                    break;

                case "save_ini_file":
                    HandleSaveIniFile(message.Data);
                    break;

                case "get_mod_list":
                    HandleGetModList();
                    break;

                case "open_donate":
                    OpenExternalUrl(DonateUrl);
                    break;

                case "open_url":
                    if (message.Data.ValueKind == JsonValueKind.Object
                        && message.Data.TryGetProperty("url", out JsonElement urlEl)
                        && urlEl.ValueKind == JsonValueKind.String)
                    {
                        string? url = urlEl.GetString();
                        if (!string.IsNullOrWhiteSpace(url))
                            OpenExternalUrl(url);
                    }
                    break;

                case "set_language":
                    HandleSetLanguage(message.Data);
                    break;

                case "start_server":
                    HandleStartServer();
                    break;

                case "stop_server":
                    _ = HandleStopServerAsync();
                    break;

                case "create_backup":
                    _ = HandleCreateBackupAsync();
                    break;

                case "list_backups":
                    HandleListBackups();
                    break;

                case "save_backup_schedule":
                    HandleSaveBackupSchedule(message.Data);
                    break;

                case "get_backup_schedule":
                    HandleGetBackupSchedule();
                    break;

                case "start_hardware_monitor":
                    StartHardwareMonitor();
                    break;

                case "stop_hardware_monitor":
                    StopHardwareMonitor();
                    break;

                case "get_hardware_stats":
                    PushHardwareStats();
                    break;

                case "list_logs":
                    HandleListLogs();
                    break;

                case "read_log":
                    HandleReadLog(message.Data);
                    break;

                case "get_admin_commands":
                    HandleGetAdminCommands();
                    break;

                case "load_sandbox":
                    HandleLoadSandbox();
                    break;

                case "save_sandbox":
                    HandleSaveSandbox(message.Data);
                    break;

                case "browse_sandbox_file":
                    BrowseSandboxFile();
                    break;

                case "get_player_list":
                    _ = HandleGetPlayerListAsync();
                    break;

                case "player_action":
                    _ = HandlePlayerActionAsync(message.Data);
                    break;

                case "save_discord_settings":
                    HandleSaveDiscordSettings(message.Data);
                    break;

                case "test_discord_webhook":
                    _ = HandleTestDiscordWebhookAsync(message.Data);
                    break;

                default:
                    Debug.WriteLine($"Unknown action received: {message.Action}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to handle web message: {ex.Message}");
        }
    }

    private static string ResolveAppVersion()
    {
        Version? asm = Assembly.GetExecutingAssembly().GetName().Version;
        if (asm is null)
            return "1.0.0";
        return $"{asm.Major}.{asm.Minor}.{asm.Build}";
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_updateInProgress)
        {
            SendToWeb("update_result", new
            {
                status = "busy",
                text = "An update is already in progress."
            });
            return;
        }

        _updateInProgress = true;
        try
        {
            SendToWeb("update_result", new
            {
                status = "checking",
                text = "Checking for updates…"
            });

            AppUpdater.CheckResult check = await AppUpdater.CheckAsync(CurrentVersion);

            if (!check.UpdateAvailable)
            {
                bool looksLikeError = check.Message.Contains("failed", StringComparison.OrdinalIgnoreCase)
                    || check.Message.Contains("Could not", StringComparison.OrdinalIgnoreCase)
                    || check.Message.Contains("No GitHub release", StringComparison.OrdinalIgnoreCase);
                SendToWeb("update_result", new
                {
                    status = looksLikeError ? "error" : "up_to_date",
                    text = check.Message
                });
                return;
            }

            if (string.IsNullOrWhiteSpace(check.DownloadUrl))
            {
                SendToWeb("update_result", new
                {
                    status = "error",
                    text = check.Message
                });
                return;
            }

            string tag = check.LatestTag ?? "new version";
            string tempExe = Path.Combine(
                Path.GetTempPath(),
                $"ZomboidManager-update-{AppUpdater.NormalizeVersion(tag)}-{Guid.NewGuid():N}.exe");

            SendToWeb("update_result", new
            {
                status = "downloading",
                progress = 0,
                text = $"Updating to {tag}…"
            });

            await AppUpdater.DownloadAsync(check.DownloadUrl, tempExe, progress =>
            {
                SendToWeb("update_result", new
                {
                    status = "downloading",
                    progress,
                    text = $"Updating to {tag}… {progress}%"
                });
            });

            SendToWeb("update_result", new
            {
                status = "applying",
                text = $"Restarting into {tag}…"
            });
            await Task.Delay(500);

            string targetExe = AppUpdater.ResolveRunningExePath();
            AppUpdater.ApplyExeAndRestart(tempExe, targetExe, Environment.ProcessId);

            // Give the detached updater a moment to spawn before we tear down.
            await Task.Delay(300);

            BeginInvoke(() =>
            {
                try
                {
                    _webView?.Dispose();
                }
                catch
                {
                    // ignore
                }

                Close();
                Application.Exit();
                Environment.Exit(0);
            });
        }
        catch (Exception ex)
        {
            SendToWeb("update_result", new
            {
                status = "error",
                text = $"Update failed: {ex.Message}"
            });
        }
        finally
        {
            _updateInProgress = false;
        }
    }

    private void OpenDiscordInvite()
    {
        OpenExternalUrl(DiscordInviteUrl);
    }

    private void OpenExternalUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SendToWeb("update_result", new
            {
                text = $"Could not open link: {ex.Message}"
            });
        }
    }

    private void HandleSetLanguage(JsonElement data)
    {
        string lang = "en";
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("language", out JsonElement langEl)
            && langEl.ValueKind == JsonValueKind.String)
        {
            lang = langEl.GetString() ?? "en";
        }

        _config.UiLanguage = lang;
        ConfigManager.Save(_config);
        SendToWeb("language_saved", new { language = lang });
        HandleGetSettings();
    }

    private void HandleGetModList()
    {
        try
        {
            string? path = _config.LastIniFilePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                SendToWeb("mod_list_data", new
                {
                    success = false,
                    message = "Keine INI geladen. Bitte zuerst im Config-Tab eine server.ini laden.",
                    mods = Array.Empty<object>()
                });
                return;
            }

            Dictionary<string, string> raw = IniManager.ReadIni(path);
            (List<string> workshopIds, List<string> modIds) = IniManager.ParseModLists(raw);

            int count = Math.Max(workshopIds.Count, modIds.Count);
            var mods = new List<object>();
            for (int i = 0; i < count; i++)
            {
                string workshopId = i < workshopIds.Count ? workshopIds[i] : "";
                string modId = i < modIds.Count ? modIds[i] : "";
                string steamUrl = string.IsNullOrWhiteSpace(workshopId)
                    ? ""
                    : $"https://steamcommunity.com/sharedfiles/filedetails/?id={workshopId}";

                mods.Add(new
                {
                    index = i + 1,
                    workshopId,
                    modId,
                    steamUrl,
                    name = string.IsNullOrWhiteSpace(modId) ? workshopId : modId
                });
            }

            SendToWeb("mod_list_data", new
            {
                success = true,
                path,
                workshopCount = workshopIds.Count,
                modCount = modIds.Count,
                mods
            });
        }
        catch (Exception ex)
        {
            SendToWeb("mod_list_data", new
            {
                success = false,
                message = ex.Message,
                mods = Array.Empty<object>()
            });
        }
    }

    private string? ResolveStartBatPath()
    {
        string batPath = Path.Combine(_config.ServerPath ?? string.Empty, _config.StartBat ?? string.Empty);
        return File.Exists(batPath) ? batPath : null;
    }

    private void HandleStartServer()
    {
        try
        {
            string? batPath = ResolveStartBatPath();
            if (batPath is null)
            {
                SendToWeb("server_action_result", new
                {
                    success = false,
                    message = "Start file not found. Configure Server folder + Start .bat in Settings."
                });
                return;
            }

            bool javaRunning = false;
            Process[] javaProcesses = Process.GetProcessesByName("java");
            try
            {
                javaRunning = javaProcesses.Length > 0;
            }
            finally
            {
                foreach (Process jp in javaProcesses)
                    jp.Dispose();
            }

            if (javaRunning && !_serverProcessManager.IsManagedProcessRunning)
            {
                SendToWeb("server_console", new
                {
                    line = "Server already appears online (Java process detected). Press Stop first, then Start."
                });
                SendToWeb("server_action_result", new
                {
                    success = false,
                    message = "Server already online. Press Stop first, then Start."
                });
                return;
            }

            SendToWeb("server_console", new { line = $"Starting: {batPath}" });
            Process process = _serverProcessManager.StartServer(batPath, embedConsole: true);
            SendToWeb("server_console", new { line = $"Server console opened (PID {process.Id})." });
            SendToWeb("server_action_result", new
            {
                success = true,
                message = "Server start requested — console window should open."
            });
            _ = NotifyDiscordAsync("▶️ Server start requested.");
            _ = HandleGetServerStatusAsync();
        }
        catch (Exception ex)
        {
            SendToWeb("server_console", new { line = "Start failed: " + ex.Message });
            SendToWeb("server_action_result", new { success = false, message = ex.Message });
        }
    }

    private async Task HandleStopServerAsync()
    {
        try
        {
            SendToWeb("server_console", new { line = "Stopping via RCON quit..." });
            await NotifyDiscordAsync("🛑 Server stop requested (RCON quit).");
            string response = await _rconManager.SendCommandAsync(
                _config.RconHost, _config.RconPort, _config.RconPassword, "quit");
            SendToWeb("server_console", new { line = $"RCON: {response}" });
            await Task.Delay(8000);

            foreach (string message in _serverProcessManager.KillServerTree())
                SendToWeb("server_console", new { line = message });

            SendToWeb("server_action_result", new { success = true, message = "Stop completed." });
            await HandleGetServerStatusAsync();
        }
        catch (Exception ex)
        {
            SendToWeb("server_action_result", new { success = false, message = ex.Message });
        }
    }

    private async Task HandleCreateBackupAsync(bool fromScheduler = false)
    {
        if (_backupInProgress)
        {
            SendToWeb("backup_result", new
            {
                success = false,
                message = "A backup is already in progress."
            });
            return;
        }

        _backupInProgress = true;
        SendToWeb("backup_progress", new { active = true, done = 0, file = "" });

        AppConfig snapshot = CloneConfigForBackup(_config);
        try
        {
            var progress = new Progress<(int done, string file)>(p =>
            {
                SendToWeb("backup_progress", new { active = true, done = p.done, file = p.file });
            });

            BackupResult result = await Task.Run(() => BackupManager.CreateBackup(snapshot, progress));
            SendToWeb("backup_progress", new { active = false, done = result.FileCount, file = "" });
            SendToWeb("backup_result", new
            {
                success = result.Success,
                message = result.Message,
                path = result.Path,
                fileCount = result.FileCount
            });
            if (result.Success)
            {
                HandleListBackups();
                if (fromScheduler)
                {
                    SendToWeb("log", $"Scheduled backup completed: {result.Message}");
                    _backupScheduler.MarkOneTimeDone();
                }
            }
            else if (fromScheduler)
            {
                SendToWeb("log", $"Scheduled backup failed: {result.Message}");
            }
        }
        catch (Exception ex)
        {
            SendToWeb("backup_progress", new { active = false, done = 0, file = "" });
            SendToWeb("backup_result", new { success = false, message = ex.Message });
            if (fromScheduler)
                SendToWeb("log", $"Scheduled backup failed: {ex.Message}");
        }
        finally
        {
            _backupInProgress = false;
        }
    }

    private static AppConfig CloneConfigForBackup(AppConfig source) => new()
    {
        ZomboidDataPath = source.ZomboidDataPath,
        LastIniFilePath = source.LastIniFilePath
    };

    private void HandleListBackups()
    {
        List<BackupInfo> backups = BackupManager.ListBackups();
        SendToWeb("backup_list", new
        {
            backups = backups.Select(b => new
            {
                name = b.Name,
                path = b.Path,
                createdAt = b.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")
            })
        });
    }

    private void HandleLoadSandbox()
    {
        try
        {
            string? path = SandboxManager.ResolveSandboxPath(_config);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                SendToWeb("sandbox_loaded", new
                {
                    success = false,
                    message = "SandboxVars.lua not found. Set Zomboid data path and load a server.ini first."
                });
                return;
            }

            Dictionary<string, string> raw = SandboxManager.ReadSandbox(path);
            List<IniEntry> entries = SandboxManager.ParseToEntries(raw);
            SendToWeb("sandbox_loaded", new
            {
                success = true,
                path,
                entries
            });
        }
        catch (Exception ex)
        {
            SendToWeb("sandbox_loaded", new { success = false, message = ex.Message });
        }
    }

    private void HandleSaveSandbox(JsonElement data)
    {
        try
        {
            string path = data.GetProperty("path").GetString()
                ?? throw new ArgumentException("Missing sandbox path.");

            Dictionary<string, string>? values = JsonSerializer.Deserialize<Dictionary<string, string>>(
                data.GetProperty("values").GetRawText(),
                JsonOptions);

            if (values is null)
                throw new ArgumentException("Could not deserialize sandbox values.");

            SandboxManager.WriteSandbox(path, values);
            SendToWeb("sandbox_saved", new { success = true, message = "SandboxVars saved." });
        }
        catch (Exception ex)
        {
            SendToWeb("sandbox_saved", new { success = false, message = "Error: " + ex.Message });
        }
    }

    private void BrowseSandboxFile()
    {
        this.BeginInvoke(new Action(() =>
        {
            try
            {
                Activate();
                BringToFront();

                using var dialog = new OpenFileDialog
                {
                    Title = "SandboxVars.lua auswählen",
                    Filter = "Lua (*.lua)|*.lua|All files (*.*)|*.*",
                    CheckFileExists = true,
                    Multiselect = false
                };

                string initial = !string.IsNullOrWhiteSpace(_config.ZomboidDataPath)
                    ? Path.Combine(_config.ZomboidDataPath, "Server")
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                if (Directory.Exists(initial))
                    dialog.InitialDirectory = initial;

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    Dictionary<string, string> raw = SandboxManager.ReadSandbox(dialog.FileName);
                    List<IniEntry> entries = SandboxManager.ParseToEntries(raw);
                    SendToWeb("sandbox_loaded", new
                    {
                        success = true,
                        path = dialog.FileName,
                        entries
                    });
                }
            }
            catch (Exception ex)
            {
                SendToWeb("sandbox_loaded", new { success = false, message = ex.Message });
            }
        }));
    }

    private void BrowseIniFile()
    {
        this.BeginInvoke(new Action(() =>
        {
            try
            {
                Activate();
                BringToFront();

                using var dialog = new OpenFileDialog
                {
                    Title = "Project Zomboid Server-Konfiguration auswählen",
                    Filter = "INI-Dateien (*.ini)|*.ini|Alle Dateien (*.*)|*.*",
                    CheckFileExists = true,
                    Multiselect = false
                };

                string zomboidServer = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Zomboid",
                    "Server");

                dialog.InitialDirectory = Directory.Exists(zomboidServer)
                    ? zomboidServer
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                if (dialog.ShowDialog(this) == DialogResult.OK)
                    SendToWeb("ini_file_selected", new { path = dialog.FileName });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BrowseIniFile failed: {ex}");
                SendToWeb("ini_saved", new { success = false, message = "Fehler Dialog: " + ex.Message });
            }
        }));
    }

    private void BrowseServerFolder()
    {
        this.BeginInvoke(new Action(() =>
        {
            try
            {
                Activate();
                BringToFront();

                using var dialog = new FolderBrowserDialog
                {
                    Description = "Project Zomboid Server-Ordner auswählen (Ordner mit StartServer64.bat)",
                    UseDescriptionForTitle = true,
                    ShowNewFolderButton = false
                };

                if (Directory.Exists(@"C:\"))
                    dialog.InitialDirectory = @"C:\";

                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;

                string selectedPath = dialog.SelectedPath;
                _config.ServerPath = selectedPath;

                var (startBat, notice) = DetectStartBat(selectedPath);
                if (!string.IsNullOrEmpty(startBat))
                {
                    _config.StartBat = startBat;
                }

                ConfigManager.Save(_config);

                SendToWeb("server_folder_selected", new
                {
                    path = selectedPath,
                    startBat,
                    notice
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BrowseServerFolder failed: {ex}");
                SendToWeb("ini_saved", new { success = false, message = "Fehler Dialog: " + ex.Message });
            }
        }));
    }

    /// <summary>
    /// Finds a likely PZ dedicated server launcher .bat.
    /// Auto-fills only when exactly one likely match exists.
    /// </summary>
    private static (string startBat, string notice) DetectStartBat(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return (string.Empty, string.Empty);

        string[] allBats = Directory.GetFiles(folderPath, "*.bat");
        if (allBats.Length == 0)
            return (string.Empty, "No .bat files found — please select the startup file manually.");

        static bool IsLikely(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            return name.Contains("StartServer", StringComparison.OrdinalIgnoreCase)
                   || name.Contains("startserver", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("StartServer64", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("StartServer32", StringComparison.OrdinalIgnoreCase)
                   || (name.Contains("Start", StringComparison.OrdinalIgnoreCase)
                       && name.Contains("Server", StringComparison.OrdinalIgnoreCase))
                   || name.Contains("start", StringComparison.OrdinalIgnoreCase);
        }

        string[] likely = allBats.Where(IsLikely).ToArray();

        // Prefer StartServer* names over generic "start"
        string[] preferred = likely
            .Where(p => Path.GetFileName(p).Contains("StartServer", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        string[] candidates = preferred.Length > 0 ? preferred : likely;

        if (candidates.Length == 1)
        {
            string name = Path.GetFileName(candidates[0]);
            return (name, $"Startup file auto-detected: {name}");
        }

        if (allBats.Length == 1)
        {
            string name = Path.GetFileName(allBats[0]);
            return (name, $"Startup file auto-detected: {name}");
        }

        return (string.Empty, "Multiple .bat files found — please select the startup file manually.");
    }

    private void BrowseStartBat()
    {
        this.BeginInvoke(new Action(() =>
        {
            try
            {
                Activate();
                BringToFront();

                using var dialog = new OpenFileDialog
                {
                    Title = "Select server startup .bat file",
                    Filter = "Batch files (*.bat)|*.bat|All files (*.*)|*.*",
                    CheckFileExists = true,
                    Multiselect = false
                };

                string initial = _config.ServerPath;
                if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial))
                    dialog.InitialDirectory = initial;

                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;

                string fullPath = dialog.FileName;
                string fileName = Path.GetFileName(fullPath);
                string? dir = Path.GetDirectoryName(fullPath);

                // If user picked a bat outside the configured server folder, keep filename and optionally update folder.
                if (!string.IsNullOrWhiteSpace(dir)
                    && (string.IsNullOrWhiteSpace(_config.ServerPath)
                        || !string.Equals(Path.GetFullPath(dir), Path.GetFullPath(_config.ServerPath), StringComparison.OrdinalIgnoreCase)))
                {
                    // Prefer storing relative to ServerPath when same folder; otherwise store filename and set folder.
                    if (string.IsNullOrWhiteSpace(_config.ServerPath))
                        _config.ServerPath = dir;
                }

                // ResolveStartBatPath combines ServerPath + StartBat filename
                if (!string.IsNullOrWhiteSpace(_config.ServerPath)
                    && string.Equals(Path.GetFullPath(dir ?? ""), Path.GetFullPath(_config.ServerPath), StringComparison.OrdinalIgnoreCase))
                {
                    _config.StartBat = fileName;
                }
                else
                {
                    _config.StartBat = fileName;
                    if (!string.IsNullOrWhiteSpace(dir))
                        _config.ServerPath = dir;
                }

                ConfigManager.Save(_config);
                SendToWeb("start_bat_selected", new
                {
                    startBat = _config.StartBat,
                    serverPath = _config.ServerPath
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BrowseStartBat failed: {ex}");
                SendToWeb("ini_saved", new { success = false, message = "Fehler Dialog: " + ex.Message });
            }
        }));
    }

    private void BrowseZomboidDataFolder()
    {
        this.BeginInvoke(new Action(() =>
        {
            try
            {
                Activate();
                BringToFront();

                using var dialog = new FolderBrowserDialog
                {
                    Description = "Zomboid Daten-Ordner auswählen (Ordner mit 'Server' Unterordner, z.B. C:\\Users\\NAME\\Zomboid)",
                    UseDescriptionForTitle = true,
                    ShowNewFolderButton = false,
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                };

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    string selectedPath = dialog.SelectedPath;
                    SendToWeb("zomboid_folder_selected", new { path = selectedPath });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BrowseZomboidDataFolder failed: {ex}");
                SendToWeb("ini_saved", new { success = false, message = "Fehler Dialog: " + ex.Message });
            }
        }));
    }

    private void HandleLoadIniFile(JsonElement data)
    {
        try
        {
            string? path = data.GetProperty("path").GetString();
            if (string.IsNullOrWhiteSpace(path))
                return;

            Dictionary<string, string> raw = IniManager.ReadIni(path);
            List<IniEntry> entries = IniManager.ParseToEntries(raw);
            SendToWeb("ini_loaded", new { path, entries });

            _config.LastIniFilePath = path;
            ConfigManager.Save(_config);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load INI: {ex.Message}");
        }
    }

    private void HandleSaveIniFile(JsonElement data)
    {
        try
        {
            string path = data.GetProperty("path").GetString()
                ?? throw new ArgumentException("Missing INI path.");

            Dictionary<string, string>? values = JsonSerializer.Deserialize<Dictionary<string, string>>(
                data.GetProperty("values").GetRawText(),
                JsonOptions);

            if (values is null)
                throw new ArgumentException("Could not deserialize INI values.");

            IniManager.WriteIni(path, values);
            SendToWeb("ini_saved", new { success = true, message = "Konfiguration gespeichert!" });
        }
        catch (Exception ex)
        {
            SendToWeb("ini_saved", new { success = false, message = "Fehler: " + ex.Message });
        }
    }

    private void HandleSaveHours(JsonElement data)
    {
        List<int> hours = new();

        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("hours", out JsonElement hoursElement))
        {
            hours = JsonSerializer.Deserialize<List<int>>(hoursElement.GetRawText(), JsonOptions) ?? new List<int>();
        }
        else if (data.ValueKind == JsonValueKind.Array)
        {
            hours = JsonSerializer.Deserialize<List<int>>(data.GetRawText(), JsonOptions) ?? new List<int>();
        }

        hours = hours.Where(h => h is >= 0 and <= 23).Distinct().OrderBy(h => h).ToList();
        _config.SelectedHours = hours;
        ConfigManager.Save(_config);
        _scheduleManager.UpdateSelectedHours(hours);
        _scheduleManager.UpdateWarningSettings(
            _config.Announce10MinBeforeRestart,
            _config.Announce5MinBeforeRestart);

        string summary = hours.Count == 0
            ? "(none)"
            : string.Join(", ", hours.Select(h => $"{h:00}:00"));
        SendToWeb("log", $"Hours saved: {summary}");
    }

    private void HandleSaveRestartWarnings(JsonElement data)
    {
        bool GetBool(string name, bool fallback)
        {
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out JsonElement value))
                return fallback;
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(value.GetString(), out bool b) && b,
                _ => fallback
            };
        }

        _config.Announce10MinBeforeRestart = GetBool("announce10MinBeforeRestart", _config.Announce10MinBeforeRestart);
        _config.Announce5MinBeforeRestart = GetBool("announce5MinBeforeRestart", _config.Announce5MinBeforeRestart);
        ConfigManager.Save(_config);
        _scheduleManager.UpdateWarningSettings(
            _config.Announce10MinBeforeRestart,
            _config.Announce5MinBeforeRestart);

        SendToWeb("restart_warnings_saved", new { success = true });
    }

    private async Task SendPreRestartAnnouncementAsync(int minutesBefore)
    {
        // Fixed messages per warning checkbox (independent of each other).
        // The existing 1-minute warning in ExecuteRestartRoutine is unchanged.
        string text = minutesBefore switch
        {
            10 => "Server is restarting in 10 Minutes, please find a safe place!",
            5 => "Server is restarting in 5 Minutes, better stop fighting!",
            _ => $"Server restarting in {minutesBefore} minutes!"
        };

        string escaped = text.Replace("\"", "\\\"");
        string command = $"servermsg \"{escaped}\"";

        SendToWeb("log", $"RCON: {command}");
        try
        {
            string response = await _rconManager.SendCommandAsync(
                _config.RconHost,
                _config.RconPort,
                _config.RconPassword,
                command);
            SendToWeb("log", $"Pre-restart announcement ({minutesBefore} min) response: {response}");
        }
        catch (Exception ex)
        {
            SendToWeb("log", $"Pre-restart announcement failed: {ex.Message}");
        }
    }

    private void HandleToggleScheduler()
    {
        if (_scheduleManager.IsRunning)
        {
            _scheduleManager.Stop();
            _config.SchedulerActive = false;
        }
        else
        {
            _scheduleManager.UpdateSelectedHours(_config.SelectedHours ?? new List<int>());
            _scheduleManager.UpdateWarningSettings(
                _config.Announce10MinBeforeRestart,
                _config.Announce5MinBeforeRestart);
            _scheduleManager.Start();
            _config.SchedulerActive = true;
        }

        ConfigManager.Save(_config);
        SendToWeb("scheduler_status", new { active = _config.SchedulerActive });
    }

    private void HandleSaveSettings(JsonElement data)
    {
        string GetString(string name, string fallback = "")
        {
            if (data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty(name, out JsonElement value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? fallback;
            }

            return fallback;
        }

        int GetInt(string name, int fallback)
        {
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out JsonElement value))
                return fallback;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
                return number;

            if (value.ValueKind == JsonValueKind.String
                && int.TryParse(value.GetString(), out int parsed))
            {
                return parsed;
            }

            return fallback;
        }

        _config.RconHost = GetString("rconHost", _config.RconHost);
        _config.RconPort = GetInt("rconPort", _config.RconPort);
        _config.RconPassword = GetString("rconPassword", _config.RconPassword);
        _config.ServerPath = GetString("serverPath", _config.ServerPath);
        _config.StartBat = GetString("startBat", _config.StartBat);
        _config.ZomboidDataPath = GetString("zomboidDataPath", _config.ZomboidDataPath);
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("uiLanguage", out JsonElement langEl)
            && langEl.ValueKind == JsonValueKind.String)
        {
            string? lang = langEl.GetString();
            if (!string.IsNullOrWhiteSpace(lang))
                _config.UiLanguage = lang;
        }

        ConfigManager.Save(_config);
        SendToWeb("settings_saved", new { success = true });
        SendToWeb("log", "Settings saved.");
        HandleGetSettings();
    }

    private void HandleSaveDiscordSettings(JsonElement data)
    {
        string GetString(string name, string fallback = "")
        {
            if (data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty(name, out JsonElement value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? fallback;
            }

            return fallback;
        }

        bool GetBool(string name, bool fallback)
        {
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out JsonElement value))
                return fallback;
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(value.GetString(), out bool b) && b,
                _ => fallback
            };
        }

        _config.DiscordWebhookUrl = GetString("discordWebhookUrl", _config.DiscordWebhookUrl).Trim();
        _config.DiscordNotifyEnabled = GetBool("discordNotifyEnabled", _config.DiscordNotifyEnabled);

        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("discordEvents", out JsonElement eventsEl)
            && eventsEl.ValueKind == JsonValueKind.Array)
        {
            var slots = new List<DiscordEventSlot>();
            foreach (JsonElement item in eventsEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                string key = "";
                if (item.TryGetProperty("eventKey", out JsonElement keyEl)
                    && keyEl.ValueKind == JsonValueKind.String)
                {
                    key = keyEl.GetString() ?? "";
                }

                if (string.IsNullOrWhiteSpace(key))
                    continue;

                bool enabled = true;
                if (item.TryGetProperty("enabled", out JsonElement enEl))
                {
                    enabled = enEl.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.String => bool.TryParse(enEl.GetString(), out bool b) && b,
                        _ => true
                    };
                }

                string template = "";
                if (item.TryGetProperty("template", out JsonElement tplEl)
                    && tplEl.ValueKind == JsonValueKind.String)
                {
                    template = tplEl.GetString() ?? "";
                }

                slots.Add(new DiscordEventSlot
                {
                    EventKey = key.Trim(),
                    Enabled = enabled,
                    Template = template
                });
            }

            _config.DiscordEvents = DiscordEventCatalog.Normalize(slots, _config.DiscordCustomMessage);
            DiscordEventSlot? custom = _config.DiscordEvents
                .FirstOrDefault(s => s.EventKey == DiscordEventCatalog.CustomHourly);
            if (custom is not null)
                _config.DiscordCustomMessage = custom.Template ?? string.Empty;
        }

        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("discordCustomHours", out JsonElement hoursEl)
            && hoursEl.ValueKind == JsonValueKind.Array)
        {
            var hours = new List<int>();
            foreach (JsonElement item in hoursEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int h) && h is >= 0 and <= 23)
                    hours.Add(h);
                else if (item.ValueKind == JsonValueKind.String && int.TryParse(item.GetString(), out int parsed) && parsed is >= 0 and <= 23)
                    hours.Add(parsed);
            }
            _config.DiscordCustomHours = hours.Distinct().OrderBy(h => h).ToList();
        }

        ConfigManager.Save(_config);
        SendToWeb("discord_settings_saved", new { success = true });
        HandleGetSettings();
    }

    private async Task HandleTestDiscordWebhookAsync(JsonElement data)
    {
        string url = _config.DiscordWebhookUrl;
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("discordWebhookUrl", out JsonElement urlEl)
            && urlEl.ValueKind == JsonValueKind.String)
        {
            string? fromUi = urlEl.GetString();
            if (!string.IsNullOrWhiteSpace(fromUi))
                url = fromUi.Trim();
        }

        (bool success, string message) = await DiscordNotifier.SendAsync(
            url,
            "🔔 Zomboid Manager test message — webhook is working.");
        SendToWeb("discord_test_result", new { success, message });
    }

    private List<string> GetConfiguredWorkshopIds()
    {
        try
        {
            string? path = _config.LastIniFilePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return new List<string>();

            Dictionary<string, string> raw = IniManager.ReadIni(path);
            return IniManager.ParseModLists(raw).WorkshopIds;
        }
        catch (Exception ex)
        {
            SendToWeb("log", "Could not read Workshop IDs for mod update check: " + ex.Message);
            return new List<string>();
        }
    }

    private async Task NotifyDiscordEventAsync(
        string eventKey,
        IReadOnlyDictionary<string, string>? placeholders = null)
    {
        if (!_config.DiscordNotifyEnabled || string.IsNullOrWhiteSpace(_config.DiscordWebhookUrl))
            return;

        _config.DiscordEvents = DiscordEventCatalog.Normalize(
            _config.DiscordEvents,
            _config.DiscordCustomMessage);

        DiscordEventSlot? slot = _config.DiscordEvents
            .FirstOrDefault(s => string.Equals(s.EventKey, eventKey, StringComparison.OrdinalIgnoreCase));

        if (slot is null || !slot.Enabled || string.IsNullOrWhiteSpace(slot.Template))
            return;

        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["time"] = DateTime.Now.ToString("HH:mm"),
            ["date"] = DateTime.Now.ToString("yyyy-MM-dd"),
            ["datetime"] = DateTime.Now.ToString("g"),
            ["hour"] = DateTime.Now.Hour.ToString("00"),
            ["mods"] = ""
        };

        if (placeholders is not null)
        {
            foreach (KeyValuePair<string, string> pair in placeholders)
                vars[pair.Key] = pair.Value ?? string.Empty;
        }

        string content = DiscordEventCatalog.ApplyTemplate(slot.Template, vars);
        if (string.IsNullOrWhiteSpace(content))
            return;

        await NotifyDiscordAsync(content);
    }

    private async Task NotifyDiscordAsync(string content)
    {
        if (!_config.DiscordNotifyEnabled || string.IsNullOrWhiteSpace(_config.DiscordWebhookUrl))
            return;

        try
        {
            (bool success, string message) = await DiscordNotifier.SendAsync(_config.DiscordWebhookUrl, content);
            if (!success)
                SendToWeb("log", "Discord notify failed: " + message);
        }
        catch (Exception ex)
        {
            SendToWeb("log", "Discord notify error: " + ex.Message);
        }
    }

    private void OnDiscordCustomTimerTick(object? sender, EventArgs e)
    {
        DateTime now = DateTime.Now;
        if (now.Minute != 0)
        {
            _lastDiscordCustomHour = null;
            return;
        }

        int hour = now.Hour;
        if (_lastDiscordCustomHour == hour)
            return;

        if (!(_config.DiscordCustomHours?.Contains(hour) ?? false))
            return;

        if (!_config.DiscordNotifyEnabled || string.IsNullOrWhiteSpace(_config.DiscordWebhookUrl))
            return;

        _lastDiscordCustomHour = hour;
        _ = NotifyDiscordEventAsync(
            DiscordEventCatalog.CustomHourly,
            new Dictionary<string, string> { ["hour"] = hour.ToString("00") });
        SendToWeb("log", $"Discord custom hourly event posted ({hour:00}:00).");
    }

    private async Task HandleGetPlayerListAsync()
    {
        try
        {
            string response = await _rconManager.SendCommandAsync(
                _config.RconHost, _config.RconPort, _config.RconPassword, "players");

            if (string.IsNullOrWhiteSpace(response)
                || response.StartsWith("RCON error", StringComparison.OrdinalIgnoreCase))
            {
                SendToWeb("player_list", new
                {
                    success = false,
                    message = string.IsNullOrWhiteSpace(response) ? "No RCON response." : response,
                    raw = response ?? "",
                    players = Array.Empty<object>()
                });
                return;
            }

            List<PlayerInfo> players = PlayerListParser.Parse(response);
            SendToWeb("player_list", new
            {
                success = true,
                raw = response,
                players = players.Select(p => new
                {
                    name = p.Name,
                    steamId = p.SteamId,
                    status = "online",
                    raw = p.Raw
                })
            });
        }
        catch (Exception ex)
        {
            SendToWeb("player_list", new
            {
                success = false,
                message = ex.Message,
                players = Array.Empty<object>()
            });
        }
    }

    private async Task HandlePlayerActionAsync(JsonElement data)
    {
        try
        {
            string action = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("action", out JsonElement a) ? a.GetString() ?? "" : "";
            string player = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("player", out JsonElement p) ? p.GetString()?.Trim() ?? "" : "";
            string reason = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("reason", out JsonElement r) ? r.GetString()?.Trim() ?? "" : "";
            string level = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("level", out JsonElement l) ? l.GetString()?.Trim() ?? "" : "";
            string password = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("password", out JsonElement pw) ? pw.GetString()?.Trim() ?? "" : "";
            string message = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("message", out JsonElement m) ? m.GetString()?.Trim() ?? "" : "";
            string steamId = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("steamId", out JsonElement sid) ? sid.GetString()?.Trim() ?? "" : "";
            string itemId = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("itemId", out JsonElement item) ? item.GetString()?.Trim() ?? "" : "";
            string quantity = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("quantity", out JsonElement qty) ? qty.GetString()?.Trim() ?? "" : "";
            string skill = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("skill", out JsonElement sk) ? sk.GetString()?.Trim() ?? "" : "";
            string xpAmount = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("xpAmount", out JsonElement xp) ? xp.GetString()?.Trim() ?? "" : "";
            string targetPlayer = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("targetPlayer", out JsonElement tp) ? tp.GetString()?.Trim() ?? "" : "";
            string flag = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("flag", out JsonElement fl) ? fl.GetString()?.Trim() ?? "-true" : "-true";
            if (flag is not ("-true" or "-false"))
                flag = "-true";

            int qtyNum = 1;
            if (!string.IsNullOrWhiteSpace(quantity) && int.TryParse(quantity, out int parsedQty) && parsedQty > 0)
                qtyNum = parsedQty;

            string command = action.ToLowerInvariant() switch
            {
                "kick" when !string.IsNullOrWhiteSpace(player) =>
                    string.IsNullOrWhiteSpace(reason)
                        ? $"kickuser \"{player}\""
                        : $"kickuser \"{player}\" -r \"{reason}\"",
                "ban" when !string.IsNullOrWhiteSpace(player) =>
                    string.IsNullOrWhiteSpace(reason)
                        ? $"banuser \"{player}\" true"
                        : $"banuser \"{player}\" -r \"{reason}\" true",
                "banid" when !string.IsNullOrWhiteSpace(steamId) => $"banid {steamId} true",
                "unban" when !string.IsNullOrWhiteSpace(player) => $"unbanuser \"{player}\"",
                "unbanid" when !string.IsNullOrWhiteSpace(steamId) => $"unbanid {steamId}",
                "setaccess" when !string.IsNullOrWhiteSpace(player) && !string.IsNullOrWhiteSpace(level) =>
                    $"setaccesslevel \"{player}\" {level}",
                "godmode" when !string.IsNullOrWhiteSpace(player) =>
                    $"godmodeplayer \"{player}\" {flag}",
                "invisible" when !string.IsNullOrWhiteSpace(player) =>
                    $"invisibleplayer \"{player}\" {flag}",
                "teleport_to" when !string.IsNullOrWhiteSpace(player) && !string.IsNullOrWhiteSpace(targetPlayer) =>
                    $"teleportplayer \"{player}\" \"{targetPlayer}\"",
                "additem" when !string.IsNullOrWhiteSpace(player) && !string.IsNullOrWhiteSpace(itemId) =>
                    $"additem \"{player}\" \"{itemId}\" {qtyNum}",
                "addxp" when !string.IsNullOrWhiteSpace(player) && !string.IsNullOrWhiteSpace(skill) && !string.IsNullOrWhiteSpace(xpAmount) =>
                    $"addxp \"{player}\" {skill} {xpAmount}",
                "whitelist_add" when !string.IsNullOrWhiteSpace(player) && !string.IsNullOrWhiteSpace(password) =>
                    $"adduser \"{player}\" \"{password}\"",
                "whitelist_remove" when !string.IsNullOrWhiteSpace(player) =>
                    $"removeuserfromwhitelist \"{player}\"",
                "servermsg" when !string.IsNullOrWhiteSpace(message) => $"servermsg \"{message}\"",
                _ => string.Empty
            };

            if (string.IsNullOrWhiteSpace(command))
            {
                SendToWeb("player_action_result", new { success = false, message = "Invalid player action or missing fields." });
                return;
            }

            string response = await _rconManager.SendCommandAsync(
                _config.RconHost, _config.RconPort, _config.RconPassword, command);

            bool ok = !string.IsNullOrWhiteSpace(response)
                && !response.StartsWith("RCON error", StringComparison.OrdinalIgnoreCase);

            SendToWeb("player_action_result", new
            {
                success = ok,
                message = response,
                command
            });
            SendToWeb("server_console", new { line = $"> {command}" });
            SendToWeb("server_console", new { line = response });

            if (action is "kick" or "ban" or "unban" or "setaccess" or "whitelist_add" or "whitelist_remove")
                await HandleGetPlayerListAsync();
        }
        catch (Exception ex)
        {
            SendToWeb("player_action_result", new { success = false, message = ex.Message });
        }
    }

    private async Task HandleTestRconConnectionAsync()
    {
        try
        {
            string response = await _rconManager.SendCommandAsync(
                _config.RconHost,
                _config.RconPort,
                _config.RconPassword,
                "players");

            if (string.IsNullOrWhiteSpace(response)
                || response.StartsWith("RCON error:", StringComparison.OrdinalIgnoreCase))
            {
                string errorText = string.IsNullOrWhiteSpace(response)
                    ? "Keine Antwort vom Server."
                    : response["RCON error:".Length..].Trim();

                SendToWeb("rcon_test_result", new
                {
                    success = false,
                    message = errorText
                });
                return;
            }

            SendToWeb("rcon_test_result", new
            {
                success = true,
                message = "Verbindung erfolgreich"
            });
        }
        catch (Exception ex)
        {
            SendToWeb("rcon_test_result", new
            {
                success = false,
                message = ex.Message
            });
        }
    }

    private async Task HandleSendRconCommandAsync(JsonElement data)
    {
        string command = string.Empty;
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("command", out JsonElement commandElement)
            && commandElement.ValueKind == JsonValueKind.String)
        {
            command = commandElement.GetString()?.Trim() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            SendToWeb("rcon_response", new { success = false, response = "No command entered." });
            return;
        }

        string response = await _rconManager.SendCommandAsync(
            _config.RconHost,
            _config.RconPort,
            _config.RconPassword,
            command);

        bool success = !string.IsNullOrWhiteSpace(response)
            && !response.StartsWith("RCON error", StringComparison.OrdinalIgnoreCase);

        SendToWeb("rcon_response", new
        {
            success,
            response = string.IsNullOrWhiteSpace(response) ? "(empty response)" : response
        });
    }

    private void HandleGetSettings()
    {
        var payload = new
        {
            selectedHours = _config.SelectedHours ?? new List<int>(),
            schedulerActive = _config.SchedulerActive,
            announce10MinBeforeRestart = _config.Announce10MinBeforeRestart,
            announce5MinBeforeRestart = _config.Announce5MinBeforeRestart,
            serverPath = _config.ServerPath ?? string.Empty,
            startBat = _config.StartBat ?? string.Empty,
            rconHost = _config.RconHost ?? string.Empty,
            rconPort = _config.RconPort,
            rconPassword = _config.RconPassword ?? string.Empty,
            zomboidDataPath = _config.ZomboidDataPath ?? string.Empty,
            lastIniFilePath = _config.LastIniFilePath ?? string.Empty,
            uiLanguage = _config.UiLanguage ?? string.Empty,
            discordWebhookUrl = _config.DiscordWebhookUrl ?? string.Empty,
            discordNotifyEnabled = _config.DiscordNotifyEnabled,
            discordCustomMessage = _config.DiscordCustomMessage ?? string.Empty,
            discordCustomHours = _config.DiscordCustomHours ?? new List<int>(),
            discordEvents = DiscordEventCatalog.BuildUiPayload(_config.DiscordEvents, _config.DiscordCustomMessage),
            broadcastMessages = BuildBroadcastMessagesPayload(),
            backupSchedule = _backupScheduler.BuildScheduleDto(),
            modUpdateAutoRestart = BuildModUpdateAutoRestartPayload(),
            version = CurrentVersion
        };

        SendToWeb("settings_data", payload);
        SendToWeb("app_info", new { version = CurrentVersion });
        PushModUpdateRestartStatus();
    }

    private object BuildModUpdateAutoRestartPayload()
    {
        ModUpdateAutoRestartConfig cfg = _config.ModUpdateAutoRestart ?? new ModUpdateAutoRestartConfig();
        return new
        {
            enabled = cfg.Enabled,
            warningMessage = cfg.WarningMessage
                ?? "A mod has been updated. The server will restart in {minutes} minutes to apply the update.",
            warnMinutesBefore = Math.Max(0, cfg.WarnMinutesBefore),
            waitForEmpty = cfg.WaitForEmpty,
            maxWaitMinutes = Math.Max(1, cfg.MaxWaitMinutes)
        };
    }

    private List<object> BuildBroadcastMessagesPayload()
    {
        var status = _broadcastManager.BuildStatusPayload();
        // Extract list via anonymous rebuild for settings_data consistency
        return _broadcastManager.Slots.Select((slot, index) =>
        {
            DateTime now = DateTime.Now;
            string nextLabel = string.Empty;
            if (slot.Enabled && slot.NextSendAt is DateTime next)
            {
                if (next <= now)
                    nextLabel = "Next: due now";
                else
                {
                    TimeSpan delta = next - now;
                    if (delta.TotalMinutes < 1)
                        nextLabel = "Next: in under a minute";
                    else if (delta.TotalHours < 1)
                        nextLabel = $"Next: in {(int)Math.Ceiling(delta.TotalMinutes)} minutes";
                    else if (delta.TotalDays < 1)
                    {
                        int hours = (int)Math.Floor(delta.TotalHours);
                        int mins = delta.Minutes;
                        nextLabel = mins > 0 ? $"Next: in {hours}h {mins}m" : $"Next: in {hours} hours";
                    }
                    else
                        nextLabel = $"Next: {next:g}";
                }
            }

            return (object)new
            {
                index,
                message = slot.Message ?? string.Empty,
                mode = slot.Mode,
                scheduledTime = slot.ScheduledTime ?? string.Empty,
                intervalValue = slot.IntervalValue,
                intervalUnit = slot.IntervalUnit,
                enabled = slot.Enabled,
                nextSendAt = slot.NextSendAt?.ToString("o"),
                nextLabel
            };
        }).ToList();
    }

    private void HandleGetBroadcastMessages()
    {
        SendToWeb("broadcast_status", _broadcastManager.BuildStatusPayload());
    }

    private void HandleGetBackupSchedule()
    {
        SendToWeb("backup_schedule_status", _backupScheduler.BuildStatusPayload());
    }

    private async Task RunAdminCommandsSelfTestAsync()
    {
        string outPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZomboidManager",
            "admin_commands_selftest.json");

        try
        {
            await Task.Delay(1200);
            await _webView.CoreWebView2.ExecuteScriptAsync(
                "window.__adminSelfTestResult=null;window.runAdminCommandsSelfTest().then(function(r){window.__adminSelfTestResult=r;});");

            string? resultJson = null;
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(200);
                string raw = await _webView.CoreWebView2.ExecuteScriptAsync("window.__adminSelfTestResult");
                if (string.IsNullOrWhiteSpace(raw) || raw == "null")
                    continue;

                // Objects come back as JSON; strings are JSON-encoded with quotes.
                if (raw.StartsWith('{') || raw.StartsWith('['))
                    resultJson = raw;
                else
                    resultJson = JsonSerializer.Deserialize<string>(raw) ?? raw;
                break;
            }

            if (string.IsNullOrWhiteSpace(resultJson))
            {
                File.WriteAllText(outPath, """{"ok":false,"error":"no self-test result"}""");
            }
            else
            {
                File.WriteAllText(outPath, resultJson);
            }
        }
        catch (Exception ex)
        {
            File.WriteAllText(outPath, JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
        }
        finally
        {
            BeginInvoke(() =>
            {
                try { Close(); }
                catch { /* ignore */ }
            });
        }
    }

    private void HandleGetAdminCommands()
    {
        try
        {
            string? json = TryReadAdminCommandsJson();
            if (string.IsNullOrWhiteSpace(json))
            {
                SendToWeb("admin_commands_data", new
                {
                    success = false,
                    message = "admin_commands.json not found."
                });
                return;
            }

            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("commands", out JsonElement commands)
                || commands.ValueKind != JsonValueKind.Array)
            {
                SendToWeb("admin_commands_data", new
                {
                    success = false,
                    message = "admin_commands.json has no commands array."
                });
                return;
            }

            // Pass the raw document so the frontend keeps the original schema.
            SendToWeb("admin_commands_data", new
            {
                success = true,
                json
            });
        }
        catch (Exception ex)
        {
            SendToWeb("admin_commands_data", new { success = false, message = ex.Message });
        }
    }

    private string? TryReadAdminCommandsJson()
    {
        string contentRoot = ResolveContentRoot();
        string diskPath = Path.Combine(contentRoot, "wwwroot", "data", "admin_commands.json");
        if (File.Exists(diskPath))
            return File.ReadAllText(diskPath);

        Assembly assembly = Assembly.GetExecutingAssembly();
        const string resourceName = "ui/wwwroot/data/admin_commands.json";
        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return null;

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void HandleListLogs()
    {
        try
        {
            List<LogFileInfo> logs = LogViewer.ListImportantLogs(_config);
            SendToWeb("log_list", new
            {
                success = true,
                zomboidLogsPath = string.IsNullOrWhiteSpace(_config.ZomboidDataPath)
                    ? ""
                    : Path.Combine(_config.ZomboidDataPath, "Logs"),
                serverLogsPath = string.IsNullOrWhiteSpace(_config.ServerPath)
                    ? ""
                    : Path.Combine(_config.ServerPath, "logs"),
                files = logs.Select(l => new
                {
                    path = l.Path,
                    name = l.Name,
                    source = l.Source,
                    sizeBytes = l.SizeBytes,
                    sizeLabel = FormatSize(l.SizeBytes),
                    lastWrite = l.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
                })
            });
        }
        catch (Exception ex)
        {
            SendToWeb("log_list", new { success = false, message = ex.Message, files = Array.Empty<object>() });
        }
    }

    private void HandleReadLog(JsonElement data)
    {
        string path = "";
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("path", out JsonElement p)
            && p.ValueKind == JsonValueKind.String)
        {
            path = p.GetString() ?? "";
        }

        (bool success, string message, string content, bool truncated) = LogViewer.ReadLogTail(path, _config);
        SendToWeb("log_content", new
        {
            success,
            message,
            path,
            content,
            truncated
        });
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / (1024.0 * 1024.0):0.#} MB";
    }

    private void StartHardwareMonitor()
    {
        _hardwarePollingActive = true;
        if (!_hardwareTimer.Enabled)
            _hardwareTimer.Start();
        PushHardwareStats();
    }

    private void StopHardwareMonitor()
    {
        _hardwarePollingActive = false;
        _hardwareTimer.Stop();
    }

    private void PushHardwareStats()
    {
        try
        {
            object hardware = _hardwareMonitor.Snapshot(_config.ServerPath);
            var ports = new List<object>();
            string iniPath = _config.LastIniFilePath;
            if (!string.IsNullOrWhiteSpace(iniPath) && File.Exists(iniPath))
            {
                Dictionary<string, string> ini = IniManager.ReadIni(iniPath);
                void AddPort(string key, string label)
                {
                    if (ini.TryGetValue(key, out string? v) && !string.IsNullOrWhiteSpace(v))
                        ports.Add(new { name = label, value = v });
                }

                AddPort("DefaultPort", "DefaultPort");
                AddPort("UDPPort", "UDPPort");
                AddPort("SteamPort1", "SteamPort1");
                AddPort("SteamPort2", "SteamPort2");
                AddPort("RCONPort", "RCONPort (ini)");
            }

            ports.Add(new { name = "RCON (settings)", value = _config.RconPort.ToString() });

            string adminsNote =
                "Access levels are not exposed by the RCON players command. Use Player Management → Set access.";

            SendToWeb("hardware_stats", new
            {
                hardware,
                ports,
                adminsNote,
                admins = Array.Empty<string>()
            });
        }
        catch (Exception ex)
        {
            SendToWeb("log", "Hardware stats failed: " + ex.Message);
        }
    }

    private void HandleSaveBackupSchedule(JsonElement data)
    {
        var schedule = new BackupScheduleConfig();
        JsonElement root = data;
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("backupSchedule", out JsonElement nested)
            && nested.ValueKind == JsonValueKind.Object)
        {
            root = nested;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("enabled", out JsonElement en))
            {
                schedule.Enabled = en.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => bool.TryParse(en.GetString(), out bool b) && b,
                    _ => false
                };
            }

            schedule.Mode = root.TryGetProperty("mode", out JsonElement modeEl)
                ? modeEl.GetString() ?? "recurring"
                : "recurring";
            schedule.Time = root.TryGetProperty("time", out JsonElement timeEl)
                ? timeEl.GetString() ?? "03:00"
                : "03:00";
            schedule.OneTimeDateTime = root.TryGetProperty("oneTimeDateTime", out JsonElement ot)
                ? ot.GetString() ?? ""
                : "";

            if (root.TryGetProperty("days", out JsonElement daysEl) && daysEl.ValueKind == JsonValueKind.Array)
            {
                var days = new List<int>();
                foreach (JsonElement d in daysEl.EnumerateArray())
                {
                    if (d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out int n) && n is >= 0 and <= 6)
                        days.Add(n);
                    else if (d.ValueKind == JsonValueKind.String && int.TryParse(d.GetString(), out int p) && p is >= 0 and <= 6)
                        days.Add(p);
                }
                schedule.Days = days;
            }
        }

        _backupScheduler.UpdateSchedule(schedule);
        _config.BackupSchedule = _backupScheduler.Schedule;
        ConfigManager.Save(_config);
        SendToWeb("backup_schedule_status", _backupScheduler.BuildStatusPayload());
        SendToWeb("log", "Backup schedule saved.");
    }

    private void HandleSaveBroadcastMessages(JsonElement data)
    {
        var slots = new List<BroadcastMessageSlot>();
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("broadcastMessages", out JsonElement arr)
            && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                string mode = item.TryGetProperty("mode", out JsonElement modeEl)
                    ? modeEl.GetString() ?? "oneTime"
                    : "oneTime";
                string unit = item.TryGetProperty("intervalUnit", out JsonElement unitEl)
                    ? unitEl.GetString() ?? "hours"
                    : "hours";
                int interval = 1;
                if (item.TryGetProperty("intervalValue", out JsonElement iv))
                {
                    if (iv.ValueKind == JsonValueKind.Number && iv.TryGetInt32(out int n))
                        interval = n;
                    else if (iv.ValueKind == JsonValueKind.String && int.TryParse(iv.GetString(), out int parsed))
                        interval = parsed;
                }

                bool enabled = false;
                if (item.TryGetProperty("enabled", out JsonElement en))
                {
                    enabled = en.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.String => bool.TryParse(en.GetString(), out bool b) && b,
                        _ => false
                    };
                }

                slots.Add(new BroadcastMessageSlot
                {
                    Message = item.TryGetProperty("message", out JsonElement msg) ? msg.GetString() ?? "" : "",
                    Mode = mode,
                    ScheduledTime = item.TryGetProperty("scheduledTime", out JsonElement st) ? st.GetString() ?? "" : "",
                    IntervalValue = Math.Max(1, interval),
                    IntervalUnit = unit,
                    Enabled = enabled
                });
            }
        }

        _broadcastManager.UpdateSlots(slots);
        _config.BroadcastMessages = _broadcastManager.Slots.ToList();
        ConfigManager.Save(_config);
        SendToWeb("broadcast_status", _broadcastManager.BuildStatusPayload());
        SendToWeb("log", "Broadcast messages saved.");
    }

    private async Task HandleSendBroadcastNowAsync(JsonElement data)
    {
        int index = -1;
        string text = "";

        if (data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("index", out JsonElement indexEl)
                && indexEl.TryGetInt32(out int parsedIndex))
            {
                index = parsedIndex;
            }

            if (data.TryGetProperty("message", out JsonElement msgEl)
                && msgEl.ValueKind == JsonValueKind.String)
            {
                text = (msgEl.GetString() ?? "").Trim();
            }
        }

        if (index < 0 || index >= BroadcastManager.MaxSlots)
        {
            SendToWeb("broadcast_send_result", new
            {
                success = false,
                index,
                message = "Invalid broadcast slot."
            });
            return;
        }

        if (string.IsNullOrWhiteSpace(text)
            && index < _broadcastManager.Slots.Count)
        {
            text = (_broadcastManager.Slots[index].Message ?? "").Trim();
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            SendToWeb("broadcast_send_result", new
            {
                success = false,
                index,
                message = "Message text is empty."
            });
            return;
        }

        try
        {
            if (!IsJavaServerOnline())
            {
                SendToWeb("broadcast_send_result", new
                {
                    success = false,
                    index,
                    message = "Server offline — message not sent."
                });
                return;
            }

            string command = BroadcastRconCommand.FormatOutgoingMessage(text);
            SendToWeb("log", $"Broadcast slot {index + 1} send-now: {command}");

            string response = await _rconManager.SendCommandAsync(
                _config.RconHost,
                _config.RconPort,
                _config.RconPassword,
                command);

            bool success = !string.IsNullOrWhiteSpace(response)
                && !response.StartsWith("RCON error", StringComparison.OrdinalIgnoreCase);

            // Intentional: do not MarkSent / alter schedule for manual sends.
            SendToWeb("broadcast_send_result", new
            {
                success,
                index,
                message = success
                    ? $"Slot {index + 1} sent now."
                    : (string.IsNullOrWhiteSpace(response) ? "RCON send failed." : response)
            });
            SendToWeb("log", success
                ? $"Broadcast slot {index + 1} sent now (schedule unchanged)."
                : $"Broadcast slot {index + 1} send-now failed: {response}");
        }
        catch (Exception ex)
        {
            SendToWeb("broadcast_send_result", new
            {
                success = false,
                index,
                message = ex.Message
            });
            SendToWeb("log", $"Broadcast slot {index + 1} send-now failed: {ex.Message}");
        }
    }

    private async Task HandleBroadcastDueAsync(int index, BroadcastMessageSlot slot)
    {
        lock (_broadcastInFlight)
        {
            if (!_broadcastInFlight.Add(index))
                return;
        }

        try
        {
            if (!IsJavaServerOnline())
            {
                SendToWeb("log", $"Broadcast slot {index + 1} skipped (server offline).");
                return;
            }

            string text = (slot.Message ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(text))
                return;

            string command = BroadcastRconCommand.FormatOutgoingMessage(text);

            string response = await _rconManager.SendCommandAsync(
                _config.RconHost,
                _config.RconPort,
                _config.RconPassword,
                command);

            bool success = !string.IsNullOrWhiteSpace(response)
                && !response.StartsWith("RCON error", StringComparison.OrdinalIgnoreCase);

            if (!success)
            {
                SendToWeb("log", $"Broadcast slot {index + 1} failed (RCON): {response}");
                return;
            }

            _broadcastManager.MarkSent(index);
            _config.BroadcastMessages = _broadcastManager.Slots.ToList();
            ConfigManager.Save(_config);
            SendToWeb("log", $"Broadcast slot {index + 1} sent.");
            SendToWeb("broadcast_status", _broadcastManager.BuildStatusPayload());
        }
        catch (Exception ex)
        {
            SendToWeb("log", $"Broadcast slot {index + 1} failed: {ex.Message}");
        }
        finally
        {
            lock (_broadcastInFlight)
                _broadcastInFlight.Remove(index);
        }
    }

    private static bool IsJavaServerOnline()
    {
        Process[] javaProcesses = Process.GetProcessesByName("java");
        try
        {
            return javaProcesses.Length > 0;
        }
        finally
        {
            foreach (Process process in javaProcesses)
                process.Dispose();
        }
    }

    private async Task HandleGetServerStatusAsync()
    {
        bool isOnline = false;
        Process[] javaProcesses = Process.GetProcessesByName("java");
        try
        {
            isOnline = javaProcesses.Length > 0;
        }
        finally
        {
            foreach (Process process in javaProcesses)
                process.Dispose();
        }

        string players = "–";
        if (isOnline && !string.IsNullOrWhiteSpace(_config.RconPassword))
        {
            try
            {
                Task<string> rconTask = _rconManager.SendCommandAsync(
                    _config.RconHost,
                    _config.RconPort,
                    _config.RconPassword,
                    "players");

                Task completed = await Task.WhenAny(rconTask, Task.Delay(TimeSpan.FromSeconds(4)));
                if (completed == rconTask)
                {
                    string response = await rconTask;
                    players = string.IsNullOrWhiteSpace(response) || response.StartsWith("RCON error", StringComparison.OrdinalIgnoreCase)
                        ? "–"
                        : response.Trim();
                }
            }
            catch
            {
                players = "–";
            }
        }

        string lastRestart = _lastRestartTime.HasValue
            ? _lastRestartTime.Value.ToString("dd.MM.yyyy HH:mm:ss")
            : "No restart in this session yet";

        var payload = new
        {
            status = isOnline ? "online" : "offline",
            address = $"{_config.RconHost}:{_config.RconPort}",
            players,
            lastRestart
        };

        SendToWeb("server_status", payload);
    }

    private void WireModUpdateRestartEvents()
    {
        _modUpdateRestartFlow.StatusChanged += () =>
        {
            try
            {
                if (InvokeRequired)
                    BeginInvoke(PushModUpdateRestartStatus);
                else
                    PushModUpdateRestartStatus();
            }
            catch
            {
                // ignore UI push failures
            }
        };

        _modUpdateRestartFlow.Completed += (success, message, mods) =>
        {
            try
            {
                void Finish()
                {
                    PushModUpdateRestartStatus();
                    if (success && mods.Count > 0)
                    {
                        _ = NotifyDiscordEventAsync(
                            DiscordEventCatalog.ModsUpdated,
                            new Dictionary<string, string> { ["mods"] = string.Join(", ", mods) });
                    }
                    else if (!success && !string.Equals(message, "Cancelled.", StringComparison.Ordinal))
                    {
                        SendToWeb("log",
                            "Mod update was detected but automatic restart failed and needs manual attention: "
                            + message);
                        _ = NotifyDiscordAsync(
                            "⚠️ Mod update detected but automatic restart failed: " + message
                            + (mods.Count > 0 ? "\nMods: " + string.Join(", ", mods) : ""));
                    }
                }

                if (InvokeRequired)
                    BeginInvoke(Finish);
                else
                    Finish();
            }
            catch
            {
                // ignore
            }
        };
    }

    private void ApplyModUpdatePollTimer()
    {
        bool enabled = _config.ModUpdateAutoRestart?.Enabled == true;
        if (enabled)
        {
            if (!_modUpdatePollTimer.Enabled)
                _modUpdatePollTimer.Start();
        }
        else
        {
            _modUpdatePollTimer.Stop();
        }
    }

    private void PushModUpdateRestartStatus()
    {
        SendToWeb("mod_update_restart_status", _modUpdateRestartFlow.BuildStatusPayload());
    }

    private void HandleSaveModUpdateAutoRestart(JsonElement data)
    {
        ModUpdateAutoRestartConfig cfg = _config.ModUpdateAutoRestart ?? new ModUpdateAutoRestartConfig();

        bool GetBool(string name, bool fallback)
        {
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out JsonElement value))
                return fallback;
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(value.GetString(), out bool b) && b,
                _ => fallback
            };
        }

        int GetInt(string name, int fallback)
        {
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out JsonElement value))
                return fallback;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n))
                return n;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out int parsed))
                return parsed;
            return fallback;
        }

        string GetString(string name, string fallback)
        {
            if (data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty(name, out JsonElement value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? fallback;
            }

            return fallback;
        }

        cfg.Enabled = GetBool("enabled", cfg.Enabled);
        cfg.WarningMessage = GetString("warningMessage", cfg.WarningMessage);
        if (string.IsNullOrWhiteSpace(cfg.WarningMessage))
        {
            cfg.WarningMessage =
                "A mod has been updated. The server will restart in {minutes} minutes to apply the update.";
        }

        cfg.WarnMinutesBefore = Math.Clamp(GetInt("warnMinutesBefore", cfg.WarnMinutesBefore), 0, 240);
        cfg.WaitForEmpty = GetBool("waitForEmpty", cfg.WaitForEmpty);
        cfg.MaxWaitMinutes = Math.Clamp(GetInt("maxWaitMinutes", cfg.MaxWaitMinutes), 1, 1440);

        _config.ModUpdateAutoRestart = cfg;
        ConfigManager.Save(_config);
        ApplyModUpdatePollTimer();
        SendToWeb("mod_update_auto_restart_saved", new { success = true });
        SendToWeb("log", "Mod Update Auto-Restart settings saved.");
        HandleGetSettings();
    }

    private void HandleModUpdateCancelRestart()
    {
        if (_modUpdateRestartFlow.TryCancel())
        {
            SendToWeb("log", "Mod-update pending restart cancelled.");
            PushModUpdateRestartStatus();
        }
        else
        {
            SendToWeb("log", "No cancellable mod-update restart pending.");
            PushModUpdateRestartStatus();
        }
    }

    private async Task HandleModUpdateRestartNowAsync()
    {
        if (_restartInProgress || _modUpdateRestartFlow.IsActive)
        {
            SendToWeb("log", "Restart already in progress.");
            PushModUpdateRestartStatus();
            return;
        }

        await StartModUpdateRestartFlowAsync(
            updatedMods: Array.Empty<string>(),
            countdownOverride: TimeSpan.Zero,
            skipCountdown: true);
    }

    private async Task HandleModUpdateScheduleRestartAsync(JsonElement data)
    {
        int minutes = 5;
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("minutes", out JsonElement m)
            && m.ValueKind == JsonValueKind.Number
            && m.TryGetInt32(out int parsed))
        {
            minutes = parsed;
        }
        else if (data.ValueKind == JsonValueKind.Object
                 && data.TryGetProperty("minutes", out JsonElement ms)
                 && ms.ValueKind == JsonValueKind.String
                 && int.TryParse(ms.GetString(), out int fromString))
        {
            minutes = fromString;
        }

        minutes = Math.Clamp(minutes, 1, 240);
        if (_restartInProgress || _modUpdateRestartFlow.IsActive)
        {
            SendToWeb("log", "Cannot schedule: a restart is already in progress.");
            PushModUpdateRestartStatus();
            return;
        }

        await StartModUpdateRestartFlowAsync(
            updatedMods: Array.Empty<string>(),
            countdownOverride: TimeSpan.FromMinutes(minutes),
            skipCountdown: false);
    }

    private async Task RunScheduledModUpdateCheckAsync(bool fromManual)
    {
        if (_modUpdateCheckInFlight)
            return;

        _modUpdateCheckInFlight = true;
        try
        {
            List<string> ids = GetConfiguredWorkshopIds();
            if (ids.Count == 0)
            {
                SendToWeb("mod_update_check_result", new
                {
                    success = true,
                    fromManual,
                    updated = false,
                    mods = Array.Empty<string>(),
                    message = "No Workshop IDs configured (load a server.ini in Config first)."
                });
                return;
            }

            SendToWeb("log", fromManual
                ? "Manual Workshop mod update check…"
                : "Scheduled Workshop mod update check…");

            IReadOnlyList<string> updated = await ModUpdateChecker.CheckForUpdatedModsAsync(
                ids,
                msg => SendToWeb("log", msg));

            if (updated.Count == 0)
            {
                SendToWeb("mod_update_check_result", new
                {
                    success = true,
                    fromManual,
                    updated = false,
                    mods = Array.Empty<string>(),
                    message = "No mod updates detected."
                });
                return;
            }

            string joined = string.Join(", ", updated);
            SendToWeb("mod_update_check_result", new
            {
                success = true,
                fromManual,
                updated = true,
                mods = updated.ToList(),
                message = "Mods updated: " + joined
            });
            SendToWeb("log", "Workshop updates detected: " + joined);

            bool auto = _config.ModUpdateAutoRestart?.Enabled == true;
            if (auto)
            {
                if (_restartInProgress || _modUpdateRestartFlow.IsActive)
                {
                    SendToWeb("log",
                        "Mod updates found but a restart is already in progress — Discord notify only.");
                    await NotifyDiscordEventAsync(
                        DiscordEventCatalog.ModsUpdated,
                        new Dictionary<string, string> { ["mods"] = joined });
                    return;
                }

                await StartModUpdateRestartFlowAsync(
                    updatedMods: updated,
                    countdownOverride: null,
                    skipCountdown: false);
            }
            else
            {
                await NotifyDiscordEventAsync(
                    DiscordEventCatalog.ModsUpdated,
                    new Dictionary<string, string> { ["mods"] = joined });
            }
        }
        catch (Exception ex)
        {
            SendToWeb("mod_update_check_result", new
            {
                success = false,
                fromManual,
                updated = false,
                mods = Array.Empty<string>(),
                message = ex.Message
            });
            SendToWeb("log", "Mod update check failed: " + ex.Message);
        }
        finally
        {
            _modUpdateCheckInFlight = false;
        }
    }

    private async Task StartModUpdateRestartFlowAsync(
        IReadOnlyList<string> updatedMods,
        TimeSpan? countdownOverride,
        bool skipCountdown)
    {
        string? batPath = ResolveStartBatPath();
        if (batPath is null)
        {
            SendToWeb("log", "Mod-update restart aborted: start file not found.");
            SendToWeb("mod_update_check_result", new
            {
                success = false,
                message = "Start file not found. Configure Server folder + Start .bat in Settings."
            });
            return;
        }

        if (_restartInProgress)
        {
            SendToWeb("log", "Mod-update restart aborted: another restart is in progress.");
            return;
        }

        ModUpdateAutoRestartConfig settings = _config.ModUpdateAutoRestart ?? new ModUpdateAutoRestartConfig();
        _modUpdateStatusTimer.Start();
        PushModUpdateRestartStatus();

        await _modUpdateRestartFlow.RunAsync(
            settings,
            updatedMods,
            countdownOverride,
            skipCountdown,
            async command =>
            {
                string response = await _rconManager.SendCommandAsync(
                    _config.RconHost,
                    _config.RconPort,
                    _config.RconPassword,
                    command);
                SendToWeb("log", $"RCON: {command} → {response}");
                return response;
            },
            GetOnlinePlayerCountAsync,
            async ct =>
            {
                _restartInProgress = true;
                try
                {
                    await RunCleanSaveQuitStartAsync(batPath, ct);
                }
                finally
                {
                    _restartInProgress = false;
                }
            },
            msg =>
            {
                SendToWeb("log", msg);
                SendToWeb("server_console", new { line = msg });
            });
    }

    private async Task<int> GetOnlinePlayerCountAsync()
    {
        string response = await _rconManager.SendCommandAsync(
            _config.RconHost,
            _config.RconPort,
            _config.RconPassword,
            "players");

        if (string.IsNullOrWhiteSpace(response)
            || response.StartsWith("RCON error", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(response) ? "Empty RCON players response." : response);
        }

        return PlayerListParser.Parse(response).Count;
    }

    /// <summary>
    /// Shared clean shutdown + start used by scheduled/manual restart and mod-update restart.
    /// </summary>
    private async Task RunCleanSaveQuitStartAsync(string batPath, CancellationToken ct = default)
    {
        void Mirror(string text)
        {
            SendToWeb("log", text);
            SendToWeb("server_console", new { line = text });
        }

        string host = _config.RconHost;
        int port = _config.RconPort;
        string password = _config.RconPassword;

        ct.ThrowIfCancellationRequested();
        Mirror("RCON: save");
        string saveResponse = await _rconManager.SendCommandAsync(host, port, password, "save");
        Mirror($"RCON response: {saveResponse}");
        if (string.IsNullOrWhiteSpace(saveResponse)
            || saveResponse.StartsWith("RCON error", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("RCON save failed: " + saveResponse);
        }

        Mirror("Waiting 5 seconds...");
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        Mirror("RCON: quit");
        string quitResponse = await _rconManager.SendCommandAsync(host, port, password, "quit");
        Mirror($"RCON response: {quitResponse}");

        Mirror("Waiting 15 seconds for a clean shutdown...");
        await Task.Delay(TimeSpan.FromSeconds(15), CancellationToken.None);

        Process[] remainingJava = Process.GetProcessesByName("java");
        try
        {
            if (remainingJava.Length > 0)
            {
                Mirror($"Fallback: {remainingJava.Length} Java process(es) still running – KillServerTree.");
                foreach (string message in _serverProcessManager.KillServerTree())
                    Mirror(message);
            }
            else
            {
                Mirror("No Java process left – clean shutdown succeeded.");
                foreach (string message in _serverProcessManager.CloseOrphanServerConsoles())
                    Mirror(message);
            }
        }
        finally
        {
            foreach (Process javaProc in remainingJava)
                javaProc.Dispose();
        }

        Mirror($"Starting server: {batPath}");
        Process started = _serverProcessManager.StartServer(batPath, embedConsole: true);
        Mirror($"Server started (PID {started.Id}).");
        SendToWeb("server_console", new { line = $"Started embedded process PID {started.Id}" });
        _lastRestartTime = DateTime.Now;
        await HandleGetServerStatusAsync();
    }

    private async Task ExecuteRestartRoutine()
    {
        if (_restartInProgress || _modUpdateRestartFlow.IsActive)
        {
            SendToWeb("log", "Restart skipped (a restart is already in progress).");
            return;
        }

        string batPath = Path.Combine(_config.ServerPath ?? string.Empty, _config.StartBat ?? string.Empty);
        if (!File.Exists(batPath))
        {
            SendToWeb("log", $"Server start aborted: start file not found: {batPath}");
            return;
        }

        _restartInProgress = true;
        try
        {
            void Mirror(string text)
            {
                SendToWeb("log", text);
                SendToWeb("server_console", new { line = text });
            }

            string host = _config.RconHost;
            int port = _config.RconPort;
            string password = _config.RconPassword;

            Mirror("Restart routine started.");
            await NotifyDiscordEventAsync(DiscordEventCatalog.RestartRoutineStarted);
            Mirror($"Start file: {batPath}");

            Mirror("RCON: servermsg \"Server restart in 1 minute!\"");
            string msgResponse = await _rconManager.SendCommandAsync(
                host, port, password, "servermsg \"Server restart in 1 minute!\"");
            Mirror($"RCON response: {msgResponse}");

            Mirror("Waiting 55 seconds...");
            await Task.Delay(TimeSpan.FromSeconds(55));

            await RunCleanSaveQuitStartAsync(batPath);

            IReadOnlyList<string> updatedMods = Array.Empty<string>();
            try
            {
                updatedMods = await ModUpdateChecker.CheckForUpdatedModsAsync(
                    GetConfiguredWorkshopIds(),
                    msg => Mirror(msg));
                if (updatedMods.Count > 0)
                    Mirror("Discord mod update note: " + string.Join(", ", updatedMods));
            }
            catch (Exception modEx)
            {
                Mirror("Mod update check skipped: " + modEx.Message);
            }

            string modsText = updatedMods.Count > 0 ? string.Join(", ", updatedMods) : "";
            await NotifyDiscordEventAsync(
                DiscordEventCatalog.ServerRestarted,
                new Dictionary<string, string> { ["mods"] = modsText });

            if (updatedMods.Count > 0)
            {
                await NotifyDiscordEventAsync(
                    DiscordEventCatalog.ModsUpdated,
                    new Dictionary<string, string> { ["mods"] = modsText });
            }

            Mirror("Restart routine finished.");
        }
        catch (Exception ex)
        {
            SendToWeb("log", $"Error in restart routine: {ex.Message}");
            SendToWeb("server_console", new { line = $"Error in restart routine: {ex.Message}" });
        }
        finally
        {
            _restartInProgress = false;
        }
    }

    private void SendToWeb(string type, object payload)
    {
        if (_webView?.CoreWebView2 is null)
            return;

        void Post()
        {
            var envelope = new
            {
                type,
                payload
            };

            string json = JsonSerializer.Serialize(envelope, JsonOptions);
            _webView.CoreWebView2.PostWebMessageAsJson(json);
        }

        if (InvokeRequired)
            BeginInvoke(Post);
        else
            Post();
    }
}
