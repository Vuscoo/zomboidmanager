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
    private bool _steamCmdUpdateInFlight;
    /// <summary>Normalized release tag the user dismissed in this session's startup prompt (no re-nag).</summary>
    private string? _startupUpdateDismissedVersion;

    private readonly ScheduleManager _scheduleManager = new();
    private readonly RconManager _rconManager = new();
    private readonly ServerProcessManager _serverProcessManager = new();
    private readonly BroadcastManager _broadcastManager = new();
    private readonly BackupScheduler _backupScheduler = new();
    private readonly SchedulerHeartbeat _schedulerHeartbeat = new();
    private readonly HardwareMonitor _hardwareMonitor = new();
    private readonly StatsHistoryStore _statsHistory = new();
    private readonly StatsCollector _statsCollector;
    private readonly ModUpdateRestartFlow _modUpdateRestartFlow = new();
    private readonly System.Windows.Forms.Timer _hardwareTimer;
    private readonly System.Windows.Forms.Timer _modUpdatePollTimer;
    private readonly System.Windows.Forms.Timer _modUpdateStatusTimer;
    private bool _hardwarePollingActive;
    private bool _deferredServicesStarted;
    private List<object> _cachedHardwarePorts = new();
    private bool _modUpdateCheckInFlight;
    private bool _userStopInProgress;
    private readonly Stopwatch _startupWatch = Stopwatch.StartNew();
    private ServerStartupWatcher? _serverStartupWatcher;
    private CancellationTokenSource? _serverStartupWatchCts;
    /// <summary>True while waiting for *** SERVER STARTED *** after a manager-launched boot.</summary>
    private bool _awaitingServerReady;
    /// <summary>True after the SERVER STARTED log marker was seen for the current boot.</summary>
    private bool _serverReadyConfirmed;

    private AppConfig _config;
    private string Ui(string key) => AppLocalizer.Get(_config.UiLanguage, key);
    private string Ui(string key, params object[] args) => AppLocalizer.Format(_config.UiLanguage, key, args);

    private string ResolveSchedulerLog(string raw)
    {
        if (raw.StartsWith("scheduler.preAnnounce|", StringComparison.Ordinal))
        {
            string[] parts = raw.Split('|');
            if (parts.Length >= 3)
                return Ui("scheduler.preAnnounce", parts[1], parts[2]);
        }

        if (raw.StartsWith("scheduler.restartHour|", StringComparison.Ordinal))
        {
            string[] parts = raw.Split('|');
            if (parts.Length >= 2)
                return Ui("scheduler.restartHour", parts[1]);
        }

        return Ui(raw);
    }

    private WebView2 _webView = null!;
    private DateTime? _lastRestartTime;
    private bool _restartInProgress;
    private readonly bool _isFirstStart;
    private int? _lastDiscordCustomHour;
    private bool _backupInProgress;
    private CancellationTokenSource? _backupCts;
    private readonly HashSet<int> _broadcastInFlight = new();
    private LogAnalyzeSession? _logSession;
    private CancellationTokenSource? _logAnalyzeCts;
    private readonly object _logAnalyzeLock = new();
    private readonly string? _configBrokenBackupPath;
    private readonly string? _configLoadErrorDetail;
    private bool _allowClose;
    private bool _configBrokenWarningShown;

    public MainForm()
    {
        var (loaded, brokenBackup, loadError) = ConfigManager.LoadWithStatus();
        _config = loaded;
        _configBrokenBackupPath = brokenBackup;
        _configLoadErrorDetail = loadError;
        _config.DiscordEvents = DiscordEventCatalog.Normalize(_config.DiscordEvents, _config.DiscordCustomMessage);
        _config.ModUpdateAutoRestart ??= new ModUpdateAutoRestartConfig();
        _config.StatsIntervalMinutes = StatsHistoryStore.ClampIntervalMinutes(
            _config.StatsIntervalMinutes <= 0 ? StatsHistoryStore.DefaultIntervalMinutes : _config.StatsIntervalMinutes);
        _config.StatsRetentionDays = StatsHistoryStore.ClampRetentionDays(
            _config.StatsRetentionDays <= 0 ? StatsHistoryStore.DefaultRetentionDays : _config.StatsRetentionDays);
        _isFirstStart = string.IsNullOrWhiteSpace(_config.ServerPath)
            || string.IsNullOrWhiteSpace(_config.UiLanguage);

        _statsCollector = new StatsCollector(
            _statsHistory,
            IsJavaServerOnline,
            () => _hardwareMonitor.GetCachedOrSample(_config.ServerPath),
            TryGetPlayerCountAsync,
            () => _config.StatsIntervalMinutes,
            () => _config.StatsRetentionDays);

        InitializeComponent();

        Text = "Zomboid Manager";
        Size = new Size(1100, 750);
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = AppTheme.BackgroundDark;
        ApplyAppIcon();

        _serverProcessManager.SetServerContext(_config.ServerPath, _config.StartBat);
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
        // SchedulerHeartbeat + StatsCollector start after WebView is interactive
        // (see StartDeferredBackgroundServices) so ctor stays light.

        _hardwareTimer = new System.Windows.Forms.Timer { Interval = 3000 };
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
        DateTime?[] previousNextSendAt = _config.BroadcastMessages
            .Select(s => s.NextSendAt)
            .ToArray();

        _broadcastManager.UpdateSlots(_config.BroadcastMessages);
        _config.BroadcastMessages = _broadcastManager.Slots.ToList();

        // Only persist when recalculated NextSendAt values actually changed.
        bool nextSendChanged = !_config.BroadcastMessages
            .Select(s => s.NextSendAt)
            .SequenceEqual(previousNextSendAt);
        if (nextSendChanged)
            ConfigManager.Save(_config);
    }

    private void WireBackupSchedulerEvents()
    {
        _backupScheduler.BackupDue += () =>
        {
            SendToWeb("log", Ui("backup.scheduledTriggered"));
            _ = HandleCreateBackupAsync(fromScheduler: true);
        };
        _backupScheduler.ScheduleDisabled += () =>
        {
            _config.BackupSchedule = _backupScheduler.Schedule;
            ConfigManager.Save(_config);
            SendToWeb("backup_schedule_status", _backupScheduler.BuildStatusPayload());
            SendToWeb("log", Ui("backup.oneTimeDisabled"));
        };
    }

    private void RestoreBackupScheduleFromConfig()
    {
        _config.BackupSchedule = BackupScheduler.Normalize(_config.BackupSchedule);
        _backupScheduler.UpdateSchedule(_config.BackupSchedule);
    }

    private void StartSchedulerHeartbeat()
    {
        _schedulerHeartbeat.Register(() => _scheduleManager.Tick());
        _schedulerHeartbeat.Register(() => _backupScheduler.Tick());
        _schedulerHeartbeat.Register(() => _broadcastManager.Tick());
        _schedulerHeartbeat.Register(TickDiscordCustomHourly);
        _schedulerHeartbeat.Start();
    }

    /// <summary>
    /// Start schedule heartbeat + stats sampling as soon as the UI is ready —
    /// not in the constructor — so launch stays responsive without missing due work.
    /// </summary>
    private void StartDeferredBackgroundServices()
    {
        if (_deferredServicesStarted)
            return;
        _deferredServicesStarted = true;

        StartSchedulerHeartbeat();
        _statsCollector.Start();

        Debug.WriteLine(
            $"[startup] deferred services started at {_startupWatch.ElapsedMilliseconds} ms " +
            $"(heartbeat + StatsCollector)");
    }

    private void WireScheduleEvents()
    {
        _scheduleManager.LogMessage += message =>
        {
            string line = ResolveSchedulerLog(message);
            SendToWeb("log", line);
            SendToWeb("server_console", new { line = line });
        };
        _scheduleManager.RestartTriggered += hour =>
        {
            string msg = Ui("scheduler.scheduledRestart", hour);
            SendToWeb("log", msg);
            SendToWeb("server_console", new { line = msg });
            _ = ExecuteRestartRoutine(RestartReasons.Scheduled);
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
                _serverStartupWatcher?.ObserveLine(line);
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
                ClearServerBootState();
                SendToWeb("server_console", new { line = Ui("server.processExited") });
                if (!_restartInProgress && !_userStopInProgress)
                    _statsCollector.NotifyUnexpectedExit();
                // Cheap online/offline only — may fire while Server tab is hidden.
                _ = HandleGetServerStatusAsync(includePlayers: false);
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

        // Subscribe before Navigate so a fast first load cannot miss the event.
        _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        _webView.CoreWebView2.NavigationCompleted += (_, args) =>
        {
            if (!args.IsSuccess)
                return;

            StartDeferredBackgroundServices();
            Debug.WriteLine(
                $"[startup] WebView NavigationCompleted at {_startupWatch.ElapsedMilliseconds} ms");

            // Non-blocking: GitHub check after UI is interactive; popup only if newer.
            _ = MaybePromptStartupUpdateAsync();
            MaybeWarnBrokenConfig();

            SendToWeb("app_info", new { version = CurrentVersion });
            if (Environment.GetCommandLineArgs().Any(a =>
                    string.Equals(a, "--selftest-admin-commands", StringComparison.OrdinalIgnoreCase)))
            {
                _ = RunAdminCommandsSelfTestAsync();
            }
        };
        _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);

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
                    _ = HandleGetServerStatusAsync(ReadIncludePlayersFlag(message.Data));
                    break;

                case "manual_restart":
                    _ = ExecuteRestartRoutine(RestartReasons.Manual);
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

                case "save_manual_mod_mappings":
                    HandleSaveManualModMappings(message.Data);
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

                case "list_config_profiles":
                    HandleListConfigProfiles();
                    break;

                case "save_config_profile":
                    HandleSaveConfigProfile(message.Data);
                    break;

                case "load_config_profile":
                    HandleLoadConfigProfile(message.Data);
                    break;

                case "rename_config_profile":
                    HandleRenameConfigProfile(message.Data);
                    break;

                case "delete_config_profile":
                    HandleDeleteConfigProfile(message.Data);
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

                case "update_server":
                    HandleUpdateServer();
                    break;

                case "update_server_confirmed":
                    _ = HandleUpdateServerConfirmedAsync();
                    break;

                case "create_backup":
                    _ = HandleCreateBackupAsync();
                    break;

                case "cancel_backup":
                    CancelBackupInProgress();
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

                case "get_stats_history":
                    HandleGetStatsHistory(message.Data);
                    break;

                case "save_stats_settings":
                    HandleSaveStatsSettings(message.Data);
                    break;

                case "list_logs":
                    HandleListLogs();
                    break;

                case "clear_log_session":
                    ClearLogAnalyzeSession();
                    break;

                case "read_log":
                    HandleReadLog(message.Data);
                    break;

                case "analyze_log":
                    HandleAnalyzeLog(message.Data);
                    break;

                case "query_log_entries":
                    HandleQueryLogEntries(message.Data);
                    break;

                case "get_log_context":
                    HandleGetLogContext(message.Data);
                    break;

                case "get_log_system_info":
                    HandleGetLogSystemInfo();
                    break;

                case "get_log_mod_info":
                    HandleGetLogModInfo();
                    break;

                case "browse_log_file":
                    BrowseLogFile();
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

                case "get_whitelist":
                    HandleGetWhitelist();
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
            return "1.1.0";
        return $"{asm.Major}.{asm.Minor}.{asm.Build}";
    }

    /// <summary>
    /// Background GitHub check after the UI is ready. Silent on failure; Yes/No popup only when newer.
    /// </summary>
    private async Task MaybePromptStartupUpdateAsync()
    {
        try
        {
            // Let the first paint / settings hydrate finish before any network work.
            await Task.Delay(1500);
            if (IsDisposed || _updateInProgress)
                return;

            AppUpdater.CheckResult check = await AppUpdater.CheckAsync(CurrentVersion);
            if (!check.UpdateAvailable || string.IsNullOrWhiteSpace(check.DownloadUrl))
                return;

            string tag = check.LatestTag ?? string.Empty;
            string normalized = AppUpdater.NormalizeVersion(tag);
            if (string.Equals(_startupUpdateDismissedVersion, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            if (IsDisposed)
                return;

            bool accepted = false;
            void Ask()
            {
                if (IsDisposed)
                    return;
                string display = tag.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                    ? tag
                    : "v" + normalized;
                DialogResult answer = MessageBox.Show(
                    this,
                    Ui("appUpdate.startupPrompt", display),
                    Ui("appUpdate.startupTitle"),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information,
                    MessageBoxDefaultButton.Button2);
                accepted = answer == DialogResult.Yes;
                if (!accepted)
                    _startupUpdateDismissedVersion = normalized;
            }

            if (InvokeRequired)
                Invoke(Ask);
            else
                Ask();

            if (accepted)
                await CheckForUpdatesAsync();
        }
        catch
        {
            // No internet / GitHub down / rate limit — never block or nag on startup.
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_updateInProgress)
        {
            SendToWeb("update_result", new
            {
                status = "busy",
                text = Ui("appUpdate.inProgress")
            });
            return;
        }

        _updateInProgress = true;
        try
        {
            SendToWeb("update_result", new
            {
                status = "checking",
                text = Ui("appUpdate.checking")
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
                text = Ui("appUpdate.downloading", tag)
            });

            await AppUpdater.DownloadAsync(check.DownloadUrl, tempExe, progress =>
            {
                SendToWeb("update_result", new
                {
                    status = "downloading",
                    progress,
                    text = Ui("appUpdate.downloadingProgress", tag, progress)
                });
            });

            SendToWeb("update_result", new
            {
                status = "applying",
                text = Ui("appUpdate.restarting", tag)
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
                text = Ui("appUpdate.failed", ex.Message)
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
                text = Ui("appUpdate.couldNotOpenLink", ex.Message)
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
                    message = Ui("config.noIniLoaded"),
                    mods = Array.Empty<object>()
                });
                return;
            }

            Dictionary<string, string> raw = IniManager.ReadIni(path);
            (List<string> workshopIds, List<string> modIds) = IniManager.ParseModLists(raw);
            List<ManualModMapping> mappings = ManualModMappingHelper.Normalize(_config.ManualModMappings);
            List<ModListDisplayItem> items = ManualModMappingHelper.BuildDisplayItems(workshopIds, modIds, mappings);

            SendToWeb("mod_list_data", new
            {
                success = true,
                path,
                workshopCount = workshopIds.Count,
                modCount = modIds.Count,
                mappings = mappings.Select(ManualModMappingHelper.ToUiPayload),
                mods = items.Select(ManualModMappingHelper.ToUiPayload)
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
            if (_restartInProgress || _userStopInProgress || _modUpdateRestartFlow.IsActive)
            {
                SendToWeb("server_action_result", new
                {
                    success = false,
                    message = Ui("restart.inProgress")
                });
                return;
            }

            string? batPath = ResolveStartBatPath();
            if (batPath is null)
            {
                SendToWeb("server_action_result", new
                {
                    success = false,
                    message = Ui("server.startBatMissing")
                });
                return;
            }

            bool javaRunning = _serverProcessManager.IsPzServerJavaRunning();

            if (javaRunning || _serverProcessManager.IsManagedProcessRunning)
            {
                SendToWeb("server_console", new
                {
                    line = Ui("server.alreadyOnline")
                });
                SendToWeb("server_action_result", new
                {
                    success = false,
                    message = Ui("server.alreadyOnlineShort")
                });
                return;
            }

            SendToWeb("server_console", new { line = Ui("server.starting", batPath) });
            Process process = _serverProcessManager.StartServer(batPath, embedConsole: true);
            SendToWeb("server_console", new { line = Ui("server.consoleOpened", process.Id) });
            SendToWeb("server_action_result", new
            {
                success = true,
                message = Ui("server.startRequested")
            });
            ArmServerStartedDiscordWatch();
            _ = HandleGetServerStatusAsync();
        }
        catch (Exception ex)
        {
            SendToWeb("server_console", new { line = Ui("server.startFailed", ex.Message) });
            SendToWeb("server_action_result", new { success = false, message = ex.Message });
        }
    }

    private async Task HandleStopServerAsync()
    {
        if (_restartInProgress || _userStopInProgress || _modUpdateRestartFlow.IsActive)
        {
            SendToWeb("server_action_result", new
            {
                success = false,
                message = Ui("restart.inProgress")
            });
            return;
        }

        _userStopInProgress = true;
        try
        {
            CancelServerStartedDiscordWatch();
            SendToWeb("server_console", new { line = Ui("server.stoppingRcon") });
            string response = await _rconManager.SendCommandAsync(
                _config.RconHost, _config.RconPort, _config.RconPassword, "quit");
            SendToWeb("server_console", new { line = Ui("rcon.prefix", response) });
            await Task.Delay(8000);

            foreach (string message in _serverProcessManager.KillServerTree())
                SendToWeb("server_console", new { line = message });

            SendToWeb("server_action_result", new { success = true, message = Ui("server.stopCompleted") });
            await HandleGetServerStatusAsync();
        }
        catch (Exception ex)
        {
            SendToWeb("server_action_result", new { success = false, message = ex.Message });
        }
        finally
        {
            _userStopInProgress = false;
        }
    }

    private async Task ExecuteStopOnlyAsync()
    {
        string response = await _rconManager.SendCommandAsync(
            _config.RconHost, _config.RconPort, _config.RconPassword, "quit");
        SendToWeb("server_console", new { line = Ui("rcon.prefix", response) });
        await Task.Delay(15000);

        if (_serverProcessManager.IsPzServerJavaRunning())
        {
            foreach (string message in _serverProcessManager.KillServerTree())
                SendToWeb("server_console", new { line = message });
        }

        await HandleGetServerStatusAsync();
    }

    private void HandleUpdateServer()
    {
        if (_steamCmdUpdateInFlight)
        {
            SendToWeb("log", Ui("steamcmd.alreadyRunning"));
            PushSteamCmdUpdateStatus(true, Ui("steamcmd.statusInProgress"));
            return;
        }

        string steamCmdPath = string.IsNullOrWhiteSpace(_config.SteamCmdPath)
            ? "C:\\steamcmd\\steamcmd.exe"
            : _config.SteamCmdPath;

        if (!File.Exists(steamCmdPath))
        {
            SendToWeb("log", Ui("steamcmd.notFound", steamCmdPath));
            return;
        }

        SendToWeb("confirm_update", new
        {
            message = Ui("steamcmd.confirm")
        });
    }

    private void PushSteamCmdUpdateStatus(bool active, string? label = null)
    {
        SendToWeb("steamcmd_update_status", new
        {
            active,
            label = label ?? string.Empty
        });
    }

    private void ForwardSteamCmdLine(string? line)
    {
        if (string.IsNullOrEmpty(line))
            return;

        if (!SteamCmdConsoleFilter.ShouldForwardToConsole(line))
            return;

        string? progress = SteamCmdConsoleFilter.FormatProgressLine(line);
        SendToWeb("server_console", new { line = progress ?? line });
    }

    private async Task HandleUpdateServerConfirmedAsync()
    {
        if (_steamCmdUpdateInFlight)
        {
            SendToWeb("log", Ui("steamcmd.alreadyRunning"));
            return;
        }

        _steamCmdUpdateInFlight = true;
        SteamCmdConsoleFilter.ResetProgressTracking();
        PushSteamCmdUpdateStatus(true, Ui("steamcmd.statusChecking"));

        try
        {
            string steamCmdPath = string.IsNullOrWhiteSpace(_config.SteamCmdPath)
                ? "C:\\steamcmd\\steamcmd.exe"
                : _config.SteamCmdPath;
            string serverPath = _config.ServerPath ?? string.Empty;

            if (string.IsNullOrWhiteSpace(serverPath))
            {
                SendToWeb("server_console", new { line = Ui("steamcmd.serverPathMissing") });
                SendToWeb("log", Ui("steamcmd.serverPathMissing"));
                return;
            }

            SendToWeb("server_console", new { line = Ui("steamcmd.checking") });

            SteamCmdUpdateCheckResult preCheck = await SteamCmdUpdateHelper.CheckUpdateNeededAsync(
                serverPath,
                steamCmdPath,
                _config.SteamUpdateBranch,
                _config.ZomboidDataPath,
                _config.UiLanguage);

            SendToWeb("server_console", new { line = preCheck.Message });

            if (!preCheck.NeedsUpdate && preCheck.CheckSucceeded)
            {
                SendToWeb("log", preCheck.Message);
                SendToWeb("toast_success", new { message = Ui("steamcmd.toastUpToDate") });
                return;
            }

            PushSteamCmdUpdateStatus(true, Ui("steamcmd.statusStopping"));
            SendToWeb("server_console", new { line = Ui("steamcmd.stoppingServer") });
            await ExecuteStopOnlyAsync();

            PushSteamCmdUpdateStatus(true, Ui("steamcmd.statusRunning"));
            SendToWeb("server_console", new { line = Ui("steamcmd.running") });
            SendToWeb("log", Ui("steamcmd.running"));

            string updateBranch = SteamCmdUpdateHelper.GetEffectiveBranch(_config.SteamUpdateBranch);
            string? steamDir = Path.GetDirectoryName(steamCmdPath);
            var startInfo = new ProcessStartInfo
            {
                FileName = steamCmdPath,
                Arguments = SteamCmdUpdateHelper.BuildUpdateArguments(serverPath, updateBranch),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            if (!string.IsNullOrWhiteSpace(steamDir) && Directory.Exists(steamDir))
                startInfo.WorkingDirectory = steamDir;

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, e) => ForwardSteamCmdLine(e.Data);
            process.ErrorDataReceived += (_, e) => ForwardSteamCmdLine(e.Data);

            if (!process.Start())
            {
                SendToWeb("server_console", new { line = Ui("steamcmd.couldNotStart") });
                SendToWeb("log", Ui("steamcmd.couldNotStart"));
                return;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            int exitCode = process.ExitCode;
            if (exitCode == 0)
            {
                string? updatedVersion = SteamCmdUpdateHelper.ReadInstalledGameVersion(
                    serverPath,
                    _config.ZomboidDataPath);
                string versionNote = string.IsNullOrWhiteSpace(updatedVersion)
                    ? Ui("steamcmd.finishedStartServer")
                    : Ui("steamcmd.finishedVersion", updatedVersion);
                SendToWeb("server_console", new { line = Ui("steamcmd.finished") + versionNote });
                SendToWeb("log", Ui("steamcmd.finished") + versionNote);
                SendToWeb("toast_success", new { message = Ui("steamcmd.toastSuccess") });
            }
            else
            {
                SendToWeb("server_console", new { line = Ui("steamcmd.errorCode", exitCode) });
                SendToWeb("log", Ui("steamcmd.errorCode", exitCode));
            }
        }
        catch (Exception ex)
        {
            SendToWeb("server_console", new { line = Ui("steamcmd.failed", ex.Message) });
            SendToWeb("log", Ui("steamcmd.failed", ex.Message));
        }
        finally
        {
            _steamCmdUpdateInFlight = false;
            PushSteamCmdUpdateStatus(false);
        }
    }

    private void CancelBackupInProgress()
    {
        try
        {
            _backupCts?.Cancel();
            SendToWeb("log", Ui("backup.cancelRequested"));
        }
        catch
        {
            // ignore
        }
    }

    private async Task HandleCreateBackupAsync(bool fromScheduler = false)
    {
        if (_backupInProgress)
        {
            SendToWeb("backup_result", new
            {
                success = false,
                message = Ui("backup.alreadyRunning")
            });
            return;
        }

        _backupInProgress = true;
        _backupCts?.Dispose();
        _backupCts = new CancellationTokenSource();
        CancellationToken token = _backupCts.Token;

        SendToWeb("backup_progress", new
        {
            active = true,
            done = 0,
            total = 0,
            percent = 0,
            file = "",
            cancellable = true
        });

        AppConfig snapshot = CloneConfigForBackup(_config);
        try
        {
            var progress = new Progress<BackupProgressUpdate>(p =>
            {
                if (token.IsCancellationRequested)
                    return;
                SendToWeb("backup_progress", new
                {
                    active = true,
                    done = p.Done,
                    total = p.Total,
                    percent = p.Percent,
                    file = p.CurrentFile,
                    cancellable = true
                });
            });

            BackupResult result = await Task.Run(
                () => BackupManager.CreateBackup(snapshot, progress, token),
                token).ConfigureAwait(true);

            SendToWeb("backup_progress", new
            {
                active = false,
                done = result.FileCount,
                total = result.FileCount,
                percent = result.Success ? 100 : 0,
                file = "",
                cancellable = false
            });
            SendToWeb("backup_result", new
            {
                success = result.Success,
                cancelled = result.Cancelled,
                message = result.Message,
                path = result.Path,
                fileCount = result.FileCount
            });
            if (result.Success)
            {
                HandleListBackups();
                if (fromScheduler)
                {
                    SendToWeb("log", Ui("backup.completed", result.Message));
                    _backupScheduler.MarkOneTimeDone();
                }
            }
            else if (fromScheduler)
            {
                SendToWeb("log", result.Cancelled
                    ? Ui("backup.cancelled")
                    : Ui("backup.failed", result.Message));
            }
        }
        catch (OperationCanceledException)
        {
            SendToWeb("backup_progress", new
            {
                active = false,
                done = 0,
                total = 0,
                percent = 0,
                file = "",
                cancellable = false
            });
            SendToWeb("backup_result", new
            {
                success = false,
                cancelled = true,
                message = Ui("backup.cancelledMessage")
            });
            if (fromScheduler)
                SendToWeb("log", Ui("backup.cancelled"));
        }
        catch (Exception ex)
        {
            SendToWeb("backup_progress", new
            {
                active = false,
                done = 0,
                total = 0,
                percent = 0,
                file = "",
                cancellable = false
            });
            SendToWeb("backup_result", new
            {
                success = false,
                cancelled = false,
                message = ex.Message
            });
            if (fromScheduler)
                SendToWeb("log", Ui("backup.failed", ex.Message));
        }
        finally
        {
            _backupInProgress = false;
            try { _backupCts?.Dispose(); } catch { /* ignore */ }
            _backupCts = null;
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
                createdAt = b.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                isZip = b.IsZip,
                sizeLabel = b.IsZip && b.SizeBytes > 0 ? FormatSize(b.SizeBytes) : ""
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
                    message = Ui("config.sandboxNotFound")
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
            SendToWeb("sandbox_saved", new { success = true, message = Ui("config.sandboxSaved") });
        }
        catch (Exception ex)
        {
            SendToWeb("sandbox_saved", new { success = false, message = Ui("config.error", ex.Message) });
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
                    Title = Ui("dialog.sandboxVars"),
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
                    Title = Ui("dialog.serverIni"),
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
                SendToWeb("ini_saved", new { success = false, message = Ui("config.dialogError", ex.Message) });
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
                    Description = Ui("dialog.serverFolder"),
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
                RefreshHardwareStaticCache();

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
                SendToWeb("ini_saved", new { success = false, message = Ui("config.dialogError", ex.Message) });
            }
        }));
    }

    /// <summary>
    /// Finds a likely PZ dedicated server launcher .bat.
    /// Auto-fills only when exactly one likely match exists.
    /// </summary>
    private (string startBat, string notice) DetectStartBat(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return (string.Empty, string.Empty);

        string[] allBats = Directory.GetFiles(folderPath, "*.bat");
        if (allBats.Length == 0)
            return (string.Empty, Ui("config.noBatFound"));

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
            return (name, Ui("config.startBatAuto", name));
        }

        if (allBats.Length == 1)
        {
            string name = Path.GetFileName(allBats[0]);
            return (name, Ui("config.startBatAuto", name));
        }

        return (string.Empty, Ui("config.multipleBat"));
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
                    Title = Ui("dialog.startBat"),
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
                RefreshHardwareStaticCache();
                SendToWeb("start_bat_selected", new
                {
                    startBat = _config.StartBat,
                    serverPath = _config.ServerPath
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BrowseStartBat failed: {ex}");
                SendToWeb("ini_saved", new { success = false, message = Ui("config.dialogError", ex.Message) });
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
                    Description = Ui("dialog.zomboidData"),
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
                SendToWeb("ini_saved", new { success = false, message = Ui("config.dialogError", ex.Message) });
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
            RefreshHardwareStaticCache();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load INI: {ex.Message}");
        }
    }

    private void HandleListConfigProfiles()
    {
        try
        {
            List<ConfigProfileInfo> profiles = ConfigProfileManager.ListProfiles();
            SendToWeb("config_profiles", new
            {
                success = true,
                profiles = profiles.Select(ToProfileDto)
            });
        }
        catch (Exception ex)
        {
            SendToWeb("config_profiles", new { success = false, message = ex.Message, profiles = Array.Empty<object>() });
        }
    }

    private void HandleSaveConfigProfile(JsonElement data)
    {
        try
        {
            string name = ReadJsonString(data, "name");
            ConfigProfileInfo info = ConfigProfileManager.SaveCurrentAsProfile(name, _config);
            SendToWeb("config_profile_saved", new
            {
                success = true,
                message = Ui("config.profileSaved", info.Name),
                profile = ToProfileDto(info)
            });
            HandleListConfigProfiles();
        }
        catch (Exception ex)
        {
            SendToWeb("config_profile_saved", new { success = false, message = ex.Message });
        }
    }

    private void HandleLoadConfigProfile(JsonElement data)
    {
        try
        {
            string id = ReadJsonString(data, "id");
            (ConfigProfileInfo info, string iniPath, string sandboxPath) =
                ConfigProfileManager.ApplyProfile(id, _config);

            _config.LastIniFilePath = iniPath;
            ConfigManager.Save(_config);
            RefreshHardwareStaticCache();

            // Refresh editors with the newly applied files
            try
            {
                Dictionary<string, string> rawIni = IniManager.ReadIni(iniPath);
                SendToWeb("ini_loaded", new { path = iniPath, entries = IniManager.ParseToEntries(rawIni) });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Reload INI after profile apply failed: {ex.Message}");
            }

            try
            {
                Dictionary<string, string> rawSandbox = SandboxManager.ReadSandbox(sandboxPath);
                SendToWeb("sandbox_loaded", new
                {
                    success = true,
                    path = sandboxPath,
                    entries = SandboxManager.ParseToEntries(rawSandbox)
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Reload Sandbox after profile apply failed: {ex.Message}");
            }

            SendToWeb("config_profile_loaded", new
            {
                success = true,
                message = Ui("config.profileApplied", info.Name),
                profileName = info.Name,
                iniPath,
                sandboxPath,
                profile = ToProfileDto(info)
            });
            HandleListConfigProfiles();
        }
        catch (Exception ex)
        {
            SendToWeb("config_profile_loaded", new { success = false, message = ex.Message });
        }
    }

    private void HandleRenameConfigProfile(JsonElement data)
    {
        try
        {
            string id = ReadJsonString(data, "id");
            string name = ReadJsonString(data, "name");
            ConfigProfileInfo info = ConfigProfileManager.RenameProfile(id, name);
            SendToWeb("config_profile_renamed", new
            {
                success = true,
                message = Ui("config.profileRenamed", info.Name),
                profile = ToProfileDto(info)
            });
            HandleListConfigProfiles();
        }
        catch (Exception ex)
        {
            SendToWeb("config_profile_renamed", new { success = false, message = ex.Message });
        }
    }

    private void HandleDeleteConfigProfile(JsonElement data)
    {
        try
        {
            string id = ReadJsonString(data, "id");
            ConfigProfileManager.DeleteProfile(id);
            SendToWeb("config_profile_deleted", new { success = true, message = Ui("config.profileDeleted"), id });
            HandleListConfigProfiles();
        }
        catch (Exception ex)
        {
            SendToWeb("config_profile_deleted", new { success = false, message = ex.Message });
        }
    }

    private static object ToProfileDto(ConfigProfileInfo p) => new
    {
        id = p.Id,
        name = p.Name,
        created = p.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
        lastApplied = p.LastAppliedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "",
        iniFileName = p.IniFileName,
        sandboxFileName = p.SandboxFileName,
        hasIni = p.HasIni,
        hasSandbox = p.HasSandbox
    };

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
            if (!string.IsNullOrWhiteSpace(path))
                _config.LastIniFilePath = path;
            RefreshHardwareStaticCache();
            SendToWeb("ini_saved", new { success = true, message = Ui("config.iniSaved") });
        }
        catch (Exception ex)
        {
            SendToWeb("ini_saved", new { success = false, message = Ui("config.error", ex.Message) });
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
        SendToWeb("log", Ui("hours.saved", summary));
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

        SendToWeb("log", Ui("rcon.prefix", command));
        // Discord: only the 5-minute warning (10-min / 1-min stay in-game only).
        if (minutesBefore == 5)
        {
            _ = NotifyDiscordEventAsync(
                DiscordEventCatalog.PreRestartWarning,
                new Dictionary<string, string> { ["minutes"] = "5" });
        }
        try
        {
            string response = await _rconManager.SendCommandAsync(
                _config.RconHost,
                _config.RconPort,
                _config.RconPassword,
                command);
            SendToWeb("log", Ui("scheduler.preAnnounceResponse", minutesBefore, response));
        }
        catch (Exception ex)
        {
            SendToWeb("log", Ui("scheduler.preAnnounceFailed", minutesBefore, ex.Message));
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
        _config.SteamCmdPath = GetString("steamCmdPath", _config.SteamCmdPath);
        _config.SteamUpdateBranch = GetString("steamUpdateBranch", _config.SteamUpdateBranch);
        _config.StartBat = GetString("startBat", _config.StartBat);
        _config.ZomboidDataPath = GetString("zomboidDataPath", _config.ZomboidDataPath);
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("statsIntervalMinutes", out JsonElement intervalEl))
        {
            int interval = intervalEl.ValueKind == JsonValueKind.Number && intervalEl.TryGetInt32(out int n)
                ? n
                : GetInt("statsIntervalMinutes", _config.StatsIntervalMinutes);
            _config.StatsIntervalMinutes = StatsHistoryStore.ClampIntervalMinutes(interval);
        }

        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("statsRetentionDays", out JsonElement retentionEl))
        {
            int days = retentionEl.ValueKind == JsonValueKind.Number && retentionEl.TryGetInt32(out int d)
                ? d
                : GetInt("statsRetentionDays", _config.StatsRetentionDays);
            _config.StatsRetentionDays = StatsHistoryStore.ClampRetentionDays(days);
        }
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("uiLanguage", out JsonElement langEl)
            && langEl.ValueKind == JsonValueKind.String)
        {
            string? lang = langEl.GetString();
            if (!string.IsNullOrWhiteSpace(lang))
                _config.UiLanguage = lang;
        }

        ConfigManager.SaveImmediately(_config);
        _serverProcessManager.SetServerContext(_config.ServerPath, _config.StartBat);
        _statsCollector.ApplyInterval();
        RefreshHardwareStaticCache();
        SendToWeb("settings_saved", new { success = true });
        SendToWeb("log", Ui("settings.saved"));
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
            Ui("discord.testMessage"));
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
            SendToWeb("log", Ui("discord.workshopReadFailed", ex.Message));
            return new List<string>();
        }
    }

    private List<string> GetConfiguredModIds()
    {
        try
        {
            string? path = _config.LastIniFilePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return new List<string>();

            Dictionary<string, string> raw = IniManager.ReadIni(path);
            return IniManager.ParseModLists(raw).ModIds;
        }
        catch
        {
            return new List<string>();
        }
    }

    private string FormatUpdatedModLabel(string workshopId, string? steamTitle) =>
        ManualModMappingHelper.FormatUpdateLabel(workshopId, steamTitle, _config.ManualModMappings);

    private void CancelServerStartedDiscordWatch()
    {
        try
        {
            _serverStartupWatchCts?.Cancel();
            _serverStartupWatchCts?.Dispose();
        }
        catch
        {
            // ignore
        }

        _serverStartupWatchCts = null;
        _serverStartupWatcher = null;
        ClearServerBootState();
    }

    private void ClearServerBootState()
    {
        _awaitingServerReady = false;
        _serverReadyConfirmed = false;
    }

    private void MarkServerBootStarting()
    {
        _awaitingServerReady = true;
        _serverReadyConfirmed = false;
    }

    private void MarkServerBootReady()
    {
        _awaitingServerReady = false;
        _serverReadyConfirmed = true;
    }

    /// <summary>
    /// UI status: Online only after SERVER STARTED (or Java already up outside our boot watch).
    /// Starting while we wait for that marker after a manager launch.
    /// </summary>
    private string ResolveServerUiStatus(bool javaOnline)
    {
        if (!javaOnline)
            return "offline";
        if (_serverReadyConfirmed)
            return "online";
        if (_awaitingServerReady)
            return "starting";
        if (_serverProcessManager.IsManagedProcessRunning)
            return "starting";
        return "online";
    }

    /// <summary>
    /// After any StartServer call: Discord "back online" only when the PZ log marker appears
    /// (not when the console process starts). Shared by manual start, scheduled/manual restart,
    /// and mod-update auto-restart.
    /// </summary>
    private void ArmServerStartedDiscordWatch(
        IReadOnlyDictionary<string, string>? placeholders = null)
    {
        CancelServerStartedDiscordWatch();
        MarkServerBootStarting();
        _ = HandleGetServerStatusAsync(includePlayers: false);

        var cts = new CancellationTokenSource();
        _serverStartupWatchCts = cts;
        var watcher = new ServerStartupWatcher();
        _serverStartupWatcher = watcher;

        Dictionary<string, string>? vars = placeholders is null
            ? null
            : new Dictionary<string, string>(placeholders, StringComparer.OrdinalIgnoreCase);

        _ = Task.Run(async () =>
        {
            try
            {
                bool found = await watcher.WaitAsync(
                    ResolveServerStartupLogRoots(),
                    ServerStartupWatcher.DefaultTimeout,
                    cts.Token,
                    msg =>
                    {
                        try
                        {
                            SendToWeb("server_console", new { line = msg });
                        }
                        catch
                        {
                            // ignore
                        }
                    });

                if (cts.IsCancellationRequested)
                    return;

                if (found)
                {
                    MarkServerBootReady();
                    await NotifyDiscordEventAsync(DiscordEventCatalog.ServerRestarted, vars);
                    await HandleGetServerStatusAsync(includePlayers: false);
                    return;
                }

                _awaitingServerReady = false;
                SendToWeb("server_console", new { line = Ui("server.startedMarkerTimeout") });
                SendToWeb("log", Ui("server.startedMarkerTimeout"));
                await HandleGetServerStatusAsync(includePlayers: false);
            }
            catch (OperationCanceledException)
            {
                // superseded or form closing
            }
            catch (Exception ex)
            {
                try
                {
                    SendToWeb("log", Ui("server.startedMarkerWatchFailed", ex.Message));
                }
                catch
                {
                    // ignore
                }
            }
        });
    }

    private IEnumerable<string> ResolveServerStartupLogRoots()
    {
        var roots = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            string full = Path.GetFullPath(path.Trim());
            if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase))
                roots.Add(full);
        }

        Add(_config.ServerPath);
        Add(_config.ZomboidDataPath);
        Add(_config.ZomboidUserPath);
        Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Zomboid"));
        return roots;
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
            ["mods"] = "",
            ["minutes"] = ""
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
                SendToWeb("log", Ui("discord.notifyFailed", message));
        }
        catch (Exception ex)
        {
            SendToWeb("log", Ui("discord.notifyError", ex.Message));
        }
    }

    /// <summary>Driven by <see cref="SchedulerHeartbeat"/>; same due logic as the former 20s Discord timer.</summary>
    private void TickDiscordCustomHourly()
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
        SendToWeb("log", Ui("discord.hourlyPosted", hour));
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
                    message = string.IsNullOrWhiteSpace(response) ? Ui("rcon.noRconResponse") : response,
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
                    $"adduser \"{SanitizeRconToken(player)}\" \"{SanitizeRconToken(password)}\"",
                "whitelist_remove" when !string.IsNullOrWhiteSpace(player) =>
                    $"removeuserfromwhitelist \"{SanitizeRconToken(player)}\"",
                "servermsg" when !string.IsNullOrWhiteSpace(message) => $"servermsg \"{message}\"",
                _ => string.Empty
            };

            if (string.IsNullOrWhiteSpace(command))
            {
                SendToWeb("player_action_result", new { success = false, message = Ui("player.invalidAction") });
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
            {
                await HandleGetPlayerListAsync();
                HandleGetWhitelist();
            }
        }
        catch (Exception ex)
        {
            SendToWeb("player_action_result", new { success = false, message = ex.Message });
        }
    }

    private void HandleGetWhitelist()
    {
        try
        {
            bool? openJoin = WhitelistStore.ReadOpenJoinAllowed(_config);
            (bool success, string message, List<WhitelistUser> users, string? dbPath) =
                WhitelistStore.ReadUsers(_config);

            SendToWeb("whitelist_data", new
            {
                success,
                message,
                dbPath = dbPath ?? "",
                worldName = WhitelistStore.ResolveWorldName(_config) ?? "",
                openJoin,
                users = users.Select(u => new
                {
                    username = u.Username,
                    accessLevel = u.AccessLevel,
                    steamId = u.SteamId,
                    banned = u.Banned,
                    extra = u.Extra
                })
            });
        }
        catch (Exception ex)
        {
            SendToWeb("whitelist_data", new
            {
                success = false,
                message = ex.Message,
                users = Array.Empty<object>(),
                openJoin = (bool?)null
            });
        }
    }

    private static string SanitizeRconToken(string value)
    {
        return (value ?? "").Replace("\"", "").Replace("\r", "").Replace("\n", "").Trim();
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
                    ? Ui("rcon.noResponse")
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
                message = Ui("rcon.connectionOk")
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
            SendToWeb("rcon_response", new { success = false, response = Ui("rcon.noCommand") });
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
            response = string.IsNullOrWhiteSpace(response) ? Ui("rcon.emptyResponse") : response
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
            steamCmdPath = _config.SteamCmdPath ?? "C:\\steamcmd\\steamcmd.exe",
            steamUpdateBranch = _config.SteamUpdateBranch ?? string.Empty,
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
            statsIntervalMinutes = _config.StatsIntervalMinutes,
            statsRetentionDays = _config.StatsRetentionDays,
            manualModMappings = ManualModMappingHelper.Normalize(_config.ManualModMappings)
                .Select(ManualModMappingHelper.ToUiPayload),
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
                    message = Ui("logs.adminCommandsMissing")
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
                    message = Ui("logs.adminCommandsInvalid")
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

    private void ClearLogAnalyzeSession()
    {
        try
        {
            _logAnalyzeCts?.Cancel();
        }
        catch
        {
            // ignore
        }

        lock (_logAnalyzeLock)
            _logSession = null;
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

    private void HandleAnalyzeLog(JsonElement data)
    {
        string path = ReadJsonString(data, "path");
        _logAnalyzeCts?.Cancel();
        var cts = new CancellationTokenSource();
        _logAnalyzeCts = cts;
        CancellationToken token = cts.Token;
        List<ConfiguredModRef> mods = GetConfiguredModRefs();

        SendToWeb("log_analyze_progress", new { path, percent = 0, status = "loading" });

        _ = Task.Run(() =>
        {
            try
            {
                LogAnalyzeSession session = LogAnalyzeSession.Load(
                    path,
                    _config,
                    mods,
                    token,
                    pct => SendToWeb("log_analyze_progress", new { path, percent = pct, status = "loading" }));

                if (token.IsCancellationRequested)
                    return;

                lock (_logAnalyzeLock)
                {
                    if (token.IsCancellationRequested)
                        return;
                    _logSession = session;
                }
                SendToWeb("log_analyze_ready", session.Meta());
            }
            catch (OperationCanceledException)
            {
                // superseded by another analyze
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested)
                    return;
                SendToWeb("log_analyze_ready", new
                {
                    success = false,
                    path,
                    message = ex.Message
                });
            }
        }, token);
    }

    private void HandleQueryLogEntries(JsonElement data)
    {
        LogAnalyzeSession? session;
        lock (_logAnalyzeLock)
            session = _logSession;
        if (session is null)
        {
            SendToWeb("log_entries", new { success = false, message = Ui("logs.noLogLoaded"), rows = Array.Empty<object>(), total = 0 });
            return;
        }

        try
        {
            string level = ReadJsonString(data, "level");
            string category = ReadJsonString(data, "category");
            string search = ReadJsonString(data, "search");
            string sortBy = ReadJsonString(data, "sortBy", "timestamp");
            string sortDir = ReadJsonString(data, "sortDir", "asc");
            int offset = ReadJsonInt(data, "offset", 0);
            int limit = ReadJsonInt(data, "limit", 150);
            object page = session.Query(level, category, search, sortBy, sortDir, offset, limit);
            SendToWeb("log_entries", new
            {
                success = true,
                path = session.FilePath,
                level,
                category,
                search,
                sortBy,
                sortDir,
                page
            });
        }
        catch (Exception ex)
        {
            SendToWeb("log_entries", new { success = false, message = ex.Message, rows = Array.Empty<object>(), total = 0 });
        }
    }

    private void HandleGetLogContext(JsonElement data)
    {
        LogAnalyzeSession? session;
        lock (_logAnalyzeLock)
            session = _logSession;
        if (session is null)
        {
            SendToWeb("log_context", new { success = false, message = Ui("logs.noLogLoaded") });
            return;
        }

        int index = ReadJsonInt(data, "index", -1);
        SendToWeb("log_context", session.GetContext(index));
    }

    private void HandleGetLogSystemInfo()
    {
        LogAnalyzeSession? session;
        lock (_logAnalyzeLock)
            session = _logSession;
        if (session is null)
        {
            SendToWeb("log_system_info", new { success = false, message = Ui("logs.noLogLoaded"), specs = Array.Empty<object>() });
            return;
        }

        SendToWeb("log_system_info", session.GetSystemInfo());
    }

    private void HandleGetLogModInfo()
    {
        LogAnalyzeSession? session;
        lock (_logAnalyzeLock)
            session = _logSession;
        if (session is null)
        {
            SendToWeb("log_mod_info", new { success = false, message = Ui("logs.noLogLoaded"), mods = Array.Empty<object>(), overrides = Array.Empty<object>() });
            return;
        }

        SendToWeb("log_mod_info", session.GetModInfo());
    }

    private void BrowseLogFile()
    {
        BeginInvoke(new Action(() =>
        {
            try
            {
                Activate();
                BringToFront();

                using var dialog = new OpenFileDialog
                {
                    Title = Ui("dialog.logFile"),
                    Filter = "Log files (*.txt;*.log)|*.txt;*.log|All files (*.*)|*.*",
                    CheckFileExists = true,
                    Multiselect = false
                };

                string initial = "";
                if (!string.IsNullOrWhiteSpace(_config.ZomboidDataPath))
                {
                    string logs = Path.Combine(_config.ZomboidDataPath, "Logs");
                    initial = Directory.Exists(logs) ? logs : _config.ZomboidDataPath;
                }
                else if (!string.IsNullOrWhiteSpace(_config.ServerPath))
                {
                    string logs = Path.Combine(_config.ServerPath, "logs");
                    initial = Directory.Exists(logs) ? logs : _config.ServerPath;
                }

                if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial))
                    dialog.InitialDirectory = initial;

                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;

                string full = Path.GetFullPath(dialog.FileName);
                if (!LogViewer.IsAllowedLogPath(full, _config))
                {
                    SendToWeb("log_analyze_ready", new
                    {
                        success = false,
                        path = full,
                        message = Ui("logs.pathOutside")
                    });
                    return;
                }

                SendToWeb("log_file_selected", new { path = full, name = Path.GetFileName(full) });
                HandleAnalyzeLog(JsonSerializer.SerializeToElement(new { path = full }));
            }
            catch (Exception ex)
            {
                SendToWeb("log_analyze_ready", new { success = false, message = ex.Message });
            }
        }));
    }

    private List<ConfiguredModRef> GetConfiguredModRefs()
    {
        var list = new List<ConfiguredModRef>();
        try
        {
            string? path = _config.LastIniFilePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return list;

            Dictionary<string, string> raw = IniManager.ReadIni(path);
            (List<string> workshopIds, List<string> modIds) = IniManager.ParseModLists(raw);
            int count = Math.Max(workshopIds.Count, modIds.Count);
            for (int i = 0; i < count; i++)
            {
                list.Add(new ConfiguredModRef
                {
                    WorkshopId = i < workshopIds.Count ? workshopIds[i] : "",
                    ModId = i < modIds.Count ? modIds[i] : ""
                });
            }
        }
        catch
        {
            // INI optional for log analysis
        }

        return list;
    }

    private static string ReadJsonString(JsonElement data, string name, string fallback = "")
    {
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty(name, out JsonElement el)
            && el.ValueKind == JsonValueKind.String)
        {
            return el.GetString() ?? fallback;
        }

        return fallback;
    }

    private static int ReadJsonInt(JsonElement data, string name, int fallback)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out JsonElement el))
            return fallback;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int n))
            return n;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out n))
            return n;
        return fallback;
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
        _hardwareMonitor.EnsureCpuCounter();
        RefreshHardwareStaticCache();
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

    /// <summary>
    /// Refresh rarely-changing hardware UI data (disk totals + INI ports).
    /// Called when the Server tab opens and when Config/Settings paths change — not per tick.
    /// </summary>
    private void RefreshHardwareStaticCache()
    {
        try
        {
            _hardwareMonitor.RefreshStatic(_config.ServerPath);
            _cachedHardwarePorts = BuildHardwarePortsCache();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RefreshHardwareStaticCache failed: {ex.Message}");
            _cachedHardwarePorts = new List<object>
            {
                new { name = "RCON (settings)", value = _config.RconPort.ToString() }
            };
        }
    }

    private List<object> BuildHardwarePortsCache()
    {
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
        return ports;
    }

    private void PushHardwareStats()
    {
        try
        {
            // Dynamic only — ports/static disk come from RefreshHardwareStaticCache.
            HardwareSnapshot snap = _hardwareMonitor.SampleDynamic(_config.ServerPath);
            object hardware = new
            {
                cpuName = snap.CpuName,
                cpuUsage = snap.CpuUsage,
                ramTotalBytes = snap.RamTotalBytes,
                ramUsedBytes = snap.RamUsedBytes,
                ramAvailableBytes = snap.RamAvailableBytes,
                ramTotalGb = snap.RamTotalGb,
                ramUsedGb = snap.RamUsedGb,
                diskRoot = snap.DiskRoot,
                diskTotalBytes = snap.DiskTotalBytes,
                diskUsedBytes = snap.DiskUsedBytes,
                diskFreeBytes = snap.DiskFreeBytes,
                diskTotalGb = snap.DiskTotalGb,
                diskUsedGb = snap.DiskUsedGb,
                diskFreeGb = snap.DiskFreeGb
            };

            if (_cachedHardwarePorts.Count == 0)
                _cachedHardwarePorts = BuildHardwarePortsCache();

            SendToWeb("hardware_stats", new
            {
                hardware,
                ports = _cachedHardwarePorts
            });
        }
        catch (Exception ex)
        {
            SendToWeb("log", Ui("stats.hardwareFailed", ex.Message));
        }
    }

    private void HandleGetStatsHistory(JsonElement data)
    {
        try
        {
            string range = "24h";
            if (data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("range", out JsonElement rangeEl)
                && rangeEl.ValueKind == JsonValueKind.String)
            {
                range = rangeEl.GetString() ?? "24h";
            }

            StatsDashboard dash = _statsHistory.QueryDashboard(
                range,
                _config.StatsIntervalMinutes,
                _config.StatsRetentionDays);
            SendToWeb("stats_history", _statsHistory.ToPayload(dash));
        }
        catch (Exception ex)
        {
            SendToWeb("stats_history", new
            {
                range = "24h",
                sampleCount = 0,
                samples = Array.Empty<object>(),
                restarts = Array.Empty<object>(),
                uptime = new { hasData = false, percent = 0 },
                error = ex.Message
            });
        }
    }

    private void HandleSaveStatsSettings(JsonElement data)
    {
        int interval = ReadJsonInt(data, "intervalMinutes", _config.StatsIntervalMinutes);
        int retention = ReadJsonInt(data, "retentionDays", _config.StatsRetentionDays);
        _config.StatsIntervalMinutes = StatsHistoryStore.ClampIntervalMinutes(interval);
        _config.StatsRetentionDays = StatsHistoryStore.ClampRetentionDays(retention);
        ConfigManager.Save(_config);
        _statsCollector.ApplyInterval();
        SendToWeb("stats_settings_saved", new
        {
            success = true,
            intervalMinutes = _config.StatsIntervalMinutes,
            retentionDays = _config.StatsRetentionDays
        });
        HandleGetStatsHistory(data);
        HandleGetSettings();
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
        SendToWeb("log", Ui("backup.scheduleSaved"));
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
        SendToWeb("log", Ui("broadcast.saved"));
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
                message = Ui("broadcast.invalidSlot")
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
                message = Ui("broadcast.emptyMessage")
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
                    message = Ui("broadcast.serverOffline")
                });
                return;
            }

            string command = BroadcastRconCommand.FormatOutgoingMessage(text);
            SendToWeb("log", Ui("broadcast.logSendNowCommand", index + 1, command));

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
                    ? Ui("broadcast.sentNow", index + 1)
                    : (string.IsNullOrWhiteSpace(response) ? Ui("broadcast.rconFailed") : response)
            });
            SendToWeb("log", success
                ? Ui("broadcast.logSentNowUnchanged", index + 1)
                : Ui("broadcast.logSendNowFailed", index + 1, response));
        }
        catch (Exception ex)
        {
            SendToWeb("broadcast_send_result", new
            {
                success = false,
                index,
                message = ex.Message
            });
            SendToWeb("log", Ui("broadcast.logSendNowFailed", index + 1, ex.Message));
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
                SendToWeb("log", Ui("broadcast.logSkippedOffline", index + 1));
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
                SendToWeb("log", Ui("broadcast.logFailedRcon", index + 1, response));
                return;
            }

            _broadcastManager.MarkSent(index);
            _config.BroadcastMessages = _broadcastManager.Slots.ToList();
            ConfigManager.Save(_config);
            SendToWeb("log", Ui("broadcast.logSent", index + 1));
            SendToWeb("broadcast_status", _broadcastManager.BuildStatusPayload());
        }
        catch (Exception ex)
        {
            SendToWeb("log", Ui("broadcast.logFailed", index + 1, ex.Message));
        }
        finally
        {
            lock (_broadcastInFlight)
                _broadcastInFlight.Remove(index);
        }
    }

    private bool IsJavaServerOnline() => _serverProcessManager.IsPzServerJavaRunning();

    private void MaybeWarnBrokenConfig()
    {
        if (_configBrokenWarningShown)
            return;
        if (string.IsNullOrWhiteSpace(_configBrokenBackupPath) && string.IsNullOrWhiteSpace(_configLoadErrorDetail))
            return;

        _configBrokenWarningShown = true;
        try
        {
            string backup = string.IsNullOrWhiteSpace(_configBrokenBackupPath)
                ? Ui("config.brokenNoBackup")
                : _configBrokenBackupPath;
            string detail = string.IsNullOrWhiteSpace(_configLoadErrorDetail)
                ? ""
                : "\n\n" + Ui("config.brokenDetail", _configLoadErrorDetail);
            MessageBox.Show(
                this,
                Ui("config.brokenBody", backup) + detail,
                Ui("config.brokenTitle"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            SendToWeb("log", Ui("config.brokenLog", backup));
        }
        catch
        {
            // never block startup on the warning dialog
        }
    }

    private static bool ReadIncludePlayersFlag(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("includePlayers", out JsonElement flag))
        {
            // Refresh button / legacy callers: full status.
            return true;
        }

        return flag.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => !string.Equals(flag.GetString(), "false", StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Number => flag.TryGetInt32(out int n) && n != 0,
            _ => true
        };
    }

    /// <param name="includePlayers">
    /// When true, opens RCON for the players list (Server-tab polling / manual refresh).
    /// When false, only checks for a Java process — used for background lifecycle pushes.
    /// </param>
    private async Task HandleGetServerStatusAsync(bool includePlayers = true)
    {
        bool javaOnline = IsJavaServerOnline();
        string status = ResolveServerUiStatus(javaOnline);

        string players = "–";
        int playerCount = 0;
        int maxPlayers = TryGetConfiguredMaxPlayers();
        // RCON only works reliably once the server has finished booting.
        if (includePlayers && status == "online" && !string.IsNullOrWhiteSpace(_config.RconPassword))
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
                    if (string.IsNullOrWhiteSpace(response)
                        || response.StartsWith("RCON error", StringComparison.OrdinalIgnoreCase))
                    {
                        players = "–";
                    }
                    else
                    {
                        players = response.Trim();
                        playerCount = CountPlayersFromRcon(response);
                    }
                }
            }
            catch
            {
                players = "–";
            }
        }

        string lastRestart = _lastRestartTime.HasValue
            ? _lastRestartTime.Value.ToString("dd.MM.yyyy HH:mm:ss")
            : Ui("server.noRestartYet");

        var payload = new
        {
            status,
            address = $"{_config.RconHost}:{_config.RconPort}",
            players,
            playerCount,
            maxPlayers,
            lastRestart
        };

        SendToWeb("server_status", payload);
    }

    private static int CountPlayersFromRcon(string response)
    {
        var header = System.Text.RegularExpressions.Regex.Match(
            response,
            @"Players connected\s*\((\d+)\)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (header.Success && int.TryParse(header.Groups[1].Value, out int fromHeader))
            return fromHeader;

        return PlayerListParser.Parse(response).Count;
    }

    private int TryGetConfiguredMaxPlayers()
    {
        try
        {
            string? path = _config.LastIniFilePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return 0;

            Dictionary<string, string> ini = IniManager.ReadIni(path);
            if (ini.TryGetValue("MaxPlayers", out string? raw)
                && int.TryParse(raw, out int max)
                && max > 0)
            {
                return max;
            }
        }
        catch
        {
            // ignore
        }

        return 0;
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
                    if (!success && !string.Equals(message, "Cancelled.", StringComparison.Ordinal))
                    {
                        SendToWeb("log", Ui("mods.autoRestartFailed", message));
                        _ = NotifyDiscordAsync(
                            Ui("mods.autoRestartFailedDiscord", message)
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
        SendToWeb("log", Ui("mods.autoRestartSaved"));
        HandleGetSettings();
    }

    private void HandleSaveManualModMappings(JsonElement data)
    {
        var parsed = new List<ManualModMapping>();
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("mappings", out JsonElement listEl)
            && listEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in listEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                string workshopId = "";
                string displayName = "";
                var modIds = new List<string>();

                if (item.TryGetProperty("workshopId", out JsonElement ws)
                    && ws.ValueKind == JsonValueKind.String)
                {
                    workshopId = ws.GetString() ?? "";
                }

                if (item.TryGetProperty("displayName", out JsonElement nameEl)
                    && nameEl.ValueKind == JsonValueKind.String)
                {
                    displayName = nameEl.GetString() ?? "";
                }

                if (item.TryGetProperty("modIds", out JsonElement modsEl))
                {
                    if (modsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement modEl in modsEl.EnumerateArray())
                        {
                            if (modEl.ValueKind == JsonValueKind.String)
                            {
                                string? id = modEl.GetString();
                                if (!string.IsNullOrWhiteSpace(id))
                                    modIds.Add(id);
                            }
                        }
                    }
                    else if (modsEl.ValueKind == JsonValueKind.String)
                    {
                        string raw = modsEl.GetString() ?? "";
                        modIds.AddRange(raw.Split(new[] { ';', ',', '\n' },
                            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    }
                }

                parsed.Add(new ManualModMapping
                {
                    WorkshopId = workshopId,
                    DisplayName = displayName,
                    ModIds = modIds
                });
            }
        }

        List<ManualModMapping> normalized = ManualModMappingHelper.Normalize(parsed);
        _config.ManualModMappings = normalized;
        ConfigManager.SaveImmediately(_config);

        List<string> warnings = ManualModMappingHelper.WarnModIdsNotInConfiguredList(
            normalized,
            GetConfiguredModIds());

        foreach (string warning in warnings)
            SendToWeb("log", "⚠️ " + warning);

        SendToWeb("manual_mod_mappings_saved", new
        {
            success = true,
            mappings = normalized.Select(ManualModMappingHelper.ToUiPayload),
            warnings
        });
        HandleGetSettings();
        HandleGetModList();
    }

    private void HandleModUpdateCancelRestart()
    {
        if (_modUpdateRestartFlow.TryCancel())
        {
            SendToWeb("log", Ui("mods.pendingRestartCancelled"));
            PushModUpdateRestartStatus();
            _ = NotifyDiscordAsync(":orange_circle: Skipped scheduled restart");
        }
        else
        {
            SendToWeb("log", Ui("mods.noPendingRestart"));
            PushModUpdateRestartStatus();
        }
    }

    private async Task HandleModUpdateRestartNowAsync()
    {
        if (_restartInProgress || _modUpdateRestartFlow.IsActive)
        {
            SendToWeb("log", Ui("restart.inProgress"));
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
        int minutes = Math.Max(1, _config.ModUpdateAutoRestart?.WarnMinutesBefore ?? 5);
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
            SendToWeb("log", Ui("restart.cannotSchedule"));
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
                    message = Ui("mods.noWorkshopIds")
                });
                return;
            }

            SendToWeb("log", fromManual
                ? Ui("mods.manualCheck")
                : Ui("mods.scheduledCheck"));

            IReadOnlyList<string> updated = await ModUpdateChecker.CheckForUpdatedModsAsync(
                ids,
                msg => SendToWeb("log", msg),
                FormatUpdatedModLabel);

            if (updated.Count == 0)
            {
                SendToWeb("mod_update_check_result", new
                {
                    success = true,
                    fromManual,
                    updated = false,
                    mods = Array.Empty<string>(),
                    message = Ui("mods.noUpdates")
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
                message = Ui("mods.updated", joined)
            });
            SendToWeb("log", Ui("mods.workshopUpdatesDetected", joined));

            bool auto = _config.ModUpdateAutoRestart?.Enabled == true;
            if (auto)
            {
                if (_restartInProgress || _modUpdateRestartFlow.IsActive)
                {
                    SendToWeb("log", Ui("mods.restartInProgress"));
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
            SendToWeb("log", Ui("mods.checkFailed", ex.Message));
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
            SendToWeb("log", Ui("mods.restartAbortedNoBat"));
            SendToWeb("mod_update_check_result", new
            {
                success = false,
                message = Ui("server.startBatMissing")
            });
            return;
        }

        if (_restartInProgress)
        {
            SendToWeb("log", Ui("mods.restartAbortedInProgress"));
            return;
        }

        ModUpdateAutoRestartConfig settings = _config.ModUpdateAutoRestart ?? new ModUpdateAutoRestartConfig();
        _modUpdateStatusTimer.Start();
        PushModUpdateRestartStatus();

        string modsJoined = string.Join(", ", updatedMods);
        _ = NotifyDiscordEventAsync(
            DiscordEventCatalog.ModsUpdated,
            new Dictionary<string, string> { ["mods"] = modsJoined });

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
                SendToWeb("log", Ui("rcon.prefix", $"{command} → {response}"));
                return response;
            },
            GetOnlinePlayerCountAsync,
            async ct =>
            {
                _restartInProgress = true;
                try
                {
                    await RunCleanSaveQuitStartAsync(batPath, ct, RestartReasons.ModUpdate);
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

    private async Task<int?> TryGetPlayerCountAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_config.RconPassword))
                return null;

            string response = await _rconManager.SendCommandAsync(
                _config.RconHost,
                _config.RconPort,
                _config.RconPassword,
                "players");

            if (string.IsNullOrWhiteSpace(response)
                || response.StartsWith("RCON error", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return PlayerListParser.Parse(response).Count;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Shared clean shutdown + start used by scheduled/manual restart and mod-update restart.
    /// </summary>
    private async Task RunCleanSaveQuitStartAsync(
        string batPath,
        CancellationToken ct = default,
        string? restartReason = null)
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
        CancelServerStartedDiscordWatch();
        MarkServerBootStarting();
        _ = HandleGetServerStatusAsync(includePlayers: false);
        await NotifyDiscordEventAsync(DiscordEventCatalog.ServerRestarting);

        Mirror(Ui("rcon.prefix", "save"));
        string saveResponse = await _rconManager.SendCommandAsync(host, port, password, "save");
        Mirror(Ui("rcon.response", saveResponse));
        if (string.IsNullOrWhiteSpace(saveResponse)
            || saveResponse.StartsWith("RCON error", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("RCON save failed: " + saveResponse);
        }

        Mirror(Ui("restart.waiting5"));
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        Mirror(Ui("rcon.prefix", "quit"));
        string quitResponse = await _rconManager.SendCommandAsync(host, port, password, "quit");
        Mirror(Ui("rcon.response", quitResponse));

        Mirror(Ui("restart.waitingCleanShutdown"));
        await Task.Delay(TimeSpan.FromSeconds(15), CancellationToken.None);

        if (_serverProcessManager.IsPzServerJavaRunning())
        {
            Mirror(Ui("restart.javaKill"));
            foreach (string message in _serverProcessManager.KillServerTree())
                Mirror(message);
        }
        else
        {
            Mirror(Ui("restart.cleanShutdown"));
            foreach (string message in _serverProcessManager.CloseOrphanServerConsoles())
                Mirror(message);
        }

        Mirror(Ui("restart.startingServer", batPath));
        if (_modUpdateRestartFlow.Phase == ModUpdateRestartPhase.ShuttingDown)
            _modUpdateRestartFlow.NotifyEnteringStartPhase();
        Process started = _serverProcessManager.StartServer(batPath, embedConsole: true);
        Mirror(Ui("restart.serverStarted", started.Id));
        SendToWeb("server_console", new { line = Ui("process.embeddedPid", started.Id) });
        ArmServerStartedDiscordWatch();
        _lastRestartTime = DateTime.Now;
        if (!string.IsNullOrWhiteSpace(restartReason))
        {
            try
            {
                _statsHistory.AddRestart(DateTime.UtcNow, restartReason);
            }
            catch
            {
                // never fail a restart because history logging failed
            }
        }
        await HandleGetServerStatusAsync();
    }

    private async Task ExecuteRestartRoutine(string reason = RestartReasons.Manual)
    {
        if (_restartInProgress || _modUpdateRestartFlow.IsActive)
        {
            SendToWeb("log", Ui("restart.skippedInProgress"));
            return;
        }

        if (!IsJavaServerOnline() && !_serverProcessManager.IsManagedProcessRunning)
        {
            string skip = Ui("restart.skipped");
            SendToWeb("log", skip);
            SendToWeb("server_console", new { line = skip });
            return;
        }

        string batPath = Path.Combine(_config.ServerPath ?? string.Empty, _config.StartBat ?? string.Empty);
        if (!File.Exists(batPath))
        {
            SendToWeb("log", Ui("restart.startAborted", batPath));
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

            Mirror(Ui("restart.routineStarted"));
            Mirror(Ui("restart.startFile", batPath));

            Mirror(Ui("rcon.prefix", "servermsg \"Server restart in 1 minute!\""));
            string msgResponse = await _rconManager.SendCommandAsync(
                host, port, password, "servermsg \"Server restart in 1 minute!\"");
            Mirror(Ui("rcon.response", msgResponse));

            Mirror(Ui("restart.waiting55"));
            await Task.Delay(TimeSpan.FromSeconds(55));

            await RunCleanSaveQuitStartAsync(batPath, restartReason: reason);

            IReadOnlyList<string> updatedMods = Array.Empty<string>();
            try
            {
                updatedMods = await ModUpdateChecker.CheckForUpdatedModsAsync(
                    GetConfiguredWorkshopIds(),
                    msg => Mirror(msg),
                    FormatUpdatedModLabel);
                if (updatedMods.Count > 0)
                    Mirror(Ui("restart.discordModUpdateNote", string.Join(", ", updatedMods)));
            }
            catch (Exception modEx)
            {
                Mirror(Ui("restart.modUpdateCheckSkipped", modEx.Message));
            }

            if (updatedMods.Count > 0)
            {
                await NotifyDiscordEventAsync(
                    DiscordEventCatalog.ModsUpdated,
                    new Dictionary<string, string> { ["mods"] = string.Join(", ", updatedMods) });
            }

            Mirror(Ui("restart.routineFinished"));
        }
        catch (Exception ex)
        {
            SendToWeb("log", Ui("restart.error", ex.Message));
            SendToWeb("server_console", new { line = Ui("restart.error", ex.Message) });
        }
        finally
        {
            _restartInProgress = false;
        }
    }

    private void SendToWeb(string type, object payload)
    {
        // CoreWebView2 must only be touched on the UI thread — never probe it here.
        void Post()
        {
            try
            {
                if (IsDisposed || _webView?.CoreWebView2 is null)
                    return;

                var envelope = new { type, payload };
                string json = JsonSerializer.Serialize(envelope, JsonOptions);
                _webView.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch
            {
                // ignore UI teardown races
            }
        }

        try
        {
            if (IsDisposed)
                return;
            if (InvokeRequired)
                BeginInvoke(Post);
            else
                Post();
        }
        catch
        {
            // ignore if handle is gone
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose
            && e.CloseReason == CloseReason.UserClosing
            && (IsJavaServerOnline() || _serverProcessManager.IsManagedProcessRunning))
        {
            e.Cancel = true;
            PromptCloseWhileServerRunning();
            return;
        }

        try
        {
            _hardwareTimer.Stop();
            _modUpdatePollTimer.Stop();
            _modUpdateStatusTimer.Stop();
            _hardwareTimer.Dispose();
            _modUpdatePollTimer.Dispose();
            _modUpdateStatusTimer.Dispose();
            _schedulerHeartbeat.Dispose();
            try { _modUpdateRestartFlow.TryCancel(); } catch { /* ignore */ }
            try { CancelServerStartedDiscordWatch(); } catch { /* ignore */ }
            // Flush any coalesced config.json write before tearing down.
            ConfigManager.SaveImmediately(_config);
            ConfigManager.DisposeDebounceTimer();
            _statsCollector.Dispose();
            _statsHistory.Dispose();
            _hardwareMonitor.Dispose();
            ClearLogAnalyzeSession();
            _rconManager.Dispose();
            try { _backupCts?.Cancel(); } catch { /* ignore */ }
            try { _backupCts?.Dispose(); } catch { /* ignore */ }
        }
        catch
        {
            // ignore shutdown races
        }

        base.OnFormClosing(e);
    }

    private void PromptCloseWhileServerRunning()
    {
        try
        {
            var stopAndClose = new TaskDialogButton(Ui("close.stopAndClose"));
            var leaveRunning = new TaskDialogButton(Ui("close.leaveRunning"));
            var cancel = TaskDialogButton.Cancel;

            var page = new TaskDialogPage
            {
                Caption = Ui("close.title"),
                Heading = Ui("close.heading"),
                Text = Ui("close.body"),
                Icon = TaskDialogIcon.Warning,
                Buttons = { stopAndClose, leaveRunning, cancel },
                DefaultButton = cancel
            };

            TaskDialogButton result = TaskDialog.ShowDialog(this, page);
            if (result == cancel)
                return;

            if (result == stopAndClose)
            {
                _ = StopServerThenCloseAsync();
                return;
            }

            // Leave server running in the background.
            _allowClose = true;
            BeginInvoke(Close);
        }
        catch (Exception ex)
        {
            // Fallback if TaskDialog is unavailable.
            DialogResult fallback = MessageBox.Show(
                this,
                Ui("close.body") + "\n\n" + ex.Message,
                Ui("close.title"),
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Warning);
            if (fallback == DialogResult.Cancel)
                return;
            if (fallback == DialogResult.Yes)
            {
                _ = StopServerThenCloseAsync();
                return;
            }

            _allowClose = true;
            BeginInvoke(Close);
        }
    }

    private async Task StopServerThenCloseAsync()
    {
        try
        {
            try { _modUpdateRestartFlow.TryCancel(); } catch { /* ignore */ }
            if (!_userStopInProgress)
            {
                // Bypass the normal "restart in progress" guard — user explicitly chose stop & close.
                _userStopInProgress = true;
                try
                {
                    CancelServerStartedDiscordWatch();
                    try
                    {
                        string response = await _rconManager.SendCommandAsync(
                            _config.RconHost, _config.RconPort, _config.RconPassword, "quit");
                        SendToWeb("server_console", new { line = Ui("rcon.prefix", response) });
                        await Task.Delay(5000);
                    }
                    catch
                    {
                        // fall through to force-kill
                    }

                    foreach (string message in _serverProcessManager.KillServerTree())
                        SendToWeb("server_console", new { line = message });
                }
                finally
                {
                    _userStopInProgress = false;
                }
            }
        }
        catch
        {
            // still attempt to close
        }
        finally
        {
            _allowClose = true;
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(Close);
        }
    }
}
