namespace ZomboidManager;

/// <summary>
/// Known Discord notification events, default templates, and placeholder substitution.
/// Only the four active events are exposed in the Discord Events UI.
/// </summary>
public class DiscordEventSlot
{
    public string EventKey { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string Template { get; set; } = string.Empty;
}

public sealed class DiscordEventDefinition
{
    public required string EventKey { get; init; }
    public required string DisplayName { get; init; }
    public required string DefaultTemplate { get; init; }
    public required string[] Placeholders { get; init; }
    public required string PlaceholderHelp { get; init; }
    public bool DefaultEnabled { get; init; } = true;
}

public static class DiscordEventCatalog
{
    // Active (UI) events
    public const string PreRestartWarning = "pre_restart_warning";
    public const string ServerRestarting = "server_restarting";
    public const string ServerRestarted = "server_restarted";
    public const string ModsUpdated = "mods_updated";

    // Legacy keys — kept so old call sites / configs compile; no longer in Definitions.
    public const string RestartRoutineStarted = "restart_routine_started";
    public const string ScheduledRestart = "scheduled_restart";
    public const string ServerShuttingDown = "server_shutting_down";
    public const string CustomHourly = "custom_hourly";

    private static readonly Dictionary<string, string[]> LegacyDefaultTemplates =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [PreRestartWarning] = new[]
            {
                "⏰ **Game Server** – Restart in {minutes} minutes"
            },
            [ServerRestarting] = new[]
            {
                "🔴 **Game Server** – Server restarting",
                "🔴 **Game Server** – Server is restarting",
                "🔴 **Game Server** – Server shutting down"
            },
            [ServerRestarted] = new[]
            {
                "🟢 **Game Server** – Server is back online",
                "▶️ Server restarted"
            },
            [ModsUpdated] = new[]
            {
                "🔔 **Game Server** – Mod Update: {mods}",
                "🔄 Mods updated: {mods}"
            }
        };

    public static readonly DiscordEventDefinition[] Definitions =
    {
        new()
        {
            EventKey = PreRestartWarning,
            DisplayName = "Restart in 5 minutes",
            DefaultTemplate = "⏰ **Game Server** – Restart in 5 minutes",
            Placeholders = new[] { "time", "date", "datetime", "minutes" },
            PlaceholderHelp = "{time}, {date}, {datetime}, {minutes} — Discord only for the 5-minute warning"
        },
        new()
        {
            EventKey = ServerRestarting,
            DisplayName = "Server restarting",
            DefaultTemplate = "🔴 **Game Server** – Server is restarting (takes up to 10 minutes)",
            Placeholders = new[] { "time", "date", "datetime" },
            PlaceholderHelp = "{time}, {date}, {datetime}"
        },
        new()
        {
            EventKey = ServerRestarted,
            DisplayName = "Server is back online",
            DefaultTemplate = "🟢 **Game Server** – Server is back online",
            Placeholders = new[] { "time", "date", "datetime", "mods" },
            PlaceholderHelp = "{time}, {date}, {datetime}, {mods} — sent after *** SERVER STARTED *** in the server log"
        },
        new()
        {
            EventKey = ModsUpdated,
            DisplayName = "Mods updated",
            DefaultTemplate = "🔔 **Game Server** – Mod Update: {mods}",
            Placeholders = new[] { "time", "date", "datetime", "mods" },
            PlaceholderHelp = "{time}, {date}, {datetime}, {mods} — only sent when Workshop mods changed"
        }
    };

    public static List<DiscordEventSlot> Normalize(
        IEnumerable<DiscordEventSlot>? slots,
        string? legacyCustomMessage = null)
    {
        _ = legacyCustomMessage;

        var byKey = (slots ?? Enumerable.Empty<DiscordEventSlot>())
            .Where(s => !string.IsNullOrWhiteSpace(s.EventKey))
            .GroupBy(s => s.EventKey.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        var result = new List<DiscordEventSlot>(Definitions.Length);
        foreach (DiscordEventDefinition def in Definitions)
        {
            if (byKey.TryGetValue(def.EventKey, out DiscordEventSlot? existing))
            {
                string template = string.IsNullOrWhiteSpace(existing.Template)
                    ? def.DefaultTemplate
                    : existing.Template;

                if (IsLegacyDefaultTemplate(def.EventKey, template))
                    template = def.DefaultTemplate;

                result.Add(new DiscordEventSlot
                {
                    EventKey = def.EventKey,
                    Enabled = existing.Enabled,
                    Template = template
                });
            }
            else
            {
                result.Add(new DiscordEventSlot
                {
                    EventKey = def.EventKey,
                    Enabled = def.DefaultEnabled,
                    Template = def.DefaultTemplate
                });
            }
        }

        return result;
    }

    private static bool IsLegacyDefaultTemplate(string eventKey, string template)
    {
        if (!LegacyDefaultTemplates.TryGetValue(eventKey, out string[]? legacy))
            return false;

        string trimmed = (template ?? string.Empty).Trim();
        return legacy.Any(old => string.Equals(old, trimmed, StringComparison.Ordinal));
    }

    public static string ApplyTemplate(string template, IReadOnlyDictionary<string, string> placeholders)
    {
        string result = template ?? string.Empty;
        foreach (KeyValuePair<string, string> pair in placeholders)
        {
            result = result.Replace(
                "{" + pair.Key + "}",
                pair.Value ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
        }

        return result.Trim();
    }

    public static object BuildUiPayload(IEnumerable<DiscordEventSlot>? slots, string? legacyCustomMessage = null)
    {
        List<DiscordEventSlot> normalized = Normalize(slots, legacyCustomMessage);
        return normalized.Select(slot =>
        {
            DiscordEventDefinition def = Definitions.First(d => d.EventKey == slot.EventKey);
            return new
            {
                eventKey = slot.EventKey,
                enabled = slot.Enabled,
                template = slot.Template,
                displayName = def.DisplayName,
                placeholders = def.Placeholders,
                placeholderHelp = def.PlaceholderHelp,
                defaultTemplate = def.DefaultTemplate
            };
        }).ToList();
    }
}
