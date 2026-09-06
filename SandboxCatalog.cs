namespace ZomboidManager;

/// <summary>
/// Dedicated category map for SandboxVars.lua (independent from server.ini IniCatalog).
/// </summary>
public static class SandboxCatalog
{
    public sealed record Meta(string DisplayName, string Category, string InputType = "text", int Order = 100);

    private static readonly string[] CategoryOrder =
    [
        "Welt & Zeit",
        "Zombies",
        "Loot",
        "Charakter & Skills",
        "Kampf",
        "Fahrzeuge",
        "Farming & Tiere",
        "Wetter & Natur",
        "Krankheit & Verletzung",
        "Map & Meta",
        "Multiplayer",
        "Sonstiges"
    ];

    /// <summary>
    /// Explicit overrides for well-known sandbox keys (German labels preserved where previously used).
    /// </summary>
    private static readonly Dictionary<string, Meta> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MultiHitZombies"] = new("Mehrfachtreffer bei Zombies", "Kampf", "checkbox", 10),
        ["AttackBlockMovements"] = new("Angriff blockiert Bewegung", "Kampf", "checkbox", 20),
        ["MeleeHitReaction"] = new("Nahkampf-Trefferreaktion", "Kampf", "text", 30),
    };

    public static Meta Resolve(string key, string value)
    {
        if (Map.TryGetValue(key, out Meta? meta))
            return meta;

        string category = Categorize(key);
        string inputType = InferInputType(key, value);
        return new Meta(IniCatalog.PrettifyKey(key), category, inputType, 500);
    }

    public static int CategorySortIndex(string category)
    {
        int idx = Array.FindIndex(CategoryOrder, c =>
            string.Equals(c, category, StringComparison.OrdinalIgnoreCase));
        return idx >= 0 ? idx : 999;
    }

    public static IReadOnlyList<string> Categories => CategoryOrder;

    private static string Categorize(string key)
    {
        if (Matches(key,
                "Zombie", "Zombies", "Population", "RespawnHours", "Rally", "Sight", "Hearing",
                "Memory", "Speed", "Strength", "Toughness", "Cognition", "Crawl", "FakeDead",
                "Thump", "Door", "Fence", "Sprinter", "Shambler", "Fast", "Slow", "Distribution"))
            return "Zombies";

        if (Matches(key,
                "Loot", "Food", "Weapon", "Ammo", "Medical", "Literature", "Mechanic", "OtherLoot",
                "Rarity", "Container", "Refrigerator", "Freezer", "SurvivorHouse"))
            return "Loot";

        if (Matches(key,
                "XP", "Skill", "Perk", "Character", "Stats", "Endurance", "Hunger", "Thirst",
                "Fatigue", "Stress", "Morale", "Panic", "Boredom", "Unhappiness", "Nutrition",
                "Calories", "Weight", "StarterKit", "Trait", "Profession"))
            return "Charakter & Skills";

        if (Matches(key,
                "Hit", "Damage", "Combat", "Weapon", "Firearm", "Reload", "Aim", "Melee",
                "MultiHit", "Attack", "Injury", "Fracture", "Wound"))
            return "Kampf";

        if (Matches(key,
                "Car", "Vehicle", "Gas", "Fuel", "Engine", "Traffic", "Parking", "Siren"))
            return "Fahrzeuge";

        if (Matches(key,
                "Farm", "Plant", "Crop", "Animal", "Livestock", "Compost", "Nature", "Erosion",
                "PlantResilience", "Farming"))
            return "Farming & Tiere";

        if (Matches(key,
                "Weather", "Rain", "Snow", "Temperature", "Climate", "Fog", "Wind", "Day",
                "Night", "Moon", "Season", "Temperature"))
            return "Wetter & Natur";

        if (Matches(key,
                "Virus", "Infection", "Poison", "Illness", "Sickness", "Blood", "Mortality",
                "Transmission", "Incubation", "Zombification"))
            return "Krankheit & Verletzung";

        if (Matches(key,
                "Map", "World", "Meta", "Helicopter", "MetaEvent", "Alarm", "HouseAlarm",
                "Generator", "Power", "Water", "Shutoff", "Electricity", "HoursFor",
                "Time", "StartMonth", "StartDay", "StartYear", "StartTime", "DayLength",
                "Fire", "Smoke", "Light"))
            return "Welt & Zeit";

        if (Matches(key,
                "Map", "Tile", "Room", "Building", "World", "Spawn", "Safehouse", "Faction"))
            return "Map & Meta";

        if (Matches(key,
                "Player", "PVP", "Sleep", "Chat", "Voice", "Admin", "Connected", "Multiplayer"))
            return "Multiplayer";

        return "Sonstiges";
    }

    private static bool Matches(string key, params string[] needles) =>
        needles.Any(n => key.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static string InferInputType(string key, string value)
    {
        if (SandboxManager.IsLuaTableLiteral(value))
            return "lua_table";

        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
            return "checkbox";

        if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out _))
            return "number";

        return "text";
    }
}
