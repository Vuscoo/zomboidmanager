# Changelog

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
