namespace ZomboidManager;

/// <summary>
/// Human-readable labels + categories for Project Zomboid server.ini keys.
/// Display names are German by default (UI localization can map later).
/// </summary>
public static class IniCatalog
{
    public sealed record Meta(string DisplayName, string Category, string InputType = "text", int Order = 100);

    private static readonly Dictionary<string, Meta> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // Allgemein / Server
        ["PublicName"] = new("Öffentlicher Servername", "Allgemein", "text", 10),
        ["PublicDescription"] = new("Serverbeschreibung", "Allgemein", "text", 20),
        ["Open"] = new("Server ist öffentlich gelistet", "Allgemein", "checkbox", 30),
        ["PauseEmpty"] = new("Spielwelt pausiert ohne Spieler", "Allgemein", "checkbox", 40),
        ["SaveWorldEveryMinutes"] = new("Welt speichern alle X Minuten", "Allgemein", "number", 50),
        ["ServerWelcomeMessage"] = new("Willkommensnachricht", "Allgemein", "text", 60),
        ["ResetID"] = new("Reset-ID (Welt-Reset Kennung)", "Allgemein", "number", 70),
        ["ServerPlayerID"] = new("Server Player-ID", "Allgemein", "number", 80),

        // Netzwerk
        ["DefaultPort"] = new("Standard-Port (UDP)", "Netzwerk", "number", 10),
        ["UDPPort"] = new("UDP-Port", "Netzwerk", "number", 20),
        ["SteamPort1"] = new("Steam Port 1", "Netzwerk", "number", 30),
        ["SteamPort2"] = new("Steam Port 2", "Netzwerk", "number", 40),
        ["MaxPlayers"] = new("Maximale Spieleranzahl", "Spieler", "number", 10),
        ["MaxAccountsPerUser"] = new("Max. Accounts pro Steam-User", "Spieler", "number", 20),
        ["PingLimit"] = new("Ping-Limit (ms)", "Netzwerk", "number", 50),
        ["UPnP"] = new("UPnP aktivieren", "Netzwerk", "checkbox", 60),

        // Sicherheit / RCON
        ["Password"] = new("Server-Passwort", "Sicherheit", "password", 10),
        ["PasswordRequired"] = new("Passwort erforderlich", "Sicherheit", "checkbox", 15),
        ["RCONPort"] = new("RCON-Port", "Sicherheit", "number", 20),
        ["RCONPassword"] = new("RCON-Passwort", "Sicherheit", "password", 30),
        ["DoLuaChecksum"] = new("Lua-Checksum prüfen", "Sicherheit", "checkbox", 40),
        ["DenyLoginOnOverloadedServer"] = new("Login bei Überlastung ablehnen", "Sicherheit", "checkbox", 50),
        ["KickFastPlayers"] = new("Zu schnelle Spieler kicken", "Sicherheit", "checkbox", 60),
        ["AntiCheatProtectionType"] = new("Anti-Cheat Schutztyp", "Sicherheit", "number", 70),
        ["AntiCheatProtectionType2"] = new("Anti-Cheat Schutztyp 2", "Sicherheit", "number", 71),
        ["AntiCheatProtectionType3"] = new("Anti-Cheat Schutztyp 3", "Sicherheit", "number", 72),

        // PVP / Chat
        ["PVP"] = new("PvP aktiviert", "Kampf & PvP", "checkbox", 10),
        ["PVPLogToChat"] = new("PvP-Log in den Chat", "Kampf & PvP", "checkbox", 20),
        ["PVPLogToFile"] = new("PvP-Log in Datei", "Kampf & PvP", "checkbox", 30),
        ["SafetySystem"] = new("Sicherheitssystem (PvP-Schutz)", "Kampf & PvP", "checkbox", 40),
        ["ShowSafety"] = new("Sicherheitsstatus anzeigen", "Kampf & PvP", "checkbox", 50),
        ["SafetyToggleTimer"] = new("Sicherheits-Umschaltzeit (Sek.)", "Kampf & PvP", "number", 60),
        ["SafetyCooldownTimer"] = new("Sicherheits-Abklingzeit (Sek.)", "Kampf & PvP", "number", 70),
        ["SafetyDisconnectDelay"] = new("Sicherheits-Disconnect-Verzögerung", "Kampf & PvP", "number", 80),
        ["GlobalChat"] = new("Globaler Chat", "Chat", "checkbox", 10),
        ["ChatStreams"] = new("Chat-Kanäle", "Chat", "text", 20),
        ["DiscordEnable"] = new("Discord-Integration", "Chat", "checkbox", 30),
        ["DiscordToken"] = new("Discord Bot-Token", "Chat", "password", 40),
        ["DiscordChannel"] = new("Discord Kanal", "Chat", "text", 50),

        // Spieleranzeige
        ["DisplayUserName"] = new("Spielernamen anzeigen", "Spieler", "checkbox", 30),
        ["ShowFirstAndLastName"] = new("Vor- und Nachnamen anzeigen", "Spieler", "checkbox", 40),
        ["UsernameDisguises"] = new("Namens-Verkleidungen erlauben", "Spieler", "checkbox", 50),
        ["HideDisguisedUserName"] = new("Verkleidete Namen verstecken", "Spieler", "checkbox", 60),
        ["MouseOverToSeeDisplayName"] = new("Namen nur per Mauszeiger", "Spieler", "checkbox", 70),

        // Spawn / Map
        ["Map"] = new("Karten / Maps", "Welt & Spawn", "text", 10),
        ["SpawnPoint"] = new("Spawn-Punkt (x,y,z)", "Welt & Spawn", "text", 20),
        ["SpawnItems"] = new("Spawn-Gegenstände", "Welt & Spawn", "text", 30),
        ["PlayerRespawnWithSelf"] = new("Respawn bei eigener Leiche", "Welt & Spawn", "checkbox", 40),
        ["PlayerRespawnWithOther"] = new("Respawn bei fremder Leiche", "Welt & Spawn", "checkbox", 50),
        ["HoursForLootRespawn"] = new("Stunden bis Loot-Respawn", "Welt & Spawn", "number", 60),
        ["MaxItemsForLootRespawn"] = new("Max. Items für Loot-Respawn", "Welt & Spawn", "number", 70),
        ["ConstructionPreventsLootRespawn"] = new("Bauten verhindern Loot-Respawn", "Welt & Spawn", "checkbox", 80),

        ["WorkshopItems"] = new("Workshop-IDs (Steam)", "Mods & Workshop", "modlist", 10),
        ["Mods"] = new("Mod-IDs (Spiel)", "Mods & Workshop", "modlist", 20),

        // Steam
        ["SteamScoreboard"] = new("Steam Scoreboard", "Steam", "checkbox", 10),
        ["SteamVAC"] = new("Steam VAC", "Steam", "checkbox", 20),

        // Performance
        ["FastForwardMultiplier"] = new("Zeitraffer-Multiplikator", "Leistung", "number", 20),
        ["NightLengthModifier"] = new("Nachtlänge-Modifikator", "Leistung", "number", 30),

        // Combat-related that may appear in server.ini / mods
        ["MultiHitZombies"] = new("Mehrfachtreffer bei Zombies", "Kampf & PvP", "checkbox", 5),
        ["AttackBlockMovements"] = new("Angriff blockiert Bewegung", "Kampf & PvP", "checkbox", 6),
        ["MeleeHitReaction"] = new("Nahkampf-Trefferreaktion", "Kampf & PvP", "text", 7),
    };

    private static readonly string[] CategoryOrder =
    [
        "Mods & Workshop",
        "Allgemein",
        "Netzwerk",
        "Sicherheit",
        "Spieler",
        "Kampf & PvP",
        "Chat",
        "Welt & Spawn",
        "Steam",
        "Leistung",
        "Sonstiges"
    ];

    public static Meta Resolve(string key, string value)
    {
        if (Map.TryGetValue(key, out Meta? meta))
            return meta;

        string category = "Sonstiges";
        string inputType = "text";
        bool isBool = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

        if (key.Contains("Port", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Minutes", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Timer", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Hours", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Max", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Limit", StringComparison.OrdinalIgnoreCase))
        {
            inputType = "number";
            if (key.Contains("Port", StringComparison.OrdinalIgnoreCase))
                category = "Netzwerk";
        }

        if (key.Contains("Password", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Passwd", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Token", StringComparison.OrdinalIgnoreCase))
        {
            category = "Sicherheit";
            inputType = "password";
        }

        if (key.Equals("WorkshopItems", StringComparison.OrdinalIgnoreCase)
            || key.Equals("Mods", StringComparison.OrdinalIgnoreCase))
        {
            category = "Mods & Workshop";
            inputType = "modlist";
        }
        else if (key.Contains("Workshop", StringComparison.OrdinalIgnoreCase))
        {
            // Non-ID workshop-related leftovers → Steam
            category = "Steam";
        }
        else if (key.Contains("Mod", StringComparison.OrdinalIgnoreCase)
                 && !key.Contains("Mode", StringComparison.OrdinalIgnoreCase))
        {
            // Avoid dumping unrelated *Mod* keys into Mods & Workshop
            category = "Allgemein";
        }

        if (key.Contains("PVP", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Hit", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Combat", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Weapon", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Zombie", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Safety", StringComparison.OrdinalIgnoreCase))
        {
            category = "Kampf & PvP";
        }

        if (isBool)
            inputType = "checkbox";

        return new Meta(PrettifyKey(key), category, inputType, 500);
    }

    public static int CategorySortIndex(string category)
    {
        int idx = Array.FindIndex(CategoryOrder, c =>
            string.Equals(c, category, StringComparison.OrdinalIgnoreCase));
        return idx >= 0 ? idx : 999;
    }

    public static string PrettifyKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return key;

        var chars = new List<char> { key[0] };
        for (int i = 1; i < key.Length; i++)
        {
            char c = key[i];
            if (char.IsUpper(c) && !char.IsUpper(key[i - 1]))
                chars.Add(' ');
            chars.Add(c);
        }

        return new string(chars.ToArray());
    }
}
