using System.Text.Json;

namespace ZomboidManager;

public sealed class ModListDisplayItem
{
    public int Index { get; init; }
    public string WorkshopId { get; init; } = "";
    public string ModId { get; init; } = "";
    public List<string> ModIds { get; init; } = new();
    public bool Grouped { get; init; }
    public bool ManualMapping { get; init; }
    public string SteamUrl { get; init; } = "";
    public string Name { get; init; } = "";
}

/// <summary>
/// Optional 1:N Workshop → Mod ID associations. Unmapped items keep the old 1:1 index pairing.
/// </summary>
public static class ManualModMappingHelper
{
    public static List<ManualModMapping> Normalize(IEnumerable<ManualModMapping>? mappings)
    {
        var result = new List<ManualModMapping>();
        var seenWorkshop = new HashSet<string>(StringComparer.Ordinal);

        foreach (ManualModMapping raw in mappings ?? Enumerable.Empty<ManualModMapping>())
        {
            string workshopId = (raw.WorkshopId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(workshopId) || !seenWorkshop.Add(workshopId))
                continue;

            List<string> modIds = (raw.ModIds ?? new List<string>())
                .Select(id => (id ?? string.Empty).Trim())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            result.Add(new ManualModMapping
            {
                WorkshopId = workshopId,
                DisplayName = (raw.DisplayName ?? string.Empty).Trim(),
                ModIds = modIds
            });
        }

        return result;
    }

    public static ManualModMapping? FindByWorkshopId(
        IEnumerable<ManualModMapping>? mappings,
        string workshopId)
    {
        string id = (workshopId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(id))
            return null;

        return Normalize(mappings).FirstOrDefault(m =>
            string.Equals(m.WorkshopId, id, StringComparison.Ordinal));
    }

    /// <summary>
    /// Label used in logs, UI check results, and Discord {mods}.
    /// Unmapped IDs keep Steam title (or the raw Workshop ID).
    /// </summary>
    public static string FormatUpdateLabel(
        string workshopId,
        string? steamTitle,
        IEnumerable<ManualModMapping>? mappings)
    {
        string fallback = string.IsNullOrWhiteSpace(steamTitle)
            ? (workshopId ?? string.Empty).Trim()
            : steamTitle.Trim();

        ManualModMapping? mapping = FindByWorkshopId(mappings, workshopId ?? string.Empty);
        if (mapping is null)
            return fallback;

        string name = !string.IsNullOrWhiteSpace(mapping.DisplayName)
            ? mapping.DisplayName
            : fallback;

        if (mapping.ModIds.Count == 0)
            return name;

        return $"{name} ({string.Join(", ", mapping.ModIds)})";
    }

    public static List<string> WarnModIdsNotInConfiguredList(
        IEnumerable<ManualModMapping>? mappings,
        IEnumerable<string> configuredModIds)
    {
        var configured = new HashSet<string>(
            (configuredModIds ?? Enumerable.Empty<string>())
                .Select(id => (id ?? string.Empty).Trim())
                .Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.OrdinalIgnoreCase);

        var warnings = new List<string>();
        foreach (ManualModMapping mapping in Normalize(mappings))
        {
            foreach (string modId in mapping.ModIds)
            {
                if (!configured.Contains(modId))
                {
                    warnings.Add(
                        $"Mod ID '{modId}' is mapped to Workshop {mapping.WorkshopId} but is not in the configured Mods= list.");
                }
            }
        }

        return warnings;
    }

    public static List<ModListDisplayItem> BuildDisplayItems(
        IReadOnlyList<string> workshopIds,
        IReadOnlyList<string> modIds,
        IEnumerable<ManualModMapping>? mappings)
    {
        List<string> workshops = (workshopIds ?? Array.Empty<string>())
            .Select(id => (id ?? string.Empty).Trim())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();
        List<string> mods = (modIds ?? Array.Empty<string>())
            .Select(id => (id ?? string.Empty).Trim())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();

        List<ManualModMapping> normalized = Normalize(mappings);
        var consumedWorkshops = new HashSet<string>(StringComparer.Ordinal);
        var consumedMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<ModListDisplayItem>();
        int index = 1;

        foreach (ManualModMapping mapping in normalized)
        {
            if (!workshops.Contains(mapping.WorkshopId, StringComparer.Ordinal))
                continue;

            consumedWorkshops.Add(mapping.WorkshopId);
            foreach (string modId in mapping.ModIds)
            {
                if (mods.Contains(modId, StringComparer.OrdinalIgnoreCase))
                    consumedMods.Add(modId);
            }

            string name = !string.IsNullOrWhiteSpace(mapping.DisplayName)
                ? mapping.DisplayName
                : (mapping.ModIds.Count > 0 ? mapping.ModIds[0] : mapping.WorkshopId);

            items.Add(new ModListDisplayItem
            {
                Index = index++,
                WorkshopId = mapping.WorkshopId,
                ModId = mapping.ModIds.Count > 0 ? string.Join("; ", mapping.ModIds) : "",
                ModIds = mapping.ModIds.ToList(),
                Grouped = mapping.ModIds.Count > 1,
                ManualMapping = true,
                SteamUrl = SteamUrl(mapping.WorkshopId),
                Name = name
            });
        }

        List<string> leftoverWorkshops = workshops
            .Where(id => !consumedWorkshops.Contains(id))
            .ToList();
        List<string> leftoverMods = mods
            .Where(id => !consumedMods.Contains(id))
            .ToList();

        int leftoverCount = Math.Max(leftoverWorkshops.Count, leftoverMods.Count);
        for (int i = 0; i < leftoverCount; i++)
        {
            string workshopId = i < leftoverWorkshops.Count ? leftoverWorkshops[i] : "";
            string modId = i < leftoverMods.Count ? leftoverMods[i] : "";
            items.Add(new ModListDisplayItem
            {
                Index = index++,
                WorkshopId = workshopId,
                ModId = modId,
                ModIds = string.IsNullOrWhiteSpace(modId) ? new List<string>() : new List<string> { modId },
                Grouped = false,
                ManualMapping = false,
                SteamUrl = SteamUrl(workshopId),
                Name = string.IsNullOrWhiteSpace(modId) ? workshopId : modId
            });
        }

        return items;
    }

    public static object ToUiPayload(ManualModMapping mapping) => new
    {
        workshopId = mapping.WorkshopId,
        modIds = mapping.ModIds,
        displayName = mapping.DisplayName
    };

    public static object ToUiPayload(ModListDisplayItem item) => new
    {
        index = item.Index,
        workshopId = item.WorkshopId,
        modId = item.ModId,
        modIds = item.ModIds,
        grouped = item.Grouped,
        manualMapping = item.ManualMapping,
        steamUrl = item.SteamUrl,
        name = item.Name
    };

    private static string SteamUrl(string workshopId) =>
        string.IsNullOrWhiteSpace(workshopId)
            ? ""
            : $"https://steamcommunity.com/sharedfiles/filedetails/?id={workshopId}";

    public static void RunSelfTest()
    {
        string outPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZomboidManager",
            "manual-mod-mapping-selftest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        try
        {
            var mapping = new ManualModMapping
            {
                WorkshopId = "111",
                DisplayName = "Spongie's Character Customisation",
                ModIds = new List<string> { "ModA", "ModB", "ModC", "ModD", "ModE" }
            };

            List<ModListDisplayItem> items = BuildDisplayItems(
                new[] { "111", "222" },
                new[] { "ModA", "ModB", "ModC", "ModD", "ModE", "OtherMod" },
                new[] { mapping });

            bool groupedOk = items.Count == 2
                && items[0].ManualMapping
                && items[0].Grouped
                && items[0].Name == "Spongie's Character Customisation"
                && items[0].ModIds.Count == 5
                && items[0].ModIds.SequenceEqual(new[] { "ModA", "ModB", "ModC", "ModD", "ModE" })
                && items[1].WorkshopId == "222"
                && items[1].ModId == "OtherMod"
                && !items[1].ManualMapping;

            List<ModListDisplayItem> unmapped = BuildDisplayItems(
                new[] { "111", "222" },
                new[] { "ModA", "ModB" },
                Array.Empty<ManualModMapping>());
            bool oneToOneOk = unmapped.Count == 2
                && unmapped[0].ModId == "ModA"
                && unmapped[1].ModId == "ModB"
                && !unmapped[0].ManualMapping;

            string label = FormatUpdateLabel("111", "Steam Title Ignored When Named", new[] { mapping });
            bool labelOk = label == "Spongie's Character Customisation (ModA, ModB, ModC, ModD, ModE)";

            string unmappedLabel = FormatUpdateLabel("222", "Plain Mod", Array.Empty<ManualModMapping>());
            bool unmappedLabelOk = unmappedLabel == "Plain Mod";

            string discord = DiscordEventCatalog.ApplyTemplate(
                "🔔 **Game Server** – Mod Update: {mods}",
                new Dictionary<string, string> { ["mods"] = label });
            bool discordOk = discord.Contains("Spongie's Character Customisation", StringComparison.Ordinal)
                && discord.Contains("ModA", StringComparison.Ordinal)
                && discord.Contains("ModE", StringComparison.Ordinal);

            List<string> warnings = WarnModIdsNotInConfiguredList(
                new[]
                {
                    mapping,
                    new ManualModMapping
                    {
                        WorkshopId = "999",
                        ModIds = new List<string> { "TypoMod" },
                        DisplayName = "Broken"
                    }
                },
                new[] { "ModA", "ModB", "ModC", "ModD", "ModE" });
            bool warnOk = warnings.Count == 1 && warnings[0].Contains("TypoMod", StringComparison.Ordinal);

            bool ok = groupedOk && oneToOneOk && labelOk && unmappedLabelOk && discordOk && warnOk;
            File.WriteAllText(outPath, JsonSerializer.Serialize(new
            {
                ok,
                groupedOk,
                oneToOneOk,
                labelOk,
                unmappedLabelOk,
                discordOk,
                warnOk,
                label,
                discord,
                warnings,
                groupedNames = items.Select(i => i.Name).ToList()
            }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            File.WriteAllText(outPath, JsonSerializer.Serialize(new { ok = false, error = ex.ToString() }));
            Environment.ExitCode = 1;
        }
    }
}
