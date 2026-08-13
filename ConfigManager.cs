using System.Text.Json;

namespace ZomboidManager;

public class AppConfig
{
    public List<int> SelectedHours { get; set; } = new();
    public string ServerPath { get; set; } = string.Empty;
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
    /// <summary>UI language code, e.g. de, en, fr. Empty = not chosen yet.</summary>
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

    public static AppConfig Load()
    {
        try
        {
            MigrateLegacyConfigIfNeeded();

            if (!File.Exists(ConfigPath))
                return new AppConfig();

            string json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    public static void Save(AppConfig config)
    {
        Directory.CreateDirectory(ConfigDirectory);
        string json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(ConfigPath, json);
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
