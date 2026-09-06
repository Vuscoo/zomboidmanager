# Changelog

## v1.1.0

### New
- **Server update (SteamCMD)** — update the Project Zomboid dedicated server from a new Tools tile; checks Steam first and skips the download when already up to date
- **Steam branch setting** — choose B42 Stable (`public`, default), Build 41 (`legacy41`), or legacy B42 beta (`42.19`) under Settings → Paths
- **SteamCMD path** — configure where SteamCMD lives under Settings → Paths
- **Manual Mod Mapping** — optional mapping when one Workshop item contains several Mod IDs: define a friendly name and group them in the mod list; mod-update notifications (including Discord) use that name and the full Mod ID list instead of a single guessed entry
- **Bilingual UI (English & German only)** — all user-visible text in the web UI and backend logs/toasts follows the language chosen in Settings; French, Spanish, and other partial translations were removed to keep EN/DE complete
- **Startup update check** — after launch, prompts Yes/No when a newer app version is available on GitHub (same update path as Help → Update)
- **Discord message style** — server/restart/mod-update notifications use a simple single-line format (`emoji **Game Server** – …`)

### Improved
- **Update pre-check** — compares against the correct Steam branch (B42 Stable uses `public`, not the old `42.19` beta) so false “update available” prompts are avoided
- **Installed version detection** — reads game version from recent server logs only (ignores stale log lines older than the installed `projectzomboid.jar`)
- **Cleaner update console** — SteamCMD noise is filtered; progress appears as compact percentage lines
- **Update flow** — server is not stopped until an update is actually needed; confirmation dialog before download
- **UI cleanup** — removed unused Network & Access “Server admins” block and redundant tip/hint texts (Config, Settings, Restart Warnings)
- **Mod Update Auto-Restart** — more compact card; warn minutes and restart delay share one value; cancel sends a Discord skip notice
- **Discord restart message** — “Server is restarting” notes that load can take up to 10 minutes
- **Server tab dashboard** — status/players cards, live CPU/memory/disk meters, cleaner console; orange “Server is starting” until the server is fully up

### Fixed
- **SandboxVars.lua** — settings stored as Lua tables (common with mod option blocks) no longer show only `{` in the Config editor; they appear as readable multi-line text and are saved back without corrupting the file
- **Wrong update target** — server update no longer pointed at the `42.19` unstable branch when B42 Stable (`42.20.x`) was intended
- **Mixed-language UI** — hardcoded German backend strings and untranslated UI fragments replaced with centralized localization (`AppLocalizer` + `i18n.js`)
- **Discord “server online”** — sent only after `SERVER STARTED` in the log, not when the process launches
- **Server Online status** — UI (and sidebar) show Online only after `SERVER STARTED`, same as Discord
- **Stop/kill safety** — only Project Zomboid Java processes are terminated (command line / path fingerprint); other Java apps are left alone
- **Close while server running** — confirmation dialog: stop & close, leave running in background, or cancel
- **Broken config.json** — damaged settings are backed up as `config.json.broken.<timestamp>` with a clear warning instead of a silent reset
- **Wait-for-empty warnings** — chat warning no longer promises a fixed “in X minutes” countdown when waiting for an empty server
- **Orphan console cleanup** — only manager-launched or fingerprint-matched StartServer consoles are closed
- **Scheduled restart while offline** — skipped with a clear log line instead of a pointless RCON failure
- **Start/Stop races** — rapid Start/Stop/Restart and mod-update flows no longer overlap; stale process-exit events cannot clear a newer boot watch
- **Warn-minutes `0`** — value `0` is preserved in the UI (no longer forced back to `5`)

## v1.0.9 — Big Update

This release adds four major tools — **Log Analyzer**, **Config Profiles**, **Whitelist Manager**, and a **Statistics** dashboard — and makes the app feel faster and lighter in daily use: quicker backups, smoother Server-tab updates, faster startup, and less background work when you are not looking at a feature.

### Log Analyzer
Browse server logs in a readable table instead of a wall of raw text. Logs are loaded from your configured server `logs` folder and `Zomboid\Logs`.

- Filter by level (All / LOG / WARN / ERROR) and by category
- Search the message text; matching rows are highlighted
- ERROR and WARN rows are tinted so problems stand out
- Click a row to see the surrounding raw lines (including stack traces)
- **System Info** and **Mod Info** panels pull useful header details out of the log
- Right-click a row to copy the full line or just the message
- Large files load in the background with progress and paging so the UI stays responsive
- Closing the analyzer (or switching to another tool) frees the loaded log from memory

### Config Profiles
Save and switch complete server setups from the Config tab — each profile stores the active **server.ini** and matching **SandboxVars.lua**.

- **Save Current as Profile** — name it and keep a full copy under your app data folder
- See when a profile was created and when it was last applied
- **Load Profile** — confirms first, backs up what you have now, applies the profile, reloads the editors, then offers a restart (or apply on the next restart)
- Rename or delete profiles anytime (delete asks for confirmation)

### Whitelist Manager
A dedicated **Whitelist** section in Player Management for closed servers (`Open=false`).

- Lists accounts from the world database: username, access level, SteamID, banned flag
- **Add User** — username + password via RCON (`adduser`)
- **Remove** — with confirmation via RCON
- If the server is open (`Open=true`), an info note explains that the whitelist is not enforced — nothing in Config is changed automatically

### Statistics
A new **Statistics** tab tracks how your server behaved over time (separate from the live hardware meters on the Server tab).

- While the dedicated server is running, the app quietly records player count, CPU, RAM, and free disk space (every 1–5 minutes, default 2)
- Charts for players and for CPU/RAM, with **24h / 7 days / 30 days** ranges
- Restart history with reason: scheduled, manual, mod-update, or unexpected exit
- Uptime % for the selected range, plus planned vs unexpected downtime
- History is kept for a configurable number of days (default 30) and cleaned up automatically
- Stops when you close the app; overhead stays low

### Smoother & faster
Everyday use should feel lighter — especially with the Server tab open, during long sessions, and when making backups.

**Backups**
- Saves are packed into a single **zip** (configs + Multiplayer world) instead of copying endless loose files
- Progress appears in a small corner panel — you can keep using other tabs while a backup runs
- You can **cancel** a running backup; incomplete files are cleaned up
- Manual **Backup Now** and scheduled backups use the same fast path
- In testing, a large synthetic save finished in about **7 seconds** (previously reported as **20+ minutes** with the old copy approach)

**Server tab & live status**
- Hardware meters and server status only poll while the **Server** tab is open
- Coming back to Server refreshes status right away
- Hardware details that rarely change (CPU name, totals, ports) are cached instead of re-read constantly
- Live CPU/RAM updates about every 3 seconds; free disk space less often

**RCON & consoles**
- RCON keeps one shared connection for commands, broadcasts, player actions, and status checks (reconnects automatically if needed)
- Server and RCON consoles keep the last **500** lines so long sessions do not grow without limit

**Background work & startup**
- Restart schedule, backup schedule, chat broadcaster, and Discord hourly messages share one lightweight timer — same schedules as before, less idle overhead
- Settings are written to disk in short batches when many small changes happen quickly; a full **Save settings** and closing the app still write immediately
- The app postpones non-essential background work until the UI is ready, and Tools panels wire themselves the first time you open them — so the main window becomes usable sooner
- CPU usage metering starts when you first open the Server tab, not at launch

## v1.0.8

### New
- **Mod Update Auto-Restart** — optional automatic clean restart when Steam Workshop mods update (warn players, optional wait-until-empty with max wait, save → quit → start)
- Manual controls: Check for Mod Updates Now, Restart Now, Schedule Restart In…, Cancel Pending Restart, with live status on the Restarts tab

### Notes
- Polls Workshop about every 15 minutes while Auto-Restart is enabled (plus on-demand Check Now)
- When Auto-Restart is off, mod updates still only notify Discord (v1.0.7 behavior)
- Settings saved under `ModUpdateAutoRestart` in `config.json`

## v1.0.7

### New
- **Mod update detection** — on restart, checks Steam Workshop `time_updated` for configured Workshop IDs and can notify Discord when mods changed (state stored in `%LocalAppData%\ZomboidManager\mod_update_state.json`)
- **Discord Event templates** — five configurable notification events with enable toggles, editable message templates, placeholders, live preview, and collapsible accordion UI (saved as `DiscordEvents` in `config.json`)
- **Broadcaster Send Now** — manually send any of the five broadcast slots via RCON without changing its schedule

### Improved
- **Admin Command Reference** — fixed loading under WebView2 `file://` (data loaded via the app bridge); expanded Build 42 command dataset from the official wiki
- **Server Broadcaster** — collapsible message slots (summary line when collapsed); send path centralized for easier future chat-command swaps (`servermsg` remains the vanilla mechanism)
- **Discord restart messages** — “Server started after restart (PID …)” renamed to **Server restarted**; PID removed from Discord-facing text (still logged in the app console)

### Discord events (configurable)
- Restart routine started
- Scheduled restart triggered
- Server restarted
- Mods updated (only when Workshop timestamps changed)
- Custom hourly message (hours picker unchanged)

### Placeholders
- Common: `{time}`, `{date}`, `{datetime}`
- Scheduled / custom hourly: `{hour}`
- Server restarted / Mods updated: `{mods}`

### Notes
- Vanilla Project Zomboid has no RCON command for normal global chat lines; the Broadcaster still uses `servermsg` (red admin banner). True chat feed needs a server-side mod.
- Master Discord webhook switch must be on; disabled event toggles send nothing for that event

## v1.0.6

### New
- **Restart Warnings** — custom shutdown message plus optional 10- and 5-minute chat announcements before scheduled restarts (existing 1-minute warning unchanged)
- **Server Broadcaster** — schedule up to 5 one-time or recurring chat messages via RCON
- **Automatic Backups** — weekly or one-time backup scheduling (manual Backup Now unchanged)
- **Log Viewer** — browse important logs from your server `logs` folder and `Zomboid\Logs`
- **Admin Command Reference** — searchable Build 42 admin commands (English/German keywords)
- **Server Hardware panel** — live CPU, RAM, and disk usage, plus configured ports

### Improved
- **Player Management** — search, per-player action menu, godmode/invisible, teleport, give item/XP, ban by SteamID
- **Settings** — smarter startup `.bat` auto-detect and Browse button for the start file
- **Config editor** — “Mods & Workshop” only shows Workshop/Mod IDs; SandboxVars.lua gets its own categories
- **Consoles** — timestamps on Server Control and RCON output; slightly more compact server console
- **App icon** — correct icon in the window title bar and Windows taskbar

### Notes
- Configure server folder + Zomboid data path in Settings so Log Viewer and backups work
- Pre-restart announcements only run while the restart scheduler is active
