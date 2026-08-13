namespace ZomboidManager;

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

/// <summary>
/// Known Discord notification events, default templates, and placeholder substitution.
/// </summary>
public static class DiscordEventCatalog
{
    public const string RestartRoutineStarted = "restart_routine_started";
    public const string ScheduledRestart = "scheduled_restart";
    public const string ServerRestarted = "server_restarted";
    public const string ModsUpdated = "mods_updated";
    public const string CustomHourly = "custom_hourly";

    public static readonly DiscordEventDefinition[] Definitions =
    {
        new()
        {
            EventKey = RestartRoutineStarted,
            DisplayName = "Restart routine started",
            DefaultTemplate = "🔄 Server restart routine started.",
            Placeholders = new[] { "time", "date", "datetime" },
            PlaceholderHelp = "{time}, {date}, {datetime}"
        },
        new()
        {
            EventKey = ScheduledRestart,
            DisplayName = "Scheduled restart triggered",
            DefaultTemplate = "🔄 Scheduled server restart triggered ({hour}:00).",
            Placeholders = new[] { "time", "date", "datetime", "hour" },
            PlaceholderHelp = "{time}, {date}, {datetime}, {hour}"
        },
        new()
        {
            EventKey = ServerRestarted,
            DisplayName = "Server restarted",
            DefaultTemplate = "▶️ Server restarted",
            Placeholders = new[] { "time", "date", "datetime", "mods" },
            PlaceholderHelp = "{time}, {date}, {datetime}, {mods} (updated mod titles, or empty)"
        },
        new()
        {
            EventKey = ModsUpdated,
            DisplayName = "Mods updated",
            DefaultTemplate = "🔄 Mods updated: {mods}",
            Placeholders = new[] { "time", "date", "datetime", "mods" },
            PlaceholderHelp = "{time}, {date}, {datetime}, {mods} — only sent when Workshop mods changed"
        },
        new()
        {
            EventKey = CustomHourly,
            DisplayName = "Custom hourly message",
            DefaultTemplate = "📢 Scheduled announcement",
            Placeholders = new[] { "time", "date", "datetime", "hour" },
            PlaceholderHelp = "{time}, {date}, {datetime}, {hour} — sent at the hours selected below",
            DefaultEnabled = false
        }
    };

    public static List<DiscordEventSlot> Normalize(
        IEnumerable<DiscordEventSlot>? slots,
        string? legacyCustomMessage = null)
    {
        var byKey = (slots ?? Enumerable.Empty<DiscordEventSlot>())
            .Where(s => !string.IsNullOrWhiteSpace(s.EventKey))
            .GroupBy(s => s.EventKey.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        var result = new List<DiscordEventSlot>(Definitions.Length);
        foreach (DiscordEventDefinition def in Definitions)
        {
            if (byKey.TryGetValue(def.EventKey, out DiscordEventSlot? existing))
            {
                result.Add(new DiscordEventSlot
                {
                    EventKey = def.EventKey,
                    Enabled = existing.Enabled,
                    Template = string.IsNullOrWhiteSpace(existing.Template)
                        ? def.DefaultTemplate
                        : existing.Template
                });
            }
            else
            {
                string template = def.DefaultTemplate;
                if (def.EventKey == CustomHourly
                    && !string.IsNullOrWhiteSpace(legacyCustomMessage))
                {
                    template = legacyCustomMessage.Trim();
                }

                result.Add(new DiscordEventSlot
                {
                    EventKey = def.EventKey,
                    Enabled = def.DefaultEnabled,
                    Template = template
                });
            }
        }

        return result;
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
