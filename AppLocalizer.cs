using System.Collections.Frozen;

namespace ZomboidManager;

/// <summary>English and German UI strings for backend / WebView messages.</summary>
public static class AppLocalizer
{
    private static readonly FrozenDictionary<string, string> En = BuildEn();
    private static readonly FrozenDictionary<string, string> De = BuildDe();

    public static string NormalizeLanguage(string? lang) =>
        string.Equals(lang, "de", StringComparison.OrdinalIgnoreCase) ? "de" : "en";

    public static string Get(string? lang, string key)
    {
        string normalized = NormalizeLanguage(lang);
        if (normalized == "de" && De.TryGetValue(key, out string? de))
            return de;
        if (En.TryGetValue(key, out string? en))
            return en;
        return key;
    }

    public static string Format(string? lang, string key, params object[] args)
    {
        string format = Get(lang, key);
        try
        {
            return string.Format(format, args);
        }
        catch
        {
            return format;
        }
    }

    private static FrozenDictionary<string, string> BuildEn() =>
        new Dictionary<string, string>
        {
            // SteamCMD update
            ["steamcmd.notFound"] = "SteamCMD not found: {0}",
            ["steamcmd.alreadyRunning"] = "A server update is already running.",
            ["steamcmd.confirm"] =
                "Check and update to the latest B42 Stable branch. If needed, the server will be stopped. Continue?",
            ["steamcmd.checking"] = "⏳ Checking B42 Stable update…",
            ["steamcmd.serverPathMissing"] = "⚠️ Server path not configured.",
            ["steamcmd.stoppingServer"] = "⏳ Stopping server for update…",
            ["steamcmd.running"] = "⏳ SteamCMD update running…",
            ["steamcmd.couldNotStart"] = "⚠️ SteamCMD could not be started.",
            ["steamcmd.finished"] = "✅ B42 Stable update finished.",
            ["steamcmd.finishedStartServer"] = " Start the server once to verify the version in the log.",
            ["steamcmd.finishedVersion"] = " Version: {0}.",
            ["steamcmd.errorCode"] = "⚠️ SteamCMD error (code {0}).",
            ["steamcmd.failed"] = "⚠️ Update failed: {0}",
            ["steamcmd.toastUpToDate"] = "Server is already up to date.",
            ["steamcmd.toastSuccess"] = "Server updated successfully!",
            ["steamcmd.statusInProgress"] = "Update in progress…",
            ["steamcmd.statusChecking"] = "Checking for updates…",
            ["steamcmd.statusStopping"] = "Stopping server…",
            ["steamcmd.statusRunning"] = "Running SteamCMD update…",
            ["steamcmd.checkUnclear"] = "⚠️ Could not verify update status — update will run.",
            ["steamcmd.upToDate"] =
                "✅ B42 Stable is up to date (Steam: already installed). Start the server once to see the version in the log.",
            ["steamcmd.upToDateVersion"] = "✅ B42 Stable is up to date (version {0}).",
            ["steamcmd.updateStarting"] = "⏳ B42 Stable update will start (version {0})…",
            ["steamcmd.updateStartingNoVersion"] = "⏳ B42 Stable update will start…",
            ["steamcmd.branchMismatch"] = "Installed branch is beta {0}, target is {1}.",
            ["steamcmd.branch.b42Stable"] = "B42 Stable",
            ["steamcmd.branch.build41"] = "Build 41",
            ["steamcmd.branch.b42Unstable"] = "B42 Unstable (42.19)",
            ["steamcmd.branch.beta"] = "beta {0}",

            // Settings / general
            ["settings.saved"] = "Settings saved.",
            ["hours.saved"] = "Hours saved: {0}",

            // Server control
            ["server.alreadyOnline"] = "Server already appears online (Java process detected). Press Stop first, then Start.",
            ["server.starting"] = "Starting: {0}",
            ["server.consoleOpened"] = "Server console opened (PID {0}).",
            ["server.startFailed"] = "Start failed: {0}",
            ["server.stoppingRcon"] = "Stopping via RCON quit...",
            ["server.stopCompleted"] = "Stop completed.",
            ["server.startBatMissing"] = "Start file not found. Configure Server folder + Start .bat in Settings.",
            ["server.alreadyOnlineShort"] = "Server already online. Press Stop first, then Start.",
            ["server.startRequested"] = "Server start requested — console window should open.",
            ["server.startedMarkerTimeout"] =
                "Timed out waiting for *** SERVER STARTED *** (20 min) — server may have failed to finish booting.",
            ["server.startedMarkerWatchFailed"] = "Server-start watch failed: {0}",
            ["server.processExited"] = "[server process exited]",
            ["server.noRestartYet"] = "No restart in this session yet",

            // RCON
            ["rcon.noResponse"] = "No response from server.",
            ["rcon.connectionOk"] = "Connection successful",
            ["rcon.noCommand"] = "No command entered.",
            ["rcon.emptyResponse"] = "(empty response)",
            ["rcon.noRconResponse"] = "No RCON response.",
            ["rcon.prefix"] = "RCON: {0}",
            ["rcon.response"] = "RCON response: {0}",

            // Config / INI
            ["config.noIniLoaded"] = "No INI loaded. Please load a server.ini in the Config tab first.",
            ["config.iniSaved"] = "Configuration saved!",
            ["config.error"] = "Error: {0}",
            ["config.dialogError"] = "Dialog error: {0}",
            ["config.sandboxSaved"] = "SandboxVars saved.",
            ["config.sandboxNotFound"] =
                "SandboxVars.lua not found. Set Zomboid data path and load a server.ini first.",
            ["config.profileSaved"] = "Profile '{0}' saved.",
            ["config.profileApplied"] = "Profile '{0}' applied.",
            ["config.profileRenamed"] = "Profile renamed to '{0}'.",
            ["config.profileDeleted"] = "Profile deleted.",
            ["config.noBatFound"] = "No .bat files found — please select the startup file manually.",
            ["config.startBatAuto"] = "Startup file auto-detected: {0}",
            ["config.multipleBat"] = "Multiple .bat files found — please select the startup file manually.",

            // File dialogs
            ["dialog.sandboxVars"] = "Select SandboxVars.lua",
            ["dialog.serverIni"] = "Select Project Zomboid server configuration",
            ["dialog.serverFolder"] = "Select Project Zomboid server folder (folder with StartServer64.bat)",
            ["dialog.zomboidData"] = "Select Zomboid data folder (folder with 'Server' subfolder, e.g. …\\Zomboid)",
            ["dialog.logFile"] = "Open log file",
            ["dialog.startBat"] = "Select server startup .bat file",

            // Backup
            ["backup.cancelRequested"] = "Backup cancel requested.",
            ["backup.scheduleSaved"] = "Backup schedule saved.",
            ["backup.alreadyRunning"] = "A backup is already in progress.",
            ["backup.scheduledTriggered"] = "Scheduled backup triggered.",
            ["backup.oneTimeDisabled"] = "One-time backup schedule disabled after run.",
            ["backup.completed"] = "Scheduled backup completed: {0}",
            ["backup.cancelled"] = "Scheduled backup cancelled.",
            ["backup.cancelledMessage"] = "Backup cancelled.",
            ["backup.failed"] = "Scheduled backup failed: {0}",

            // Broadcast
            ["broadcast.saved"] = "Broadcast messages saved.",
            ["broadcast.invalidSlot"] = "Invalid broadcast slot.",
            ["broadcast.emptyMessage"] = "Message text is empty.",
            ["broadcast.serverOffline"] = "Server offline — message not sent.",
            ["broadcast.sentNow"] = "Slot {0} sent now.",
            ["broadcast.rconFailed"] = "RCON send failed.",
            ["broadcast.logSendNowCommand"] = "Broadcast slot {0} send-now: {1}",
            ["broadcast.logSentNowUnchanged"] = "Broadcast slot {0} sent now (schedule unchanged).",
            ["broadcast.logSendNowFailed"] = "Broadcast slot {0} send-now failed: {1}",
            ["broadcast.logSkippedOffline"] = "Broadcast slot {0} skipped (server offline).",
            ["broadcast.logFailedRcon"] = "Broadcast slot {0} failed (RCON): {1}",
            ["broadcast.logSent"] = "Broadcast slot {0} sent.",
            ["broadcast.logFailed"] = "Broadcast slot {0} failed: {1}",

            // Mod updates
            ["mods.autoRestartSaved"] = "Mod Update Auto-Restart settings saved.",
            ["mods.noWorkshopIds"] = "No Workshop IDs configured (load a server.ini in Config first).",
            ["mods.noUpdates"] = "No mod updates detected.",
            ["mods.updated"] = "Mods updated: {0}",
            ["mods.checkFailed"] = "Mod update check failed: {0}",
            ["mods.restartInProgress"] = "Mod updates found but a restart is already in progress — Discord notify only.",
            ["mods.manualCheck"] = "Manual Workshop mod update check…",
            ["mods.scheduledCheck"] = "Scheduled Workshop mod update check…",
            ["mods.workshopUpdatesDetected"] = "Workshop updates detected: {0}",
            ["mods.restartAbortedNoBat"] = "Mod-update restart aborted: start file not found.",
            ["mods.restartAbortedInProgress"] = "Mod-update restart aborted: another restart is in progress.",
            ["mods.pendingRestartCancelled"] = "Mod-update pending restart cancelled.",
            ["mods.noPendingRestart"] = "No cancellable mod-update restart pending.",
            ["mods.autoRestartFailed"] =
                "Mod update was detected but automatic restart failed and needs manual attention: {0}",
            ["mods.autoRestartFailedDiscord"] =
                "🔔 **Game Server** – Mod update restart failed: {0}",

            // Scheduler
            ["scheduler.started"] = "Scheduler started.",
            ["scheduler.stopped"] = "Scheduler stopped.",
            ["scheduler.restartHour"] = "Restart triggered for hour {0}:00.",
            ["scheduler.preAnnounce"] = "Pre-restart announcement ({0} min) for upcoming restart at {1}:00.",
            ["scheduler.scheduledRestart"] = "Scheduled restart triggered (hour {0}:00).",
            ["scheduler.preAnnounceResponse"] = "Pre-restart announcement ({0} min) response: {1}",
            ["scheduler.preAnnounceFailed"] = "Pre-restart announcement ({0} min) failed: {1}",

            // Restart routine
            ["restart.routineStarted"] = "Restart routine started.",
            ["restart.startFile"] = "Start file: {0}",
            ["restart.waiting55"] = "Waiting 55 seconds...",
            ["restart.waiting5"] = "Waiting 5 seconds...",
            ["restart.waiting15"] = "Waiting 15 seconds...",
            ["restart.javaKill"] = "Fallback: Project Zomboid Java still running — forcing KillServerTree.",
            ["restart.cleanShutdown"] = "No Project Zomboid Java left — clean shutdown succeeded.",
            ["restart.startingServer"] = "Starting server: {0}",
            ["restart.serverStarted"] = "Server started (PID {0}).",
            ["restart.error"] = "Error in restart routine: {0}",
            ["restart.inProgress"] = "Restart already in progress.",
            ["restart.cannotSchedule"] = "Cannot schedule: a restart is already in progress.",
            ["restart.skipped"] = "Restart skipped — server already offline.",
            ["restart.startAborted"] = "Server start aborted: start file not found at {0}",
            ["restart.routineFinished"] = "Restart routine finished.",
            ["restart.modUpdateCheckSkipped"] = "Mod update check skipped: {0}",
            ["restart.discordModUpdateNote"] = "Discord mod update note: {0}",
            ["restart.skippedInProgress"] = "Restart skipped (a restart is already in progress).",
            ["restart.waitingCleanShutdown"] = "Waiting 15 seconds for a clean shutdown...",

            ["config.brokenTitle"] = "Settings file damaged",
            ["config.brokenBody"] =
                "Your config.json could not be read and was reset to defaults.\n\nBackup:\n{0}\n\nPlease re-check Settings (paths, RCON, Discord, schedules).",
            ["config.brokenNoBackup"] = "(backup could not be created)",
            ["config.brokenDetail"] = "Details: {0}",
            ["config.brokenLog"] = "WARNING: config.json was damaged — reset to defaults. Backup: {0}",

            ["close.title"] = "Zomboid Manager",
            ["close.heading"] = "Server is still running",
            ["close.body"] =
                "The Project Zomboid server appears to be running. What should happen when you close the manager?",
            ["close.stopAndClose"] = "Stop server & close",
            ["close.leaveRunning"] = "Leave running in background",

            // App self-update
            ["appUpdate.inProgress"] = "An update is already in progress.",
            ["appUpdate.checking"] = "Checking for updates…",
            ["appUpdate.downloading"] = "Updating to {0}…",
            ["appUpdate.downloadingProgress"] = "Updating to {0}… {1}%",
            ["appUpdate.restarting"] = "Restarting into {0}…",
            ["appUpdate.failed"] = "Update failed: {0}",
            ["appUpdate.couldNotOpenLink"] = "Could not open link: {0}",
            ["appUpdate.startupTitle"] = "Update Available",
            ["appUpdate.startupPrompt"] = "A new update is available ({0}). Do you want to update now?",

            // Discord
            ["discord.workshopReadFailed"] = "Could not read Workshop IDs from ini: {0}",
            ["discord.notifyFailed"] = "Discord notify failed: {0}",
            ["discord.notifyError"] = "Discord notify error: {0}",
            ["discord.hourlyPosted"] = "Discord custom hourly event posted ({0}:00).",
            ["discord.testMessage"] = "🔔 Zomboid Manager test message — webhook is working.",

            // Players / whitelist
            ["player.invalidAction"] = "Invalid player action or missing fields.",

            // Log analyzer
            ["logs.noLogLoaded"] = "No log loaded.",
            ["logs.pathOutside"] = "Path is outside configured log folders.",
            ["logs.adminCommandsMissing"] = "admin_commands.json not found.",
            ["logs.adminCommandsInvalid"] = "admin_commands.json has no commands array.",

            // Stats
            ["stats.hardwareFailed"] = "Hardware stats failed: {0}",

            // Manual mod mapping
            ["modMapping.notInList"] =
                "⚠️ Mod ID '{0}' is mapped to Workshop {1} but is not in the configured Mods= list.",

            // Server process manager (forwarded)
            ["process.batLaunched"] = "Server .bat launched in a separate console window.",
            ["process.batFile"] = "File: {0}",
            ["process.noJava"] = "No running Java processes found.",
            ["process.terminatedJava"] = "Terminated Java process (PID {0})",
            ["process.closedConsole"] = "Closed leftover console (PID {0})",
            ["process.embeddedPid"] = "Started embedded process PID {0}",
        }.ToFrozenDictionary();

    private static FrozenDictionary<string, string> BuildDe() =>
        new Dictionary<string, string>
        {
            ["steamcmd.notFound"] = "SteamCMD nicht gefunden: {0}",
            ["steamcmd.alreadyRunning"] = "Ein Server-Update läuft bereits.",
            ["steamcmd.confirm"] =
                "Prüfung und Update auf dem neuesten B42-Stable-Branch. Bei Bedarf wird der Server gestoppt. Fortfahren?",
            ["steamcmd.checking"] = "⏳ Prüfe B42 Stable Update…",
            ["steamcmd.serverPathMissing"] = "⚠️ Server-Pfad nicht konfiguriert.",
            ["steamcmd.stoppingServer"] = "⏳ Server wird für das Update gestoppt…",
            ["steamcmd.running"] = "⏳ SteamCMD Update läuft…",
            ["steamcmd.couldNotStart"] = "⚠️ SteamCMD konnte nicht gestartet werden.",
            ["steamcmd.finished"] = "✅ B42 Stable Update abgeschlossen.",
            ["steamcmd.finishedStartServer"] = " Starte den Server einmal, um die Version im Log zu prüfen.",
            ["steamcmd.finishedVersion"] = " Version: {0}.",
            ["steamcmd.errorCode"] = "⚠️ SteamCMD Fehler (Code {0}).",
            ["steamcmd.failed"] = "⚠️ Update fehlgeschlagen: {0}",
            ["steamcmd.toastUpToDate"] = "Server ist bereits auf dem neuesten Stand.",
            ["steamcmd.toastSuccess"] = "Server erfolgreich aktualisiert!",
            ["steamcmd.statusInProgress"] = "Update läuft…",
            ["steamcmd.statusChecking"] = "Update-Prüfung…",
            ["steamcmd.statusStopping"] = "Server wird gestoppt…",
            ["steamcmd.statusRunning"] = "SteamCMD Update läuft…",
            ["steamcmd.checkUnclear"] = "⚠️ Update-Status unklar — Update wird ausgeführt.",
            ["steamcmd.upToDate"] =
                "✅ B42 Stable ist aktuell (Steam: bereits installiert). Starte den Server einmal, um die Version im Log zu sehen.",
            ["steamcmd.upToDateVersion"] = "✅ B42 Stable ist aktuell (Version {0}).",
            ["steamcmd.updateStarting"] = "⏳ B42 Stable Update wird gestartet (Version {0})…",
            ["steamcmd.updateStartingNoVersion"] = "⏳ B42 Stable Update wird gestartet…",
            ["steamcmd.branchMismatch"] = "Installierter Branch ist beta {0}, Ziel ist {1}.",
            ["steamcmd.branch.b42Stable"] = "B42 Stable",
            ["steamcmd.branch.build41"] = "Build 41",
            ["steamcmd.branch.b42Unstable"] = "B42 Unstable (42.19)",
            ["steamcmd.branch.beta"] = "beta {0}",

            ["settings.saved"] = "Einstellungen gespeichert.",
            ["hours.saved"] = "Stunden gespeichert: {0}",

            ["server.alreadyOnline"] =
                "Server scheint bereits online (Java-Prozess erkannt). Zuerst Stop, dann Start.",
            ["server.starting"] = "Starte: {0}",
            ["server.consoleOpened"] = "Server-Konsole geöffnet (PID {0}).",
            ["server.startFailed"] = "Start fehlgeschlagen: {0}",
            ["server.stoppingRcon"] = "Stoppe per RCON quit...",
            ["server.stopCompleted"] = "Stop abgeschlossen.",
            ["server.startBatMissing"] =
                "Start-Datei nicht gefunden. Server-Ordner + Start-.bat in Einstellungen konfigurieren.",
            ["server.alreadyOnlineShort"] = "Server bereits online. Zuerst Stop, dann Start.",
            ["server.startRequested"] = "Server-Start angefordert — Konsolenfenster sollte sich öffnen.",
            ["server.startedMarkerTimeout"] =
                "Timeout beim Warten auf *** SERVER STARTED *** (20 Min.) — Server-Start möglicherweise fehlgeschlagen.",
            ["server.startedMarkerWatchFailed"] = "Server-Start-Überwachung fehlgeschlagen: {0}",
            ["server.processExited"] = "[Server-Prozess beendet]",
            ["server.noRestartYet"] = "In dieser Session noch kein Restart",

            ["rcon.noResponse"] = "Keine Antwort vom Server.",
            ["rcon.connectionOk"] = "Verbindung erfolgreich",
            ["rcon.noCommand"] = "Kein Befehl eingegeben.",
            ["rcon.emptyResponse"] = "(leere Antwort)",
            ["rcon.noRconResponse"] = "Keine RCON-Antwort.",
            ["rcon.prefix"] = "RCON: {0}",
            ["rcon.response"] = "RCON-Antwort: {0}",

            ["config.noIniLoaded"] =
                "Keine INI geladen. Bitte zuerst im Config-Tab eine server.ini laden.",
            ["config.iniSaved"] = "Konfiguration gespeichert!",
            ["config.error"] = "Fehler: {0}",
            ["config.dialogError"] = "Fehler Dialog: {0}",
            ["config.sandboxSaved"] = "SandboxVars gespeichert.",
            ["config.sandboxNotFound"] =
                "SandboxVars.lua nicht gefunden. Zomboid-Datenpfad setzen und server.ini laden.",
            ["config.profileSaved"] = "Profil '{0}' gespeichert.",
            ["config.profileApplied"] = "Profil '{0}' angewendet.",
            ["config.profileRenamed"] = "Profil umbenannt in '{0}'.",
            ["config.profileDeleted"] = "Profil gelöscht.",
            ["config.noBatFound"] = "Keine .bat-Dateien gefunden — Start-Datei bitte manuell wählen.",
            ["config.startBatAuto"] = "Start-Datei automatisch erkannt: {0}",
            ["config.multipleBat"] = "Mehrere .bat-Dateien gefunden — Start-Datei bitte manuell wählen.",

            ["dialog.sandboxVars"] = "SandboxVars.lua auswählen",
            ["dialog.serverIni"] = "Project Zomboid Server-Konfiguration auswählen",
            ["dialog.serverFolder"] =
                "Project Zomboid Server-Ordner auswählen (Ordner mit StartServer64.bat)",
            ["dialog.zomboidData"] =
                "Zomboid Daten-Ordner auswählen (Ordner mit 'Server' Unterordner, z. B. …\\Zomboid)",
            ["dialog.logFile"] = "Log-Datei öffnen",
            ["dialog.startBat"] = "Server-Start-.bat auswählen",

            ["backup.cancelRequested"] = "Backup-Abbruch angefordert.",
            ["backup.scheduleSaved"] = "Backup-Plan gespeichert.",
            ["backup.alreadyRunning"] = "Ein Backup läuft bereits.",
            ["backup.scheduledTriggered"] = "Geplantes Backup gestartet.",
            ["backup.oneTimeDisabled"] = "Einmal-Backup-Plan nach Ausführung deaktiviert.",
            ["backup.completed"] = "Geplantes Backup abgeschlossen: {0}",
            ["backup.cancelled"] = "Geplantes Backup abgebrochen.",
            ["backup.cancelledMessage"] = "Backup abgebrochen.",
            ["backup.failed"] = "Geplantes Backup fehlgeschlagen: {0}",

            ["broadcast.saved"] = "Broadcast-Nachrichten gespeichert.",
            ["broadcast.invalidSlot"] = "Ungültiger Broadcast-Slot.",
            ["broadcast.emptyMessage"] = "Nachrichtentext ist leer.",
            ["broadcast.serverOffline"] = "Server offline — Nachricht nicht gesendet.",
            ["broadcast.sentNow"] = "Slot {0} jetzt gesendet.",
            ["broadcast.rconFailed"] = "RCON-Senden fehlgeschlagen.",
            ["broadcast.logSendNowCommand"] = "Broadcast-Slot {0} sofort senden: {1}",
            ["broadcast.logSentNowUnchanged"] = "Broadcast-Slot {0} jetzt gesendet (Plan unverändert).",
            ["broadcast.logSendNowFailed"] = "Broadcast-Slot {0} sofort senden fehlgeschlagen: {1}",
            ["broadcast.logSkippedOffline"] = "Broadcast-Slot {0} übersprungen (Server offline).",
            ["broadcast.logFailedRcon"] = "Broadcast-Slot {0} fehlgeschlagen (RCON): {1}",
            ["broadcast.logSent"] = "Broadcast-Slot {0} gesendet.",
            ["broadcast.logFailed"] = "Broadcast-Slot {0} fehlgeschlagen: {1}",

            ["mods.autoRestartSaved"] = "Mod-Update-Auto-Restart Einstellungen gespeichert.",
            ["mods.noWorkshopIds"] =
                "Keine Workshop-IDs konfiguriert (zuerst server.ini im Config-Tab laden).",
            ["mods.noUpdates"] = "Keine Mod-Updates gefunden.",
            ["mods.updated"] = "Mods aktualisiert: {0}",
            ["mods.checkFailed"] = "Mod-Update-Prüfung fehlgeschlagen: {0}",
            ["mods.restartInProgress"] =
                "Mod-Updates gefunden, aber Restart läuft bereits — nur Discord-Benachrichtigung.",
            ["mods.manualCheck"] = "Manuelle Workshop-Mod-Update-Prüfung…",
            ["mods.scheduledCheck"] = "Geplante Workshop-Mod-Update-Prüfung…",
            ["mods.workshopUpdatesDetected"] = "Workshop-Updates erkannt: {0}",
            ["mods.restartAbortedNoBat"] = "Mod-Update-Restart abgebrochen: Start-Datei nicht gefunden.",
            ["mods.restartAbortedInProgress"] = "Mod-Update-Restart abgebrochen: ein anderer Restart läuft bereits.",
            ["mods.pendingRestartCancelled"] = "Ausstehender Mod-Update-Restart abgebrochen.",
            ["mods.noPendingRestart"] = "Kein abbrechbarer Mod-Update-Restart ausstehend.",
            ["mods.autoRestartFailed"] =
                "Mod-Update erkannt, aber automatischer Restart fehlgeschlagen — manuelle Prüfung nötig: {0}",
            ["mods.autoRestartFailedDiscord"] =
                "🔔 **Game Server** – Mod-Update-Restart fehlgeschlagen: {0}",

            ["scheduler.started"] = "Scheduler gestartet.",
            ["scheduler.stopped"] = "Scheduler gestoppt.",
            ["scheduler.restartHour"] = "Restart für Stunde {0}:00 ausgelöst.",
            ["scheduler.preAnnounce"] = "Vorab-Ankündigung ({0} Min.) für Restart um {1}:00.",
            ["scheduler.scheduledRestart"] = "Geplanter Restart ausgelöst (Stunde {0}:00).",
            ["scheduler.preAnnounceResponse"] = "Vorab-Ankündigung ({0} Min.) Antwort: {1}",
            ["scheduler.preAnnounceFailed"] = "Vorab-Ankündigung ({0} Min.) fehlgeschlagen: {1}",

            ["restart.routineStarted"] = "Restart-Routine gestartet.",
            ["restart.startFile"] = "Start-Datei: {0}",
            ["restart.waiting55"] = "Warte 55 Sekunden...",
            ["restart.waiting5"] = "Warte 5 Sekunden...",
            ["restart.waiting15"] = "Warte 15 Sekunden...",
            ["restart.javaKill"] = "Fallback: Project-Zomboid-Java läuft noch — erzwinge KillServerTree.",
            ["restart.cleanShutdown"] = "Kein Project-Zomboid-Java mehr — sauberer Shutdown.",
            ["restart.startingServer"] = "Starte Server: {0}",
            ["restart.serverStarted"] = "Server gestartet (PID {0}).",
            ["restart.error"] = "Fehler in Restart-Routine: {0}",
            ["restart.inProgress"] = "Restart läuft bereits.",
            ["restart.cannotSchedule"] = "Nicht planbar: Restart läuft bereits.",
            ["restart.skipped"] = "Restart übersprungen — Server bereits offline.",
            ["restart.startAborted"] = "Server-Start abgebrochen: Start-Datei nicht gefunden unter {0}",
            ["restart.routineFinished"] = "Restart-Routine abgeschlossen.",
            ["restart.modUpdateCheckSkipped"] = "Mod-Update-Prüfung übersprungen: {0}",
            ["restart.discordModUpdateNote"] = "Discord Mod-Update-Hinweis: {0}",
            ["restart.skippedInProgress"] = "Restart übersprungen (ein Restart läuft bereits).",
            ["restart.waitingCleanShutdown"] = "Warte 15 Sekunden auf sauberen Shutdown...",

            ["config.brokenTitle"] = "Einstellungsdatei beschädigt",
            ["config.brokenBody"] =
                "config.json konnte nicht gelesen werden und wurde auf Standardwerte zurückgesetzt.\n\nBackup:\n{0}\n\nBitte prüfe die Einstellungen (Pfade, RCON, Discord, Zeitpläne).",
            ["config.brokenNoBackup"] = "(Backup konnte nicht erstellt werden)",
            ["config.brokenDetail"] = "Details: {0}",
            ["config.brokenLog"] = "WARNUNG: config.json war beschädigt — auf Standardwerte zurückgesetzt. Backup: {0}",

            ["close.title"] = "Zomboid Manager",
            ["close.heading"] = "Server läuft noch",
            ["close.body"] =
                "Der Project-Zomboid-Server scheint noch zu laufen. Was soll beim Schließen des Managers passieren?",
            ["close.stopAndClose"] = "Server stoppen & schließen",
            ["close.leaveRunning"] = "Im Hintergrund weiterlaufen lassen",

            ["appUpdate.inProgress"] = "Ein Update läuft bereits.",
            ["appUpdate.checking"] = "Suche Updates…",
            ["appUpdate.downloading"] = "Update auf {0}…",
            ["appUpdate.downloadingProgress"] = "Update auf {0}… {1}%",
            ["appUpdate.restarting"] = "Neustart in {0}…",
            ["appUpdate.failed"] = "Update fehlgeschlagen: {0}",
            ["appUpdate.couldNotOpenLink"] = "Link konnte nicht geöffnet werden: {0}",
            ["appUpdate.startupTitle"] = "Update verfügbar",
            ["appUpdate.startupPrompt"] = "Ein neues Update ist verfügbar ({0}). Möchtest du jetzt aktualisieren?",

            ["discord.workshopReadFailed"] = "Workshop-IDs aus INI konnten nicht gelesen werden: {0}",
            ["discord.notifyFailed"] = "Discord-Benachrichtigung fehlgeschlagen: {0}",
            ["discord.notifyError"] = "Discord-Benachrichtigung Fehler: {0}",
            ["discord.hourlyPosted"] = "Discord Stunden-Event gepostet ({0}:00).",
            ["discord.testMessage"] = "🔔 Zomboid Manager Test — Webhook funktioniert.",

            ["player.invalidAction"] = "Ungültige Spieler-Aktion oder fehlende Felder.",

            ["logs.noLogLoaded"] = "Kein Log geladen.",
            ["logs.pathOutside"] = "Pfad liegt außerhalb der konfigurierten Log-Ordner.",
            ["logs.adminCommandsMissing"] = "admin_commands.json nicht gefunden.",
            ["logs.adminCommandsInvalid"] = "admin_commands.json hat keine commands-Array.",

            ["stats.hardwareFailed"] = "Hardware-Statistik fehlgeschlagen: {0}",

            ["modMapping.notInList"] =
                "⚠️ Mod-ID '{0}' ist Workshop {1} zugeordnet, aber nicht in der Mods=-Liste.",

            ["process.batLaunched"] = "Server-.bat in separatem Konsolenfenster gestartet.",
            ["process.batFile"] = "Datei: {0}",
            ["process.noJava"] = "Keine laufenden Java-Prozesse gefunden.",
            ["process.terminatedJava"] = "Java-Prozess beendet (PID {0})",
            ["process.closedConsole"] = "Übriges Konsolenfenster geschlossen (PID {0})",
            ["process.embeddedPid"] = "Eingebetteter Prozess gestartet PID {0}",
        }.ToFrozenDictionary();
}
