using System.Text.Json;

namespace ZomboidManager;

public class AppConfig
{
    public List<int> SelectedHours { get; set; } = new();
    public string ServerPath { get; set; } = string.Empty;
    public string SteamCmdPath { get; set; } = "C:\\steamcmd\\steamcmd.exe";
    /// <summary>Steam beta branch for app_update. Empty or "public" = B42 Stable (default since 42.20).</summary>
    public string SteamUpdateBranch { get; set; } = string.Empty;
    public string StartBat { get; set; } = string.Empty;
    public string RconHost { get; set; } = "127.0.0.1";
    public int RconPort { get; set; } = 27015;
    public string RconPassword { get; set; } = string.Empty;
    public bool SchedulerActive { get; set; } = false;

    public bool Announce10MinBeforeRestart { get; set; } = false;
    public bool Announce5MinBeforeRestart { get; set; } = false;

    public string ZomboidUserPath { get; set; } = string.Empty;
    public string ZomboidDataPath { get; set; } = string.Empty;
    public string LastIniFilePath { get; set; } = string.Empty;
    /// <summary>UI language: de or en. Empty = not chosen yet (first-run modal).</summary>
    public string UiLanguage { get; set; } = string.Empty;

    public string DiscordWebhookUrl { get; set; } = string.Empty;
    public bool DiscordNotifyEnabled { get; set; } = false;
    /// <summary>Legacy custom hourly text — migrated into discordEvents[custom_hourly] when empty.</summary>
    public string DiscordCustomMessage { get; set; } = string.Empty;
    public List<int> DiscordCustomHours { get; set; } = new();
    /// <summary>Per-event Discord notification templates (enable + message text).</summary>
    public List<DiscordEventSlot> DiscordEvents { get; set; } = new();

    /// <summary>Up to 5 scheduled server chat broadcast slots.</summary>
    public List<BroadcastMessageSlot> BroadcastMessages { get; set; } = new();

    public BackupScheduleConfig BackupSchedule { get; set; } = new();

    public ModUpdateAutoRestartConfig ModUpdateAutoRestart { get; set; } = new();

    /// <summary>
    /// Optional Workshop ID → multiple Mod IDs (plus display name) for packs that are not 1:1.
    /// </summary>
    public List<ManualModMapping> ManualModMappings { get; set; } = new();

    /// <summary>Minutes between historical stats samples while the server is running (1–5).</summary>
    public int StatsIntervalMinutes { get; set; } = 2;

    /// <summary>Days of stats/restart history to keep (default 30).</summary>
    public int StatsRetentionDays { get; set; } = 30;
}

public class ManualModMapping
{
    public string WorkshopId { get; set; } = string.Empty;
    public List<string> ModIds { get; set; } = new();
    public string DisplayName { get; set; } = string.Empty;
}

public class ModUpdateAutoRestartConfig
{
    public bool Enabled { get; set; }
    public string WarningMessage { get; set; } =
        "A mod has been updated. The server will restart in {minutes} minutes to apply the update.";
    public int WarnMinutesBefore { get; set; } = 5;
    public bool WaitForEmpty { get; set; }
    public int MaxWaitMinutes { get; set; } = 60;
}

public class BackupScheduleConfig
{
    public bool Enabled { get; set; }
    /// <summary>"recurring" (weekly) or "oneTime".</summary>
    public string Mode { get; set; } = "recurring";
    /// <summary>DayOfWeek ints: 0=Sunday … 6=Saturday.</summary>
    public List<int> Days { get; set; } = new();
    /// <summary>HH:mm local time for recurring backups.</summary>
    public string Time { get; set; } = "03:00";
    /// <summary>Local datetime for one-time backup.</summary>
    public string OneTimeDateTime { get; set; } = string.Empty;
}

public class BroadcastMessageSlot
{
    public string Message { get; set; } = string.Empty;
    /// <summary>"oneTime" or "recurring".</summary>
    public string Mode { get; set; } = "oneTime";
    /// <summary>Local datetime string for one-time sends (e.g. 2026-08-11T15:30).</summary>
    public string ScheduledTime { get; set; } = string.Empty;
    public int IntervalValue { get; set; } = 1;
    /// <summary>"minutes" or "hours".</summary>
    public string IntervalUnit { get; set; } = "hours";
    public bool Enabled { get; set; }
    /// <summary>Next planned send (local). Persisted so schedules survive app restarts.</summary>
    public DateTime? NextSendAt { get; set; }
}

public static class ConfigManager
{
    /// <summary>
    /// Stored under LocalAppData so Velopack updates (which replace the app folder) do not wipe settings.
    /// </summary>
    private static readonly string ConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZomboidManager");

    private static readonly string ConfigPath = Path.Combine(ConfigDirectory, "config.json");

    private static readonly string LegacyConfigPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>Coalesce rapid Save() calls into one disk write.</summary>
    public const int DebounceMilliseconds = 500;

    private static readonly object Sync = new();
    private static AppConfig? _pendingConfig;
    private static System.Threading.Timer? _debounceTimer;

    /// <summary>
    /// Loads config from disk. On parse/IO failure, backs up the broken file and returns defaults
    /// plus backup path / error detail for a user-visible warning.
    /// </summary>
    public static (AppConfig Config, string? BrokenBackupPath, string? ErrorDetail) LoadWithStatus()
    {
        try
        {
            MigrateLegacyConfigIfNeeded();

            if (!File.Exists(ConfigPath))
                return (new AppConfig(), null, null);

            string json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json);
            if (config is null)
            {
                string? backup = TryBackupBrokenConfig("deserialize returned null");
                return (new AppConfig(), backup, "deserialize returned null");
            }

            NormalizeUiLanguage(config);
            return (config, null, null);
        }
        catch (Exception ex)
        {
            string? backup = TryBackupBrokenConfig(ex.Message);
            return (new AppConfig(), backup, ex.Message);
        }
    }

    public static AppConfig Load()
    {
        var (config, _, _) = LoadWithStatus();
        return config;
    }

    private static string? TryBackupBrokenConfig(string reason)
    {
        try
        {
            if (!File.Exists(ConfigPath))
                return null;

            Directory.CreateDirectory(ConfigDirectory);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string backupPath = Path.Combine(ConfigDirectory, $"config.json.broken.{stamp}");
            File.Copy(ConfigPath, backupPath, overwrite: false);
            try
            {
                File.AppendAllText(
                    backupPath + ".reason.txt",
                    DateTime.Now.ToString("o") + " — " + reason + Environment.NewLine);
            }
            catch
            {
                // backup of the json is enough
            }

            return backupPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Marks config dirty and schedules a disk write after <see cref="DebounceMilliseconds"/>.
    /// Further calls within the window reset the delay (one coalesced write).
    /// </summary>
    public static void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        lock (Sync)
        {
            _pendingConfig = config;
            _debounceTimer ??= new System.Threading.Timer(static _ =>
            {
                try
                {
                    FlushPending();
                }
                catch
                {
                    // never throw from timer thread
                }
            });
            _debounceTimer.Change(DebounceMilliseconds, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Cancels any pending debounced write and writes <paramref name="config"/> to disk now.
    /// Use for explicit user "save settings" actions and similar confirmation paths.
    /// </summary>
    public static void SaveImmediately(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        lock (Sync)
        {
            _pendingConfig = null;
            _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        WriteToDisk(config);
    }

    /// <summary>
    /// Writes any pending debounced config immediately (no-op if nothing is dirty).
    /// Call on clean app shutdown so coalesced changes are not lost.
    /// </summary>
    public static void FlushPending()
    {
        AppConfig? config;
        lock (Sync)
        {
            config = _pendingConfig;
            _pendingConfig = null;
            _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        if (config is not null)
            WriteToDisk(config);
    }

    /// <summary>Disposes the debounced save timer after a final flush/save on shutdown.</summary>
    public static void DisposeDebounceTimer()
    {
        lock (Sync)
        {
            _pendingConfig = null;
            _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }

    private static void WriteToDisk(AppConfig config)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(ConfigDirectory);
            string json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }
    }

    private static void NormalizeUiLanguage(AppConfig config)
    {
        if (config.UiLanguage == "de")
            return;

        if (string.IsNullOrWhiteSpace(config.UiLanguage))
            return;

        config.UiLanguage = "en";
    }

    private static void MigrateLegacyConfigIfNeeded()
    {
        if (File.Exists(ConfigPath) || !File.Exists(LegacyConfigPath))
            return;

        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            File.Copy(LegacyConfigPath, ConfigPath, overwrite: false);
        }
        catch
        {
            // keep using defaults / next Save will write a fresh file
        }
    }
}
