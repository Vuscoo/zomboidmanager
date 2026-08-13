(() => {
  let statusIntervalId = null;
  let currentIniPath = "";
  let currentIniEntries = [];
  let currentSandboxPath = "";
  let toastHideTimer = null;
  let toastFadeTimer = null;
  let hasAutoLoadedIni = false;

  function tr(key) {
    return typeof window.t === "function" ? window.t(key) : key;
  }

  function applyUiLanguage(lang) {
    if (window.I18N && typeof window.I18N.setLanguage === "function") {
      window.I18N.setLanguage(lang || "en");
    }
  }

  function requestServerStatus() {
    sendToCSharp("get_server_status");
  }

  function requestSettings() {
    sendToCSharp("get_settings");
  }

  function activateTab(tabName) {
    const items = document.querySelectorAll(".sidebar li");
    const panels = document.querySelectorAll(".tab-content");
    const pageTitle = document.getElementById("page-title");

    items.forEach((item) => {
      item.classList.toggle("active", item.dataset.tab === tabName);
    });

    panels.forEach((panel) => {
      panel.classList.add("hidden");
    });

    const target = document.getElementById(`tab-${tabName}`);
    if (target) {
      target.classList.remove("hidden");
    }

    if (pageTitle) {
      pageTitle.textContent = tr(`title.${tabName}`) || tabName;
    }

    if (tabName === "server") {
      requestServerStatus();
      sendToCSharp("start_hardware_monitor");
    } else {
      sendToCSharp("stop_hardware_monitor");
    }

    if (tabName === "rcon" || tabName === "settings") {
      requestSettings();
    }
  }

  window.switchTab = function switchTab(tabName) {
    activateTab(tabName);
  };

  function setupTabNavigation() {
    const items = document.querySelectorAll(".sidebar li");
    items.forEach((item) => {
      item.addEventListener("click", () => {
        const tabName = item.dataset.tab;
        if (!tabName) {
          return;
        }
        activateTab(tabName);
      });
    });

    activateTab("server");
  }

  function setupStatusPolling() {
    if (statusIntervalId !== null) {
      clearInterval(statusIntervalId);
    }

    statusIntervalId = setInterval(() => {
      requestServerStatus();
    }, 15000);
  }

  function setupRestartControls() {
    document.querySelectorAll("#hours-grid .hour-tile").forEach((tile) => {
      tile.addEventListener("click", () => {
        tile.classList.toggle("selected");
      });
    });

    const selectAll = document.getElementById("hours-select-all");
    const selectNone = document.getElementById("hours-select-none");
    const saveHours = document.getElementById("hours-save");
    const toggleBtn = document.getElementById("scheduler-toggle-btn");

    if (selectAll) {
      selectAll.addEventListener("click", () => {
        document.querySelectorAll("#hours-grid .hour-tile").forEach((tile) => tile.classList.add("selected"));
      });
    }

    if (selectNone) {
      selectNone.addEventListener("click", () => {
        document.querySelectorAll("#hours-grid .hour-tile").forEach((tile) => tile.classList.remove("selected"));
      });
    }

    if (saveHours) {
      saveHours.addEventListener("click", () => {
        const hours = Array.from(document.querySelectorAll("#hours-grid .hour-tile.selected"))
          .map((tile) => Number(tile.dataset.hour))
          .filter((hour) => Number.isInteger(hour));
        sendToCSharp("save_hours", { hours });
      });
    }

    if (toggleBtn) {
      toggleBtn.addEventListener("click", () => {
        sendToCSharp("toggle_scheduler");
      });
    }

    const persistRestartWarnings = () => {
      sendToCSharp("save_restart_warnings", {
        announce10MinBeforeRestart: Boolean(document.getElementById("announce-10-min")?.checked),
        announce5MinBeforeRestart: Boolean(document.getElementById("announce-5-min")?.checked)
      });
    };

    document.getElementById("announce-10-min")?.addEventListener("change", persistRestartWarnings);
    document.getElementById("announce-5-min")?.addEventListener("change", persistRestartWarnings);

    const syncMaxWaitVisibility = () => {
      const wrap = document.getElementById("mod-restart-max-wait-wrap");
      const wait = document.getElementById("mod-restart-wait-empty");
      if (wrap && wait) {
        wrap.classList.toggle("hidden", !wait.checked);
      }
    };
    document.getElementById("mod-restart-wait-empty")?.addEventListener("change", syncMaxWaitVisibility);
    syncMaxWaitVisibility();

    document.getElementById("mod-restart-save")?.addEventListener("click", () => {
      sendToCSharp("save_mod_update_auto_restart", {
        enabled: Boolean(document.getElementById("mod-restart-enabled")?.checked),
        warningMessage: document.getElementById("mod-restart-warning")?.value || "",
        warnMinutesBefore: Number(document.getElementById("mod-restart-warn-minutes")?.value) || 5,
        waitForEmpty: Boolean(document.getElementById("mod-restart-wait-empty")?.checked),
        maxWaitMinutes: Number(document.getElementById("mod-restart-max-wait")?.value) || 60
      });
      showToast(tr("modRestart.saved"), true);
    });

    document.getElementById("mod-restart-check")?.addEventListener("click", () => {
      sendToCSharp("mod_update_check_now");
    });

    document.getElementById("mod-restart-now")?.addEventListener("click", () => {
      if (!confirm(tr("modRestart.confirmNow"))) {
        return;
      }
      sendToCSharp("mod_update_restart_now");
    });

    document.getElementById("mod-restart-schedule")?.addEventListener("click", () => {
      const minutes = Number(document.getElementById("mod-restart-schedule-minutes")?.value) || 5;
      sendToCSharp("mod_update_schedule_restart", { minutes });
    });

    document.getElementById("mod-restart-cancel")?.addEventListener("click", () => {
      sendToCSharp("mod_update_cancel_restart");
    });
  }

  function applyModUpdateAutoRestart(payload) {
    const cfg = payload?.modUpdateAutoRestart || payload;
    if (!cfg) {
      return;
    }
    const enabled = document.getElementById("mod-restart-enabled");
    if (enabled) enabled.checked = Boolean(cfg.enabled);
    const warning = document.getElementById("mod-restart-warning");
    if (warning) {
      warning.value = cfg.warningMessage
        || "A mod has been updated. The server will restart in {minutes} minutes to apply the update.";
    }
    const warnMin = document.getElementById("mod-restart-warn-minutes");
    if (warnMin) warnMin.value = String(Number(cfg.warnMinutesBefore) || 5);
    const waitEmpty = document.getElementById("mod-restart-wait-empty");
    if (waitEmpty) waitEmpty.checked = Boolean(cfg.waitForEmpty);
    const maxWait = document.getElementById("mod-restart-max-wait");
    if (maxWait) maxWait.value = String(Number(cfg.maxWaitMinutes) || 60);
    const wrap = document.getElementById("mod-restart-max-wait-wrap");
    if (wrap && waitEmpty) {
      wrap.classList.toggle("hidden", !waitEmpty.checked);
    }
  }

  function applyModUpdateRestartStatus(payload) {
    const el = document.getElementById("mod-restart-status");
    if (!el) return;
    el.textContent = payload?.label || tr("modRestart.statusIdle");
  }

  function applyModUpdateCheckResult(payload) {
    const el = document.getElementById("mod-restart-check-result");
    const text = payload?.message || "";
    if (el) {
      el.textContent = text;
    }
    if (text) {
      showToast(text, payload?.success !== false);
    }
  }

  function applyRestartWarnings(payload) {
    if (!payload) {
      return;
    }
    const a10 = document.getElementById("announce-10-min");
    if (a10) {
      a10.checked = Boolean(payload.announce10MinBeforeRestart);
    }
    const a5 = document.getElementById("announce-5-min");
    if (a5) {
      a5.checked = Boolean(payload.announce5MinBeforeRestart);
    }
  }

  function formatConsoleStamp() {
    return new Date().toLocaleTimeString("de-DE", {
      hour: "2-digit",
      minute: "2-digit",
      second: "2-digit",
      hour12: false
    });
  }

  function appendConsoleLine(text, className) {
    const output = document.getElementById("rcon-console-output");
    if (!output) {
      return;
    }

    const line = document.createElement("div");
    line.className = `console-line${className ? ` ${className}` : ""}`;
    line.textContent = `[${formatConsoleStamp()}] ${text}`;
    output.appendChild(line);
    output.scrollTop = output.scrollHeight;
  }

  window.sendRconCommand = function sendRconCommand(cmd) {
    const command = String(cmd || "").trim();
    if (!command) {
      return;
    }

    appendConsoleLine(`> ${command}`, "command");
    sendToCSharp("send_rcon_command", { command });
  };

  function setupRconControls() {
    const sendBtn = document.getElementById("rcon-send-btn");
    const input = document.getElementById("rcon-command-input");
    const quickPlayers = document.getElementById("rcon-quick-players");
    const quickSave = document.getElementById("rcon-quick-save");
    const quickMsg = document.getElementById("rcon-quick-msg");
    const quickQuit = document.getElementById("rcon-quick-quit");

    function submitCommandInput() {
      if (!input) {
        return;
      }
      const value = input.value.trim();
      if (!value) {
        return;
      }
      sendRconCommand(value);
      input.value = "";
      input.focus();
    }

    if (sendBtn) {
      sendBtn.addEventListener("click", submitCommandInput);
    }

    if (input) {
      input.addEventListener("keydown", (event) => {
        if (event.key === "Enter") {
          event.preventDefault();
          submitCommandInput();
        }
      });
    }

    if (quickPlayers) {
      quickPlayers.addEventListener("click", () => sendRconCommand("players"));
    }

    if (quickSave) {
      quickSave.addEventListener("click", () => sendRconCommand("save"));
    }

    if (quickMsg) {
      quickMsg.addEventListener("click", () => {
        const text = prompt(tr("rcon.msgPrompt"));
        if (text === null) {
          return;
        }
        const trimmed = text.trim();
        if (!trimmed) {
          return;
        }
        sendRconCommand(`servermsg "${trimmed}"`);
      });
    }

    if (quickQuit) {
      quickQuit.addEventListener("click", () => {
        if (confirm(tr("rcon.confirmQuit"))) {
          sendRconCommand("quit");
        }
      });
    }
  }

  function setupSettingsControls() {
    const saveBtn = document.getElementById("save-all-settings-btn");
    if (!saveBtn) {
      return;
    }

    saveBtn.addEventListener("click", () => {
      const serverPath = document.getElementById("settings-server-path")?.value?.trim() || "";
      const startBat = document.getElementById("settings-start-bat")?.value?.trim() || "";
      const zomboidDataPath = document.getElementById("settings-zomboid-path")?.value?.trim() || "";
      const rconHost = document.getElementById("settings-rcon-host")?.value?.trim() || "";
      const rconPort = Number(document.getElementById("settings-rcon-port")?.value || 0);
      const rconPassword = document.getElementById("settings-rcon-password")?.value || "";
      const uiLanguage = document.getElementById("settings-ui-language")?.value || "en";

      sendToCSharp("save_settings", {
        serverPath,
        startBat,
        zomboidDataPath,
        rconHost,
        rconPort,
        rconPassword,
        uiLanguage
      });
    });
  }

  function fileNameFromPath(path) {
    const parts = String(path || "").split(/[/\\]/);
    return parts[parts.length - 1] || String(path || "");
  }

  function setConfigFileLabel(path) {
    const label = document.getElementById("config-file-label");
    if (label) {
      label.textContent = fileNameFromPath(path);
    }
  }

  function normalizeIniEntry(entry) {
    return {
      key: entry.key ?? entry.Key ?? "",
      displayName: entry.displayName ?? entry.DisplayName ?? entry.key ?? entry.Key ?? "",
      value: entry.value ?? entry.Value ?? "",
      category: entry.category ?? entry.Category ?? "Allgemein",
      inputType: entry.inputType ?? entry.InputType ?? "text",
      description: entry.description ?? entry.Description ?? "",
      order: entry.order ?? entry.Order ?? 100
    };
  }

  function markModified(el) {
    const field = el.closest(".config-field");
    if (field) {
      field.classList.add("modified");
    }
  }

  function createIniInput(entry) {
    const type = String(entry.inputType || "text").toLowerCase();

    if (type === "modlist") {
      const input = document.createElement("textarea");
      input.dataset.key = entry.key;
      input.value = entry.value ?? "";
      input.placeholder = "IDs mit Semikolon trennen: id1;id2;id3";
      input.addEventListener("input", () => markModified(input));
      return input;
    }

    const input = document.createElement("input");
    input.dataset.key = entry.key;

    if (type === "checkbox") {
      input.type = "checkbox";
      input.checked = String(entry.value).toLowerCase() === "true";
      input.addEventListener("change", () => markModified(input));
    } else if (type === "number") {
      input.type = "number";
      input.value = entry.value ?? "";
      input.addEventListener("input", () => markModified(input));
    } else if (type === "password") {
      input.type = "password";
      input.value = entry.value ?? "";
      input.addEventListener("input", () => markModified(input));
    } else {
      input.type = "text";
      input.value = entry.value ?? "";
      input.addEventListener("input", () => markModified(input));
    }

    return input;
  }

  const categoryOpenState = {};

  function renderIniForm(entries, options = {}) {
    const containerId = options.containerId || "config-categories-container";
    const editorCardId = options.editorCardId || "config-editor-card";
    const searchId = options.searchId || "config-search";

    const container = document.getElementById(containerId);
    const editorCard = document.getElementById(editorCardId);
    const sandboxHint = document.getElementById("sandbox-hint");
    if (!container) {
      return;
    }

    container.innerHTML = "";

    const normalized = (entries || []).map(normalizeIniEntry).filter((e) => e.key);
    if (containerId === "config-categories-container" && sandboxHint) {
      sandboxHint.style.display = "block";
    }

    const grouped = new Map();
    normalized.forEach((entry) => {
      const category = entry.category || "Allgemein";
      if (!grouped.has(category)) {
        grouped.set(category, []);
      }
      grouped.get(category).push(entry);
    });

    const preferredOrder = containerId === "sandbox-categories-container"
      ? [
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
        ]
      : [
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

    const categories = Array.from(grouped.keys()).sort((a, b) => {
      const ia = preferredOrder.indexOf(a);
      const ib = preferredOrder.indexOf(b);
      if (ia === -1 && ib === -1) {
        return a.localeCompare(b, "de", { sensitivity: "base" });
      }
      if (ia === -1) return 1;
      if (ib === -1) return -1;
      return ia - ib;
    });

    categories.forEach((categoryName) => {
      const section = document.createElement("div");
      const stateKey = `${containerId}:${categoryName}`;
      const isCollapsed = categoryOpenState[stateKey] === false;
      section.className = `config-category${isCollapsed ? " collapsed" : ""}`;
      section.dataset.category = categoryName;

      const header = document.createElement("button");
      header.type = "button";
      header.className = "config-category-header";
      header.innerHTML = `<span>${categoryName}</span><span class="chevron">${isCollapsed ? "▸" : "▾"}</span>`;
      header.addEventListener("click", () => {
        const collapsed = section.classList.toggle("collapsed");
        categoryOpenState[stateKey] = !collapsed;
        header.querySelector(".chevron").textContent = collapsed ? "▸" : "▾";
      });
      section.appendChild(header);

      const body = document.createElement("div");
      body.className = "config-category-body";

      if (categoryName === "Mods & Workshop") {
        const tip = document.createElement("p");
        tip.className = "muted";
        tip.textContent =
          "Workshop-IDs und Mod-IDs gehören zusammen (Reihenfolge beachten, Trenner: Semikolon). Übersicht auch unter Tools → Mod List.";
        body.appendChild(tip);
      }

      const grid = document.createElement("div");
      grid.className = "config-grid";

      grouped.get(categoryName).forEach((entry) => {
        const field = document.createElement("div");
        field.className = "config-field";
        if (entry.inputType === "modlist") {
          field.style.gridColumn = "1 / -1";
        }

        const label = document.createElement("label");
        label.title = entry.key;
        label.innerHTML = `${entry.displayName || entry.key}<span class="field-key">${entry.key}</span>`;
        field.appendChild(label);
        field.appendChild(createIniInput(entry));
        grid.appendChild(field);
      });

      body.appendChild(grid);
      section.appendChild(body);
      container.appendChild(section);
    });

    if (editorCard && editorCardId === "config-editor-card") {
      editorCard.style.display = "block";
    }

    const search = document.getElementById(searchId);
    if (search) {
      filterConfigFields(search.value, containerId);
    }
  }

  function collectIniValues(containerId = "config-categories-container") {
    const values = {};
    const container = document.getElementById(containerId);
    if (!container) {
      return values;
    }

    container.querySelectorAll("[data-key]").forEach((input) => {
      const key = input.getAttribute("data-key");
      if (!key) {
        return;
      }

      if (input.type === "checkbox") {
        values[key] = input.checked ? "true" : "false";
      } else {
        values[key] = input.value;
      }
    });

    return values;
  }

  function filterConfigFields(query, containerId = "config-categories-container") {
    const needle = String(query || "").trim().toLowerCase();
    const categories = document.querySelectorAll(`#${containerId} .config-category`);

    categories.forEach((category) => {
      let visibleCount = 0;
      category.querySelectorAll(".config-field").forEach((field) => {
        const label = field.querySelector("label");
        const text = (label?.textContent || "").toLowerCase();
        const match = !needle || text.includes(needle);
        field.style.display = match ? "" : "none";
        if (match) {
          visibleCount += 1;
        }
      });

      if (needle && visibleCount > 0) {
        category.classList.remove("collapsed");
        const chevron = category.querySelector(".chevron");
        if (chevron) {
          chevron.textContent = "▾";
        }
      }

      category.style.display = visibleCount > 0 ? "" : "none";
    });
  }

  function clearModifiedMarkers(containerId = "config-categories-container") {
    document
      .querySelectorAll(`#${containerId} .config-field.modified`)
      .forEach((field) => field.classList.remove("modified"));
  }

  function showToast(message, success = true, durationMs = 3000) {
    const toast = document.getElementById("toast");
    if (!toast) {
      return;
    }

    if (toastHideTimer) {
      clearTimeout(toastHideTimer);
      toastHideTimer = null;
    }
    if (toastFadeTimer) {
      clearTimeout(toastFadeTimer);
      toastFadeTimer = null;
    }

    toast.textContent = message || "";
    toast.classList.remove("toast-success", "toast-error");
    toast.classList.add(success ? "toast-success" : "toast-error");
    toast.style.display = "block";
    toast.style.opacity = "0";

    requestAnimationFrame(() => {
      requestAnimationFrame(() => {
        toast.style.opacity = "1";
      });
    });

    toastHideTimer = setTimeout(() => {
      toast.style.opacity = "0";
      toastFadeTimer = setTimeout(() => {
        toast.style.display = "none";
      }, 300);
    }, durationMs);
  }

  function setupConfigControls() {
    const search = document.getElementById("config-search");
    const saveBtn = document.getElementById("save-ini-btn");
    const reloadBtn = document.getElementById("reload-ini-btn");
    const sandboxSearch = document.getElementById("sandbox-search");
    const saveSandboxBtn = document.getElementById("save-sandbox-btn");
    const reloadSandboxBtn = document.getElementById("reload-sandbox-btn");

    if (search) {
      search.addEventListener("input", () => {
        filterConfigFields(search.value, "config-categories-container");
      });
    }

    if (sandboxSearch) {
      sandboxSearch.addEventListener("input", () => {
        filterConfigFields(sandboxSearch.value, "sandbox-categories-container");
      });
    }

    if (saveBtn) {
      saveBtn.addEventListener("click", () => {
        if (!currentIniPath) {
          showToast(tr("config.noIni"), false);
          return;
        }

        const values = collectIniValues("config-categories-container");
        sendToCSharp("save_ini_file", { path: currentIniPath, values });
        clearModifiedMarkers("config-categories-container");
      });
    }

    if (reloadBtn) {
      reloadBtn.addEventListener("click", () => {
        if (!currentIniPath) {
          showToast(tr("config.noIni"), false);
          return;
        }
        sendToCSharp("load_ini_file", { path: currentIniPath });
      });
    }

    if (saveSandboxBtn) {
      saveSandboxBtn.addEventListener("click", () => {
        if (!currentSandboxPath) {
          showToast(tr("sandbox.noFile"), false);
          return;
        }
        const values = collectIniValues("sandbox-categories-container");
        sendToCSharp("save_sandbox", { path: currentSandboxPath, values });
        clearModifiedMarkers("sandbox-categories-container");
      });
    }

    if (reloadSandboxBtn) {
      reloadSandboxBtn.addEventListener("click", () => {
        sendToCSharp("load_sandbox");
      });
    }

    document.getElementById("config-show-all")?.addEventListener("click", () => {
      setCategoryCollapsed("config-categories-container", false);
    });
    document.getElementById("config-hide-all")?.addEventListener("click", () => {
      setCategoryCollapsed("config-categories-container", true);
    });
    document.getElementById("sandbox-show-all")?.addEventListener("click", () => {
      setCategoryCollapsed("sandbox-categories-container", false);
    });
    document.getElementById("sandbox-hide-all")?.addEventListener("click", () => {
      setCategoryCollapsed("sandbox-categories-container", true);
    });

    document.getElementById("config-editor-mode")?.addEventListener("change", (e) => {
      applyConfigEditorMode(e.target.value);
    });

    document.getElementById("btn-clear-console")?.addEventListener("click", () => {
      const output = document.getElementById("server-console-output");
      if (output) {
        output.innerHTML = "";
      }
    });

    document.getElementById("btn-show-players")?.addEventListener("click", () => {
      openPlayersModal();
    });
    document.getElementById("players-modal-close")?.addEventListener("click", () => {
      document.getElementById("players-modal")?.classList.add("hidden");
    });
  }

  function applySelectedHours(hours) {
    const selected = new Set((hours || []).map((h) => Number(h)));
    document.querySelectorAll("#hours-grid .hour-tile").forEach((tile) => {
      const hour = Number(tile.dataset.hour);
      tile.classList.toggle("selected", selected.has(hour));
    });
  }

  function applySchedulerButton(isActive) {
    const toggleBtn = document.getElementById("scheduler-toggle-btn");
    if (!toggleBtn) {
      return;
    }

    toggleBtn.dataset.active = isActive ? "1" : "0";
    toggleBtn.classList.remove("success", "danger");
    if (isActive) {
      toggleBtn.textContent = tr("restarts.stopScheduler");
      toggleBtn.classList.add("btn", "danger");
    } else {
      toggleBtn.textContent = tr("restarts.startScheduler");
      toggleBtn.classList.add("btn", "success");
    }
  }

  function appendServerConsoleLine(text) {
    const output = document.getElementById("server-console-output");
    if (!output) {
      return;
    }
    const line = document.createElement("div");
    line.className = "console-line";
    line.textContent = `[${formatConsoleStamp()}] ${text}`;
    output.appendChild(line);
    output.scrollTop = output.scrollHeight;
  }

  function applyHardwareStats(payload) {
    const hw = payload?.hardware || {};
    const setText = (id, value) => {
      const el = document.getElementById(id);
      if (el) el.textContent = value;
    };
    setText("hw-cpu-name", hw.cpuName || "–");
    setText("hw-ram-total", hw.ramTotalGb != null ? `${hw.ramTotalGb} GB` : "–");
    setText("hw-disk-total", hw.diskTotalGb != null ? `${hw.diskRoot || ""} ${hw.diskTotalGb} GB` : "–");

    const cpuPct = Number(hw.cpuUsage) || 0;
    const ramPct = hw.ramTotalBytes > 0 ? Math.round((hw.ramUsedBytes / hw.ramTotalBytes) * 100) : 0;
    const diskPct = hw.diskTotalBytes > 0 ? Math.round((hw.diskUsedBytes / hw.diskTotalBytes) * 100) : 0;

    setText("hw-cpu-pct", `${cpuPct.toFixed(1)}%`);
    setText("hw-ram-pct", `${hw.ramUsedGb ?? "–"} / ${hw.ramTotalGb ?? "–"} GB (${ramPct}%)`);
    setText("hw-disk-pct", `${hw.diskUsedGb ?? "–"} / ${hw.diskTotalGb ?? "–"} GB (${diskPct}%)`);

    const setBar = (id, pct) => {
      const el = document.getElementById(id);
      if (el) el.style.width = `${Math.max(0, Math.min(100, pct))}%`;
    };
    setBar("hw-cpu-bar", cpuPct);
    setBar("hw-ram-bar", ramPct);
    setBar("hw-disk-bar", diskPct);

    const portsEl = document.getElementById("hw-ports");
    if (portsEl) {
      const ports = payload?.ports || [];
      portsEl.textContent = ports.length
        ? ports.map((p) => `${p.name}: ${p.value}`).join(" · ")
        : "–";
    }
    const adminsEl = document.getElementById("hw-admins");
    if (adminsEl) {
      const admins = payload?.admins || [];
      adminsEl.textContent = admins.length
        ? admins.join(", ")
        : (payload?.adminsNote || "–");
    }
  }

  function hideToolsPanels() {
    ["mod-list-panel", "backup-panel", "broadcast-panel", "players-panel", "discord-panel", "admin-commands-panel", "log-viewer-panel"].forEach((id) => {
      document.getElementById(id)?.classList.add("hidden");
    });
    document.getElementById("tools-grid")?.classList.remove("hidden");
  }

  function showToolsPanel(panelId) {
    switchTab("tools");
    document.getElementById("tools-grid")?.classList.add("hidden");
    ["mod-list-panel", "backup-panel", "broadcast-panel", "players-panel", "discord-panel", "admin-commands-panel", "log-viewer-panel"].forEach((id) => {
      document.getElementById(id)?.classList.add("hidden");
    });
    document.getElementById(panelId)?.classList.remove("hidden");
  }

  function setCategoryCollapsed(containerId, collapsed) {
    document.querySelectorAll(`#${containerId} .config-category`).forEach((section) => {
      section.classList.toggle("collapsed", collapsed);
      const chevron = section.querySelector(".chevron");
      if (chevron) {
        chevron.textContent = collapsed ? "▸" : "▾";
      }
      const name = section.dataset.category || "";
      categoryOpenState[`${containerId}:${name}`] = !collapsed;
    });
  }

  function applyConfigEditorMode(mode) {
    const iniSection = document.getElementById("config-ini-section");
    const sandboxSection = document.getElementById("config-sandbox-section");
    const select = document.getElementById("config-editor-mode");
    if (select && mode) {
      select.value = mode;
    }
    const isSandbox = (select?.value || mode) === "sandbox";
    iniSection?.classList.toggle("hidden", isSandbox);
    sandboxSection?.classList.toggle("hidden", !isSandbox);
    if (isSandbox && !currentSandboxPath) {
      sendToCSharp("load_sandbox");
    }
  }

  function setBackupProgressModal(active, done, file) {
    const modal = document.getElementById("backup-progress-modal");
    if (!modal) {
      return;
    }
    modal.classList.toggle("hidden", !active);
    const count = document.getElementById("backup-progress-count");
    const fileEl = document.getElementById("backup-progress-file");
    if (count) {
      count.textContent = done ? `${done} files…` : "";
    }
    if (fileEl && file) {
      fileEl.textContent = file;
    } else if (fileEl && active) {
      fileEl.textContent = tr("backup.progressWait");
    }
  }

  function prependLog(message) {
    const log = document.getElementById("restart-log");
    if (!log) {
      return;
    }

    const now = new Date();
    const stamp = now.toLocaleTimeString("de-DE", {
      hour: "2-digit",
      minute: "2-digit",
      second: "2-digit",
      hour12: false
    });

    const line = document.createElement("div");
    line.className = "log-line";
    line.textContent = `[${stamp}] ${message}`;
    log.prepend(line);
  }

  function setInputValue(id, value) {
    const el = document.getElementById(id);
    if (el) {
      el.value = value ?? "";
    }
  }

  window.confirmManualRestart = function confirmManualRestart() {
    if (confirm(tr("server.confirmRestart"))) {
      sendToCSharp("manual_restart");
    }
  };

  window.requestStartServer = function requestStartServer() {
    appendServerConsoleLine("> start_server");
    sendToCSharp("start_server");
  };

  window.sendToCSharp = function sendToCSharp(action, data = {}) {
    const payload = JSON.stringify({ action, data });
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage(payload);
      return;
    }
    console.warn("WebView2 bridge unavailable. Message not sent:", payload);
  };

  window.showToast = showToast;

  let modViewMode = "tiles";

  window.openModListTool = function openModListTool() {
    showToolsPanel("mod-list-panel");
    sendToCSharp("get_mod_list");
  };

  window.closeModListTool = function closeModListTool() {
    hideToolsPanels();
  };

  window.openBackupTool = function openBackupTool() {
    showToolsPanel("backup-panel");
    sendToCSharp("list_backups");
    sendToCSharp("get_backup_schedule");
  };

  function applyBackupSchedule(payload) {
    const s = payload?.backupSchedule || payload || {};
    const enabled = document.getElementById("backup-schedule-enabled");
    if (enabled) enabled.checked = Boolean(s.enabled);
    const mode = document.getElementById("backup-schedule-mode");
    if (mode) mode.value = s.mode === "oneTime" ? "oneTime" : "recurring";
    const time = document.getElementById("backup-schedule-time");
    if (time) time.value = s.time || "03:00";
    const oneTime = document.getElementById("backup-schedule-onetime");
    if (oneTime) {
      const raw = s.oneTimeDateTime || "";
      if (raw) {
        const d = new Date(raw);
        if (!Number.isNaN(d.getTime())) {
          const pad = (n) => String(n).padStart(2, "0");
          oneTime.value = `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
        } else {
          oneTime.value = String(raw).slice(0, 16);
        }
      } else {
        oneTime.value = "";
      }
    }
    const days = new Set((s.days || []).map((d) => Number(d)));
    document.querySelectorAll("#backup-days input[data-day]").forEach((cb) => {
      cb.checked = days.has(Number(cb.dataset.day));
    });
    const next = document.getElementById("backup-next-label");
    if (next) next.textContent = s.nextLabel || "";
    updateBackupScheduleModeUi();
  }

  function updateBackupScheduleModeUi() {
    const mode = document.getElementById("backup-schedule-mode")?.value || "recurring";
    document.getElementById("backup-recurring-fields")?.classList.toggle("hidden", mode === "oneTime");
    document.getElementById("backup-onetime-fields")?.classList.toggle("hidden", mode !== "oneTime");
  }

  function setupBackupScheduleControls() {
    document.getElementById("backup-schedule-mode")?.addEventListener("change", updateBackupScheduleModeUi);
    document.getElementById("backup-schedule-save")?.addEventListener("click", () => {
      const days = Array.from(document.querySelectorAll("#backup-days input[data-day]:checked"))
        .map((cb) => Number(cb.dataset.day))
        .filter((d) => Number.isInteger(d));
      sendToCSharp("save_backup_schedule", {
        backupSchedule: {
          enabled: Boolean(document.getElementById("backup-schedule-enabled")?.checked),
          mode: document.getElementById("backup-schedule-mode")?.value || "recurring",
          days,
          time: document.getElementById("backup-schedule-time")?.value || "03:00",
          oneTimeDateTime: document.getElementById("backup-schedule-onetime")?.value || ""
        }
      });
      showToast(tr("backup.scheduleSaved"), true);
    });
  }

  window.closeBackupTool = function closeBackupTool() {
    hideToolsPanels();
  };

  let broadcastSlotsState = [];
  const broadcastExpandedSlots = new Set();

  function emptyBroadcastSlot() {
    return {
      message: "",
      mode: "oneTime",
      scheduledTime: "",
      intervalValue: 1,
      intervalUnit: "hours",
      enabled: false,
      nextLabel: ""
    };
  }

  function ensureBroadcastSlots(list) {
    const slots = Array.isArray(list) ? list.slice(0, 5) : [];
    while (slots.length < 5) {
      slots.push(emptyBroadcastSlot());
    }
    return slots;
  }

  function toDatetimeLocalValue(value) {
    if (!value) {
      return "";
    }
    // Accept ISO or already-local "YYYY-MM-DDTHH:mm"
    const d = new Date(value);
    if (Number.isNaN(d.getTime())) {
      return String(value).slice(0, 16);
    }
    const pad = (n) => String(n).padStart(2, "0");
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
  }

  function summarizeBroadcastMessage(text) {
    const clean = String(text || "").replace(/\s+/g, " ").trim();
    if (!clean) {
      return tr("broadcast.emptyPreview");
    }
    return clean.length > 40 ? `${clean.slice(0, 40)}…` : clean;
  }

  function updateBroadcastSlotSummary(card) {
    if (!card) return;
    const preview = card.querySelector(".broadcast-summary-preview");
    const meta = card.querySelector(".broadcast-summary-meta");
    const message = card.querySelector(".broadcast-message")?.value || "";
    const enabled = Boolean(card.querySelector(".broadcast-enabled")?.checked);
    const nextEl = card.querySelector(".broadcast-next");
    const nextText = (nextEl?.textContent || "").trim();
    if (preview) {
      preview.textContent = summarizeBroadcastMessage(message);
    }
    if (meta) {
      const parts = [enabled ? tr("broadcast.enabledOn") : tr("broadcast.enabledOff")];
      if (enabled && nextText) {
        parts.push(nextText);
      }
      meta.textContent = parts.join(" · ");
    }
  }

  function setBroadcastSlotExpanded(card, expanded) {
    if (!card) return;
    const index = Number(card.dataset.broadcastSlot);
    card.classList.toggle("is-expanded", expanded);
    card.classList.toggle("is-collapsed", !expanded);
    const chevron = card.querySelector(".broadcast-chevron");
    if (chevron) {
      chevron.textContent = expanded ? "▼" : "▶";
    }
    if (Number.isInteger(index)) {
      if (expanded) {
        broadcastExpandedSlots.add(index);
      } else {
        broadcastExpandedSlots.delete(index);
      }
    }
  }

  function collectBroadcastSlotsFromDom() {
    const slots = [];
    for (let i = 0; i < 5; i++) {
      const root = document.querySelector(`[data-broadcast-slot="${i}"]`);
      if (!root) {
        slots.push(emptyBroadcastSlot());
        continue;
      }
      const mode = root.querySelector(".broadcast-mode")?.value || "oneTime";
      slots.push({
        message: root.querySelector(".broadcast-message")?.value || "",
        mode,
        scheduledTime: root.querySelector(".broadcast-scheduled")?.value || "",
        intervalValue: Number(root.querySelector(".broadcast-interval")?.value) || 1,
        intervalUnit: root.querySelector(".broadcast-unit")?.value || "hours",
        enabled: Boolean(root.querySelector(".broadcast-enabled")?.checked)
      });
    }
    return slots;
  }

  function renderBroadcastSlots(payload) {
    const container = document.getElementById("broadcast-slots");
    if (!container) {
      return;
    }

    broadcastSlotsState = ensureBroadcastSlots(payload?.broadcastMessages || payload || []);
    container.innerHTML = "";

    broadcastSlotsState.forEach((slot, index) => {
      const card = document.createElement("div");
      card.className = "broadcast-slot is-collapsed";
      card.dataset.broadcastSlot = String(index);
      const isRecurring = slot.mode === "recurring";
      const msg = document.createElement("textarea");
      msg.className = "broadcast-message";
      msg.rows = 2;
      msg.value = slot.message || "";

      card.innerHTML = `
        <button type="button" class="broadcast-slot-summary" aria-expanded="false">
          <span class="broadcast-chevron" aria-hidden="true">▶</span>
          <span class="broadcast-summary-main">
            <strong>${tr("broadcast.slot")} ${index + 1}</strong>
            <span class="broadcast-summary-preview muted"></span>
          </span>
          <span class="broadcast-summary-meta muted"></span>
        </button>
        <div class="broadcast-slot-body">
          <div class="broadcast-slot-header">
            <label class="checkbox-row">
              <input class="broadcast-enabled" type="checkbox" ${slot.enabled ? "checked" : ""} />
              <span>${tr("broadcast.enabled")}</span>
            </label>
            <button class="btn secondary btn-sm broadcast-send-now" type="button">${tr("broadcast.sendNow")}</button>
          </div>
          <label class="form-field form-field-full">
            <span>${tr("broadcast.message")}</span>
            <div class="broadcast-message-mount"></div>
          </label>
          <div class="form-grid">
            <label class="form-field">
              <span>${tr("broadcast.mode")}</span>
              <select class="broadcast-mode">
                <option value="oneTime"${!isRecurring ? " selected" : ""}>${tr("broadcast.oneTime")}</option>
                <option value="recurring"${isRecurring ? " selected" : ""}>${tr("broadcast.recurring")}</option>
              </select>
            </label>
            <label class="form-field broadcast-onetime-fields${isRecurring ? " hidden" : ""}">
              <span>${tr("broadcast.scheduledTime")}</span>
              <input class="broadcast-scheduled" type="datetime-local" value="${toDatetimeLocalValue(slot.scheduledTime || slot.nextSendAt)}" />
            </label>
            <div class="broadcast-recurring-fields form-field${isRecurring ? "" : " hidden"}" style="display:flex;gap:8px;align-items:flex-end;">
              <label class="form-field" style="flex:1;margin:0">
                <span>${tr("broadcast.every")}</span>
                <input class="broadcast-interval" type="number" min="1" value="${Number(slot.intervalValue) || 1}" />
              </label>
              <label class="form-field" style="flex:1;margin:0">
                <span>${tr("broadcast.unit")}</span>
                <select class="broadcast-unit">
                  <option value="minutes"${slot.intervalUnit === "minutes" ? " selected" : ""}>${tr("broadcast.minutes")}</option>
                  <option value="hours"${slot.intervalUnit !== "minutes" ? " selected" : ""}>${tr("broadcast.hours")}</option>
                </select>
              </label>
            </div>
          </div>
          <div class="muted broadcast-next"></div>
        </div>
      `;
      card.querySelector(".broadcast-message-mount")?.replaceWith(msg);
      const nextEl = card.querySelector(".broadcast-next");
      if (nextEl && slot.enabled && slot.nextLabel) {
        nextEl.textContent = slot.nextLabel;
      }

      const modeSelect = card.querySelector(".broadcast-mode");
      const oneTime = card.querySelector(".broadcast-onetime-fields");
      const recurring = card.querySelector(".broadcast-recurring-fields");
      modeSelect?.addEventListener("change", () => {
        const recurringMode = modeSelect.value === "recurring";
        oneTime?.classList.toggle("hidden", recurringMode);
        recurring?.classList.toggle("hidden", !recurringMode);
      });

      msg.addEventListener("input", () => updateBroadcastSlotSummary(card));
      card.querySelector(".broadcast-enabled")?.addEventListener("change", () => updateBroadcastSlotSummary(card));

      const summaryBtn = card.querySelector(".broadcast-slot-summary");
      summaryBtn?.addEventListener("click", () => {
        const expand = !card.classList.contains("is-expanded");
        setBroadcastSlotExpanded(card, expand);
        summaryBtn.setAttribute("aria-expanded", expand ? "true" : "false");
      });

      card.querySelector(".broadcast-send-now")?.addEventListener("click", (event) => {
        event.preventDefault();
        event.stopPropagation();
        const messageText = (card.querySelector(".broadcast-message")?.value || "").trim();
        if (!messageText) {
          showToast(tr("broadcast.sendNowEmpty"), false);
          return;
        }
        sendToCSharp("send_broadcast_now", { index, message: messageText });
      });

      updateBroadcastSlotSummary(card);
      setBroadcastSlotExpanded(card, broadcastExpandedSlots.has(index));
      if (summaryBtn) {
        summaryBtn.setAttribute("aria-expanded", broadcastExpandedSlots.has(index) ? "true" : "false");
      }

      container.appendChild(card);
    });
  }

  function setupBroadcastControls() {
    document.getElementById("broadcast-save-btn")?.addEventListener("click", () => {
      const broadcastMessages = collectBroadcastSlotsFromDom();
      sendToCSharp("save_broadcast_messages", { broadcastMessages });
      showToast(tr("broadcast.saved"), true);
    });
  }

  window.openBroadcastTool = function openBroadcastTool() {
    showToolsPanel("broadcast-panel");
    sendToCSharp("get_broadcast_messages");
  };

  window.closeBroadcastTool = function closeBroadcastTool() {
    hideToolsPanels();
  };

  window.openPlayersTool = function openPlayersTool() {
    showToolsPanel("players-panel");
    sendToCSharp("get_player_list");
  };

  window.closePlayersTool = function closePlayersTool() {
    hideToolsPanels();
  };

  window.openDiscordTool = function openDiscordTool() {
    showToolsPanel("discord-panel");
    requestSettings();
  };

  window.closeDiscordTool = function closeDiscordTool() {
    hideToolsPanels();
  };

  let adminCommandsData = [];
  let adminCommandsLoading = false;

  function showAdminCommandsLoadFailed(detail) {
    const list = document.getElementById("admin-cmd-list");
    if (list) {
      list.innerHTML = `<p class="muted">${tr("adminCmd.loadFailed")}${detail ? ` (${escapeHtml(detail)})` : ""}</p>`;
    }
  }

  function loadAdminCommands() {
    if (adminCommandsData.length) {
      renderAdminCommands();
      return;
    }
    if (adminCommandsLoading) return;
    adminCommandsLoading = true;
    const list = document.getElementById("admin-cmd-list");
    if (list && !list.children.length) {
      list.innerHTML = `<p class="muted">${tr("adminCmd.loading") || "Loading…"}</p>`;
    }
    // file:// fetch is blocked in WebView2 — load via the C# bridge instead.
    sendToCSharp("get_admin_commands");
  }

  function applyAdminCommandsData(payload) {
    adminCommandsLoading = false;
    if (!payload || payload.success === false) {
      showAdminCommandsLoadFailed(payload?.message || "");
      return;
    }
    try {
      const json = typeof payload.json === "string"
        ? JSON.parse(payload.json)
        : payload.data;
      adminCommandsData = (json && json.commands) || [];
      if (!adminCommandsData.length) {
        showAdminCommandsLoadFailed("empty commands list");
        return;
      }
      renderAdminCommands();
    } catch (err) {
      showAdminCommandsLoadFailed(err?.message || "parse error");
    }
  }

  async function copyAdminCommandText(text) {
    try {
      if (navigator.clipboard && navigator.clipboard.writeText) {
        await navigator.clipboard.writeText(text);
        return true;
      }
    } catch {
      // fall through to textarea fallback (common under file://)
    }
    try {
      const ta = document.createElement("textarea");
      ta.value = text;
      ta.setAttribute("readonly", "");
      ta.style.position = "fixed";
      ta.style.left = "-9999px";
      document.body.appendChild(ta);
      ta.select();
      const ok = document.execCommand("copy");
      document.body.removeChild(ta);
      return ok;
    } catch {
      return false;
    }
  }

  function renderAdminCommands() {
    const list = document.getElementById("admin-cmd-list");
    const countEl = document.getElementById("admin-cmd-count");
    if (!list) return;

    const q = (document.getElementById("admin-cmd-search")?.value || "").trim().toLowerCase();
    const cat = document.getElementById("admin-cmd-category")?.value || "All";

    const filtered = adminCommandsData.filter((cmd) => {
      if (cat !== "All" && cmd.category !== cat) return false;
      if (!q) return true;
      const hay = [
        cmd.name,
        cmd.description,
        cmd.syntax,
        cmd.category,
        ...(cmd.keywords || [])
      ].join(" ").toLowerCase();
      return hay.includes(q);
    });

    if (countEl) {
      countEl.textContent = `${filtered.length} / ${adminCommandsData.length} ${tr("adminCmd.commands")}`;
    }

    list.innerHTML = "";
    filtered.forEach((cmd) => {
      const card = document.createElement("div");
      card.className = "admin-cmd-card";
      card.innerHTML = `
        <div class="admin-cmd-header">
          <strong>/${escapeHtml(cmd.name)}</strong>
          <span class="admin-cmd-tag">${escapeHtml(cmd.category)}</span>
        </div>
        <p class="muted" style="margin:8px 0">${escapeHtml(cmd.description || "")}</p>
        <div class="admin-cmd-syntax">
          <code>${escapeHtml(cmd.syntax || "")}</code>
          <button class="btn secondary btn-sm admin-cmd-copy" type="button">${tr("adminCmd.copy")}</button>
        </div>
        ${cmd.uncertain ? `<div class="muted" style="margin-top:6px">${tr("adminCmd.uncertain")}</div>` : ""}
      `;
      card.querySelector(".admin-cmd-copy")?.addEventListener("click", async () => {
        const ok = await copyAdminCommandText(cmd.syntax || cmd.name);
        showToast(ok ? tr("adminCmd.copied") : tr("adminCmd.copyFailed"), ok);
      });
      list.appendChild(card);
    });

    if (!filtered.length) {
      list.innerHTML = `<p class="muted">${tr("adminCmd.none")}</p>`;
    }
  }

  function setupAdminCommandsControls() {
    document.getElementById("admin-cmd-search")?.addEventListener("input", renderAdminCommands);
    document.getElementById("admin-cmd-category")?.addEventListener("change", renderAdminCommands);
  }

  window.openAdminCommandsTool = function openAdminCommandsTool() {
    showToolsPanel("admin-commands-panel");
    loadAdminCommands();
  };

  window.closeAdminCommandsTool = function closeAdminCommandsTool() {
    hideToolsPanels();
  };

  window.runAdminCommandsSelfTest = function runAdminCommandsSelfTest() {
    return new Promise((resolve) => {
      const started = Date.now();
      const finish = (result) => resolve(result);
      adminCommandsData = [];
      adminCommandsLoading = false;

      const prev = window.handleCSharpMessage;
      const timer = setTimeout(() => {
        window.handleCSharpMessage = prev;
        finish({ ok: false, error: "timeout waiting for admin_commands_data" });
      }, 10000);

      window.handleCSharpMessage = function (message) {
        prev(message);
        if (message?.type !== "admin_commands_data") return;
        clearTimeout(timer);
        window.handleCSharpMessage = prev;
        setTimeout(async () => {
          try {
            if (!adminCommandsData.length) {
              finish({
                ok: false,
                error: "no commands loaded",
                message: message.payload?.message || ""
              });
              return;
            }

            const match = (q, cat) => {
              const query = (q || "").trim().toLowerCase();
              return adminCommandsData.filter((cmd) => {
                if (cat !== "All" && cmd.category !== cat) return false;
                if (!query) return true;
                const hay = [
                  cmd.name,
                  cmd.description,
                  cmd.syntax,
                  cmd.category,
                  ...(cmd.keywords || [])
                ].join(" ").toLowerCase();
                return hay.includes(query);
              }).length;
            };

            openAdminCommandsTool();
            const search = document.getElementById("admin-cmd-search");
            const category = document.getElementById("admin-cmd-category");
            if (search) {
              search.value = "regen";
              search.dispatchEvent(new Event("input"));
            }
            const deRegenVisible = document.querySelectorAll(".admin-cmd-card").length;

            if (search) {
              search.value = "kick";
              search.dispatchEvent(new Event("input"));
            }
            const enKickVisible = document.querySelectorAll(".admin-cmd-card").length;

            if (category && search) {
              search.value = "";
              category.value = "World & Weather";
              category.dispatchEvent(new Event("change"));
            }
            const weatherVisible = document.querySelectorAll(".admin-cmd-card").length;

            const copyOk = await copyAdminCommandText("players");
            const deRegen = match("regen", "All");
            const enKick = match("kick", "All");
            const weather = match("", "World & Weather");

            finish({
              ok: adminCommandsData.length > 0
                && deRegen > 0
                && enKick > 0
                && weather > 0
                && deRegenVisible > 0
                && enKickVisible > 0
                && weatherVisible > 0
                && copyOk,
              total: adminCommandsData.length,
              deRegen,
              enKick,
              weather,
              deRegenVisible,
              enKickVisible,
              weatherVisible,
              copyOk,
              ms: Date.now() - started
            });
          } catch (err) {
            finish({ ok: false, error: String(err && err.message ? err.message : err) });
          }
        }, 50);
      };

      sendToCSharp("get_admin_commands");
    });
  };

  function renderLogFileList(payload) {
    const list = document.getElementById("log-file-list");
    const paths = document.getElementById("log-viewer-paths");
    if (paths) {
      const z = payload?.zomboidLogsPath || "–";
      const s = payload?.serverLogsPath || "–";
      paths.textContent = `${tr("logViewer.zomboidPath")}: ${z}  |  ${tr("logViewer.serverPath")}: ${s}`;
    }
    if (!list) return;
    const files = payload?.files || [];
    if (!files.length) {
      list.innerHTML = `<p class="muted">${tr("logViewer.empty")}</p>`;
      return;
    }
    list.innerHTML = "";
    files.forEach((f) => {
      const row = document.createElement("button");
      row.type = "button";
      row.className = "log-file-item";
      row.innerHTML = `
        <strong>${escapeHtml(f.name || "")}</strong>
        <div class="muted">${escapeHtml(f.source || "")} · ${escapeHtml(f.sizeLabel || "")}</div>
        <div class="muted">${escapeHtml(f.lastWrite || "")}</div>`;
      row.addEventListener("click", () => {
        list.querySelectorAll(".log-file-item").forEach((el) => el.classList.remove("active"));
        row.classList.add("active");
        sendToCSharp("read_log", { path: f.path });
      });
      list.appendChild(row);
    });
  }

  function applyLogContent(payload) {
    const view = document.getElementById("log-content-view");
    const meta = document.getElementById("log-content-meta");
    if (!view) return;
    if (!payload?.success) {
      view.textContent = payload?.message || tr("logViewer.readFailed");
      if (meta) meta.textContent = "";
      return;
    }
    view.textContent = payload.content || "";
    if (meta) {
      meta.textContent = payload.truncated
        ? `${payload.path || ""} (${tr("logViewer.truncated")})`
        : (payload.path || "");
    }
    view.scrollTop = 0;
  }

  window.openLogViewerTool = function openLogViewerTool() {
    showToolsPanel("log-viewer-panel");
    sendToCSharp("list_logs");
  };

  window.closeLogViewerTool = function closeLogViewerTool() {
    hideToolsPanels();
  };

  function openPlayersModal() {
    const modal = document.getElementById("players-modal");
    modal?.classList.remove("hidden");
    sendToCSharp("get_player_list");
  }

  let cachedPlayers = [];

  function escapeHtml(text) {
    return String(text ?? "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  function renderPlayerRows(containerId, players, selectable) {
    const container = document.getElementById(containerId);
    if (!container) {
      return;
    }
    const filter = (document.getElementById("pm-search")?.value || "").trim().toLowerCase();
    const list = (players || []).filter((p) => {
      if (!filter) return true;
      const name = String(p.name || "").toLowerCase();
      const steam = String(p.steamId || "").toLowerCase();
      return name.includes(filter) || steam.includes(filter);
    });
    if (!list.length) {
      container.innerHTML = `<p class="muted">${tr("players.empty")}</p>`;
      return;
    }
    container.innerHTML = "";
    list.forEach((p) => {
      const row = document.createElement("div");
      row.className = "player-row";
      const status = p.status || "online";
      row.innerHTML = `
        <div class="player-row-main">
          <strong>${escapeHtml(p.name || "–")}</strong>
          <div class="muted">${p.steamId ? `Steam: ${escapeHtml(p.steamId)}` : escapeHtml(p.raw || "")}</div>
          <div class="muted">${tr("players.status")}: ${escapeHtml(status)}</div>
        </div>
        ${selectable ? `<select class="player-action-menu btn-sm" data-player="${escapeHtml(p.name || "")}" data-steamid="${escapeHtml(p.steamId || "")}">
          <option value="">${tr("players.actionsMenu")}</option>
          <option value="kick">${tr("players.kick")}</option>
          <option value="ban">${tr("players.ban")}</option>
          <option value="banid">${tr("players.banId")}</option>
          <option value="setaccess">${tr("players.setAccess")}</option>
          <option value="god_on">${tr("players.godOn")}</option>
          <option value="god_off">${tr("players.godOff")}</option>
          <option value="inv_on">${tr("players.invOn")}</option>
          <option value="inv_off">${tr("players.invOff")}</option>
          <option value="teleport_to">${tr("players.teleport")}</option>
          <option value="additem">${tr("players.addItem")}</option>
          <option value="addxp">${tr("players.addXp")}</option>
        </select>` : ""}`;
      if (selectable) {
        row.querySelector(".player-row-main")?.addEventListener("click", () => {
          const input = document.getElementById("pm-player");
          if (input) input.value = p.name || "";
        });
        row.querySelector(".player-action-menu")?.addEventListener("change", (ev) => {
          const select = ev.target;
          const action = select.value;
          select.value = "";
          if (!action) return;
          runPlayerMenuAction(action, p);
        });
      }
      container.appendChild(row);
    });
  }

  function runPlayerMenuAction(action, player) {
    const name = player?.name || "";
    const steamId = player?.steamId || "";
    const input = document.getElementById("pm-player");
    if (input) input.value = name;

    const send = (act, extra = {}) => {
      sendToCSharp("player_action", {
        action: act,
        player: name,
        steamId,
        reason: document.getElementById("pm-reason")?.value?.trim() || "",
        level: document.getElementById("pm-level")?.value || "",
        targetPlayer: document.getElementById("pm-target")?.value?.trim() || "",
        itemId: document.getElementById("pm-item")?.value?.trim() || "",
        quantity: document.getElementById("pm-qty")?.value || "1",
        skill: document.getElementById("pm-skill")?.value || "",
        xpAmount: document.getElementById("pm-xp")?.value || "",
        ...extra
      });
    };

    if (action === "kick") {
      if (confirm(tr("players.confirmKick"))) send("kick");
      return;
    }
    if (action === "ban") {
      if (confirm(tr("players.confirmBan"))) send("ban");
      return;
    }
    if (action === "banid") {
      if (!steamId) {
        showToast(tr("players.noSteamId"), false);
        return;
      }
      if (confirm(tr("players.confirmBan"))) send("banid");
      return;
    }
    if (action === "setaccess") {
      send("setaccess");
      return;
    }
    if (action === "god_on") {
      send("godmode", { flag: "-true" });
      return;
    }
    if (action === "god_off") {
      send("godmode", { flag: "-false" });
      return;
    }
    if (action === "inv_on") {
      send("invisible", { flag: "-true" });
      return;
    }
    if (action === "inv_off") {
      send("invisible", { flag: "-false" });
      return;
    }
    if (action === "teleport_to") {
      send("teleport_to");
      return;
    }
    if (action === "additem") {
      send("additem");
      return;
    }
    if (action === "addxp") {
      send("addxp");
    }
  }

  function applyDiscordSettings(payload) {
    if (!payload) {
      return;
    }
    setInputValue("discord-webhook-url", payload.discordWebhookUrl || "");
    const enabled = document.getElementById("discord-notify-enabled");
    if (enabled) {
      enabled.checked = Boolean(payload.discordNotifyEnabled);
    }
    const selected = new Set((payload.discordCustomHours || []).map((h) => Number(h)));
    document.querySelectorAll("#discord-hours-grid .hour-tile").forEach((tile) => {
      tile.classList.toggle("selected", selected.has(Number(tile.dataset.hour)));
    });
    renderDiscordEventSlots(payload.discordEvents || []);
  }

  const discordExpandedEvents = new Set();

  function discordPreviewSample(eventKey) {
    const now = new Date();
    const pad = (n) => String(n).padStart(2, "0");
    const time = `${pad(now.getHours())}:${pad(now.getMinutes())}`;
    const date = `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
    const hour = pad(now.getHours());
    const modsSample = eventKey === "mods_updated" || eventKey === "server_restarted"
      ? "Example Mod A, Example Mod B"
      : "";
    return {
      time,
      date,
      datetime: now.toLocaleString(),
      hour,
      mods: modsSample
    };
  }

  function applyDiscordPlaceholders(template, values) {
    let result = String(template || "");
    Object.keys(values).forEach((key) => {
      result = result.replaceAll(new RegExp(`\\{${key}\\}`, "gi"), values[key] ?? "");
    });
    return result.trim();
  }

  function summarizeDiscordTemplate(text) {
    const clean = String(text || "").replace(/\s+/g, " ").trim();
    if (!clean) return tr("discord.emptyPreview");
    return clean.length > 48 ? `${clean.slice(0, 48)}…` : clean;
  }

  function updateDiscordEventSummary(card) {
    if (!card) return;
    const preview = card.querySelector(".discord-summary-preview");
    const meta = card.querySelector(".discord-summary-meta");
    const live = card.querySelector(".discord-live-preview");
    const template = card.querySelector(".discord-event-template")?.value || "";
    const enabled = Boolean(card.querySelector(".discord-event-enabled")?.checked);
    const eventKey = card.dataset.eventKey || "";
    const rendered = applyDiscordPlaceholders(template, discordPreviewSample(eventKey));
    if (preview) preview.textContent = summarizeDiscordTemplate(rendered || template);
    if (meta) meta.textContent = enabled ? tr("discord.enabledOn") : tr("discord.enabledOff");
    if (live) live.textContent = rendered || tr("discord.emptyPreview");
  }

  function setDiscordEventExpanded(card, expanded) {
    if (!card) return;
    const key = card.dataset.eventKey || "";
    card.classList.toggle("is-expanded", expanded);
    card.classList.toggle("is-collapsed", !expanded);
    const chevron = card.querySelector(".broadcast-chevron");
    if (chevron) chevron.textContent = expanded ? "▼" : "▶";
    if (key) {
      if (expanded) discordExpandedEvents.add(key);
      else discordExpandedEvents.delete(key);
    }
  }

  function collectDiscordEventsFromDom() {
    return Array.from(document.querySelectorAll("#discord-events [data-event-key]")).map((root) => ({
      eventKey: root.dataset.eventKey || "",
      enabled: Boolean(root.querySelector(".discord-event-enabled")?.checked),
      template: root.querySelector(".discord-event-template")?.value || ""
    }));
  }

  function renderDiscordEventSlots(events) {
    const container = document.getElementById("discord-events");
    if (!container) return;
    const list = Array.isArray(events) ? events : [];
    container.innerHTML = "";

    list.forEach((evt) => {
      const card = document.createElement("div");
      card.className = "broadcast-slot discord-event-slot is-collapsed";
      card.dataset.eventKey = evt.eventKey || "";
      const title = evt.displayName || evt.eventKey || "Event";
      const help = evt.placeholderHelp || (evt.placeholders || []).map((p) => `{${p}}`).join(", ");

      const textarea = document.createElement("textarea");
      textarea.className = "discord-event-template";
      textarea.rows = 3;
      textarea.value = evt.template || evt.defaultTemplate || "";

      card.innerHTML = `
        <button type="button" class="broadcast-slot-summary" aria-expanded="false">
          <span class="broadcast-chevron" aria-hidden="true">▶</span>
          <span class="broadcast-summary-main">
            <strong>${escapeHtml(title)}</strong>
            <span class="discord-summary-preview muted"></span>
          </span>
          <span class="discord-summary-meta muted"></span>
        </button>
        <div class="broadcast-slot-body">
          <div class="broadcast-slot-header">
            <label class="checkbox-row">
              <input class="discord-event-enabled" type="checkbox" ${evt.enabled ? "checked" : ""} />
              <span>${tr("discord.eventEnabled")}</span>
            </label>
            <button class="btn secondary btn-sm discord-reset-template" type="button">${tr("discord.resetTemplate")}</button>
          </div>
          <label class="form-field form-field-full">
            <span>${tr("discord.template")}</span>
            <div class="discord-template-mount"></div>
          </label>
          <p class="muted discord-placeholder-help">${tr("discord.placeholders")}: ${escapeHtml(help)}</p>
          <div class="discord-preview-box">
            <div class="muted">${tr("discord.livePreview")}</div>
            <div class="discord-live-preview"></div>
          </div>
        </div>
      `;

      card.querySelector(".discord-template-mount")?.replaceWith(textarea);
      textarea.addEventListener("input", () => updateDiscordEventSummary(card));
      card.querySelector(".discord-event-enabled")?.addEventListener("change", () => updateDiscordEventSummary(card));
      card.querySelector(".discord-reset-template")?.addEventListener("click", (event) => {
        event.preventDefault();
        event.stopPropagation();
        textarea.value = evt.defaultTemplate || "";
        updateDiscordEventSummary(card);
      });

      const summaryBtn = card.querySelector(".broadcast-slot-summary");
      summaryBtn?.addEventListener("click", () => {
        const expand = !card.classList.contains("is-expanded");
        setDiscordEventExpanded(card, expand);
        summaryBtn.setAttribute("aria-expanded", expand ? "true" : "false");
      });

      updateDiscordEventSummary(card);
      const shouldExpand = discordExpandedEvents.has(evt.eventKey);
      setDiscordEventExpanded(card, shouldExpand);
      if (summaryBtn) summaryBtn.setAttribute("aria-expanded", shouldExpand ? "true" : "false");
      container.appendChild(card);
    });
  }

  function setupPlayersAndDiscordControls() {
    document.querySelectorAll("#discord-hours-grid .hour-tile").forEach((tile) => {
      tile.addEventListener("click", () => tile.classList.toggle("selected"));
    });

    const sendAction = (action, extra = {}) => {
      const player = document.getElementById("pm-player")?.value?.trim() || "";
      const reason = document.getElementById("pm-reason")?.value?.trim() || "";
      const level = document.getElementById("pm-level")?.value || "";
      const password = document.getElementById("pm-password")?.value?.trim() || "";
      const steamId = (cachedPlayers.find((p) => p.name === player)?.steamId) || "";
      sendToCSharp("player_action", {
        action,
        player,
        reason,
        level,
        password,
        steamId,
        targetPlayer: document.getElementById("pm-target")?.value?.trim() || "",
        itemId: document.getElementById("pm-item")?.value?.trim() || "",
        quantity: document.getElementById("pm-qty")?.value || "1",
        skill: document.getElementById("pm-skill")?.value || "",
        xpAmount: document.getElementById("pm-xp")?.value || "",
        ...extra
      });
    };

    document.getElementById("pm-search")?.addEventListener("input", () => {
      renderPlayerRows("players-list", cachedPlayers, true);
    });

    document.getElementById("pm-kick")?.addEventListener("click", () => {
      if (confirm(tr("players.confirmKick"))) {
        sendAction("kick");
      }
    });
    document.getElementById("pm-ban")?.addEventListener("click", () => {
      if (confirm(tr("players.confirmBan"))) {
        sendAction("ban");
      }
    });
    document.getElementById("pm-banid")?.addEventListener("click", () => {
      if (confirm(tr("players.confirmBan"))) sendAction("banid");
    });
    document.getElementById("pm-unban")?.addEventListener("click", () => sendAction("unban"));
    document.getElementById("pm-unbanid")?.addEventListener("click", () => sendAction("unbanid"));
    document.getElementById("pm-access")?.addEventListener("click", () => sendAction("setaccess"));
    document.getElementById("pm-god-on")?.addEventListener("click", () => sendAction("godmode", { flag: "-true" }));
    document.getElementById("pm-god-off")?.addEventListener("click", () => sendAction("godmode", { flag: "-false" }));
    document.getElementById("pm-inv-on")?.addEventListener("click", () => sendAction("invisible", { flag: "-true" }));
    document.getElementById("pm-inv-off")?.addEventListener("click", () => sendAction("invisible", { flag: "-false" }));
    document.getElementById("pm-teleport")?.addEventListener("click", () => sendAction("teleport_to"));
    document.getElementById("pm-additem")?.addEventListener("click", () => sendAction("additem"));
    document.getElementById("pm-addxp")?.addEventListener("click", () => sendAction("addxp"));
    document.getElementById("pm-wl-add")?.addEventListener("click", () => sendAction("whitelist_add"));
    document.getElementById("pm-wl-remove")?.addEventListener("click", () => sendAction("whitelist_remove"));
    document.getElementById("pm-send-msg")?.addEventListener("click", () => {
      const message = document.getElementById("pm-servermsg")?.value?.trim() || "";
      sendToCSharp("player_action", { action: "servermsg", message });
    });

    document.getElementById("discord-save-btn")?.addEventListener("click", () => {
      const hours = Array.from(document.querySelectorAll("#discord-hours-grid .hour-tile.selected"))
        .map((t) => Number(t.dataset.hour))
        .filter((h) => Number.isInteger(h));
      sendToCSharp("save_discord_settings", {
        discordWebhookUrl: document.getElementById("discord-webhook-url")?.value || "",
        discordNotifyEnabled: Boolean(document.getElementById("discord-notify-enabled")?.checked),
        discordCustomHours: hours,
        discordEvents: collectDiscordEventsFromDom()
      });
    });

    document.getElementById("discord-test-btn")?.addEventListener("click", () => {
      sendToCSharp("test_discord_webhook", {
        discordWebhookUrl: document.getElementById("discord-webhook-url")?.value || ""
      });
    });
  }

  function renderBackupList(payload) {
    const list = document.getElementById("backup-list");
    if (!list) {
      return;
    }
    const backups = payload?.backups || [];
    if (!backups.length) {
      list.innerHTML = `<p class="muted">${tr("backup.empty")}</p>`;
      return;
    }
    list.innerHTML = backups.map((b) => `
      <div class="backup-item">
        <div>
          <strong>${b.name || ""}</strong>
          <div class="muted">${b.createdAt || ""}</div>
          <code>${b.path || ""}</code>
        </div>
      </div>
    `).join("");
  }

  function renderModList(payload) {
    const container = document.getElementById("mod-list-container");
    const stats = document.getElementById("mod-list-stats");
    const source = document.getElementById("mod-list-source");
    if (!container) {
      return;
    }

    if (!payload || payload.success === false) {
      container.innerHTML = `<p class="muted">${payload?.message || tr("mod.none")}</p>`;
      if (stats) {
        stats.textContent = "";
      }
      return;
    }

    if (source) {
      source.textContent = payload.path ? `Quelle: ${payload.path}` : "";
    }
    if (stats) {
      stats.textContent = `Workshop-IDs: ${payload.workshopCount ?? 0} · Mod-IDs: ${payload.modCount ?? 0}`;
    }

    container.className = modViewMode === "list" ? "mod-tiles list-mode" : "mod-tiles";
    container.innerHTML = "";

    const mods = payload.mods || [];
    if (!mods.length) {
      container.innerHTML = `<p class="muted">${tr("mod.none")}</p>`;
      return;
    }

    mods.forEach((mod) => {
      const card = document.createElement("div");
      card.className = "mod-card";
      const title = mod.name || mod.modId || mod.workshopId || "Unknown mod";
      const steam = mod.steamUrl
        ? `<a href="#" data-url="${mod.steamUrl}" class="steam-link">${tr("mod.openSteam")}</a>`
        : `<span class='muted'>${tr("mod.noSteam")}</span>`;
      card.innerHTML = `
        <h4>#${mod.index} ${title}</h4>
        <div class="muted">Mod-ID: <code>${mod.modId || "–"}</code></div>
        <div class="muted">Workshop-ID: <code>${mod.workshopId || "–"}</code></div>
        <div style="margin-top:8px">${steam}</div>
      `;
      const link = card.querySelector(".steam-link");
      if (link) {
        link.addEventListener("click", (e) => {
          e.preventDefault();
          sendToCSharp("open_url", { url: mod.steamUrl });
        });
      }
      container.appendChild(card);
    });
  }

  function showLanguageModal(openSettingsAfter) {
    const modal = document.getElementById("language-modal");
    if (!modal) {
      if (openSettingsAfter) {
        switchTab("settings");
      }
      return;
    }
    modal.classList.remove("hidden");
    modal.dataset.openSettingsAfter = openSettingsAfter ? "1" : "0";
  }

  function setupLanguageAndModControls() {
    const continueBtn = document.getElementById("language-modal-continue");
    if (continueBtn) {
      continueBtn.addEventListener("click", () => {
        const select = document.getElementById("language-modal-select");
        const lang = select?.value || "en";
        sendToCSharp("set_language", { language: lang });
        applyUiLanguage(lang);
        const settingsLang = document.getElementById("settings-ui-language");
        if (settingsLang) {
          settingsLang.value = lang;
        }
        const modal = document.getElementById("language-modal");
        const openSettings = modal?.dataset.openSettingsAfter === "1";
        modal?.classList.add("hidden");
        if (openSettings) {
          switchTab("settings");
          showToast(tr("settings.welcome"), true, 5000);
        }
      });
    }

    const tilesBtn = document.getElementById("mod-view-tiles");
    const listBtn = document.getElementById("mod-view-list");
    if (tilesBtn) {
      tilesBtn.addEventListener("click", () => {
        modViewMode = "tiles";
        const container = document.getElementById("mod-list-container");
        if (container) {
          container.classList.remove("list-mode");
        }
      });
    }
    if (listBtn) {
      listBtn.addEventListener("click", () => {
        modViewMode = "list";
        const container = document.getElementById("mod-list-container");
        if (container) {
          container.classList.add("list-mode");
        }
      });
    }

    const settingsLang = document.getElementById("settings-ui-language");
    if (settingsLang) {
      settingsLang.addEventListener("change", () => {
        sendToCSharp("set_language", { language: settingsLang.value });
        applyUiLanguage(settingsLang.value);
        updateInfoBanners({
          zomboidDataPath: document.getElementById("settings-zomboid-path")?.value || "",
          rconHost: document.getElementById("settings-rcon-host")?.value || "",
          rconPort: document.getElementById("settings-rcon-port")?.value || ""
        });
        requestServerStatus();
      });
    }
  }

  function applyServerStatus(payload) {
    if (!payload) {
      return;
    }

    const statusEl = document.getElementById("server-status-text");
    const addressEl = document.getElementById("server-address-text");
    const playersEl = document.getElementById("server-players-text");
    const lastRestartEl = document.getElementById("server-lastrestart-text");
    const sidebarStatus = document.getElementById("sidebar-status");

    const status = String(payload.status || "").toLowerCase();
    const startBtn = document.getElementById("btn-server-start");
    if (startBtn) {
      // Keep clickable so we can show a clear toast when already online.
      startBtn.disabled = false;
      startBtn.classList.toggle("disabled", false);
      startBtn.title = status === "online"
        ? tr("server.startBlockedOnline")
        : "";
    }

    if (statusEl) {
      statusEl.textContent = status === "online"
        ? tr("server.online")
        : status === "offline"
          ? tr("server.offline")
          : (payload.status || "–");
      statusEl.style.color = status === "online" ? "var(--success)" : "var(--error)";
    }

    if (addressEl) {
      addressEl.textContent = payload.address || "–";
    }

    if (playersEl) {
      playersEl.textContent = payload.players || "–";
    }

    if (lastRestartEl) {
      const lr = String(payload.lastRestart || "");
      lastRestartEl.textContent = (!lr || /no restart/i.test(lr))
        ? tr("server.noRestart")
        : lr;
    }

    if (sidebarStatus) {
      const text = sidebarStatus.querySelector(".status-text");
      if (text) {
        text.textContent = status === "online" ? tr("nav.online") : tr("nav.offline");
      } else {
        sidebarStatus.textContent = status === "online" ? tr("nav.online") : tr("nav.offline");
      }
    }

    const sidebarDot = document.getElementById("sidebar-status-dot");
    if (sidebarDot) {
      sidebarDot.classList.remove("online", "offline");
      sidebarDot.classList.add(status === "online" ? "online" : "offline");
    }
  }

  function updateInfoBanners(payload) {
    const host = payload?.rconHost || "";
    const port = payload?.rconPort;
    const zomboidPath = payload?.zomboidDataPath || "";

    const rconInfo = document.getElementById("rcon-connection-info");
    if (rconInfo) {
      rconInfo.textContent = host
        ? `${host}:${port ?? ""}`
        : tr("config.notConfigured");
    }

    const configInfo = document.getElementById("config-server-path-info");
    if (configInfo) {
      configInfo.textContent = zomboidPath || tr("config.notConfigured");
    }
  }

  function applySettingsData(payload) {
    if (!payload) {
      return;
    }

    applySelectedHours(payload.selectedHours || []);
    applySchedulerButton(Boolean(payload.schedulerActive));
    applyRestartWarnings(payload);
    applyModUpdateAutoRestart(payload);

    setInputValue("settings-server-path", payload.serverPath || "");
    setInputValue("settings-zomboid-path", payload.zomboidDataPath || "");
    setInputValue("settings-start-bat", payload.startBat || "");
    setInputValue("settings-rcon-host", payload.rconHost || "127.0.0.1");
    setInputValue("settings-rcon-port", payload.rconPort ?? 27015);
    setInputValue("settings-rcon-password", payload.rconPassword || "");
    setInputValue("ini-file-path", payload.lastIniFilePath || "");

    const langSelect = document.getElementById("settings-ui-language");
    if (langSelect && payload.uiLanguage) {
      langSelect.value = payload.uiLanguage;
      applyUiLanguage(payload.uiLanguage);
    } else if (payload.uiLanguage) {
      applyUiLanguage(payload.uiLanguage);
    }

    updateInfoBanners(payload);
    applyDiscordSettings(payload);

    if (payload.version) {
      const el = document.getElementById("app-version");
      if (el) {
        el.textContent = "Version " + payload.version;
      }
    }

    const lastIni = payload.lastIniFilePath || "";
    if (lastIni && !hasAutoLoadedIni) {
      hasAutoLoadedIni = true;
      currentIniPath = lastIni;
      sendToCSharp("load_ini_file", { path: lastIni });
    }
  }

  window.handleCSharpMessage = function handleCSharpMessage(message) {
    console.log(message);

    if (!message || !message.type) {
      return;
    }

    if (message.type === "server_status") {
      applyServerStatus(message.payload);
      return;
    }

    if (message.type === "settings_data") {
      applySettingsData(message.payload);
      return;
    }

    if (message.type === "mod_update_restart_status") {
      applyModUpdateRestartStatus(message.payload || {});
      return;
    }

    if (message.type === "mod_update_check_result") {
      applyModUpdateCheckResult(message.payload || {});
      return;
    }

    if (message.type === "mod_update_auto_restart_saved") {
      showToast(tr("modRestart.saved"), true);
      return;
    }

    if (message.type === "scheduler_status") {
      const active = typeof message.payload === "boolean"
        ? message.payload
        : Boolean(message.payload && message.payload.active);
      applySchedulerButton(active);
      return;
    }

    if (message.type === "rcon_response") {
      const success = Boolean(message.payload && message.payload.success);
      const response = (message.payload && message.payload.response) || "(empty response)";
      appendConsoleLine(response, success ? "success" : "error");
      return;
    }

    if (message.type === "app_info") {
      const version = (message.payload && message.payload.version) || "";
      const el = document.getElementById("app-version");
      if (el && version) {
        el.textContent = "Version " + version;
      }
      return;
    }

    if (message.type === "update_result") {
      const status = (message.payload && message.payload.status) || "";
      const text = (message.payload && message.payload.text) || "No update information available.";
      const updateBtn = document.querySelector('button[onclick*="check_updates"]');

      if (status === "checking" || status === "downloading" || status === "applying" || status === "busy") {
        if (updateBtn) {
          updateBtn.disabled = true;
        }
        showToast(text, true, status === "downloading" ? 1500 : 4000);
        return;
      }

      if (updateBtn) {
        updateBtn.disabled = false;
      }

      showToast(text, status !== "error", 4500);
      return;
    }

    if (message.type === "log") {
      const text = typeof message.payload === "string"
        ? message.payload
        : (message.payload && message.payload.message) || JSON.stringify(message.payload);
      prependLog(text);
      return;
    }

    if (message.type === "ini_file_selected") {
      const path = message.payload?.path || "";
      setInputValue("ini-file-path", path);
      currentIniPath = path;
      setConfigFileLabel(path);
      return;
    }

    if (message.type === "server_folder_selected") {
      const path = message.payload?.path || "";
      const startBat = message.payload?.startBat || "";
      const notice = message.payload?.notice || "";
      setInputValue("settings-server-path", path);
      updateInfoBanners({
        zomboidDataPath: document.getElementById("settings-zomboid-path")?.value || "",
        rconHost: document.getElementById("settings-rcon-host")?.value || "",
        rconPort: document.getElementById("settings-rcon-port")?.value || ""
      });
      if (startBat) {
        setInputValue("settings-start-bat", startBat);
      }
      if (notice) {
        showToast(notice, Boolean(startBat));
      }
      return;
    }

    if (message.type === "start_bat_selected") {
      setInputValue("settings-start-bat", message.payload?.startBat || "");
      if (message.payload?.serverPath) {
        setInputValue("settings-server-path", message.payload.serverPath);
      }
      showToast(tr("settings.batSelected"), true);
      return;
    }

    if (message.type === "zomboid_folder_selected") {
      setInputValue("settings-zomboid-path", message.payload?.path || "");
      return;
    }

    if (message.type === "settings_saved") {
      const success = Boolean(message.payload && message.payload.success);
      showToast(success ? tr("settings.saved") : tr("settings.saveFailed"), success);
      return;
    }

    if (message.type === "rcon_test_result") {
      const result = document.getElementById("rcon-test-result");
      if (!result) {
        return;
      }
      const success = Boolean(message.payload && message.payload.success);
      const text = (message.payload && message.payload.message) || "";
      result.textContent = success
        ? tr("settings.testOk")
        : `${tr("settings.testFail")} ${text}`;
      result.style.color = success ? "var(--success)" : "var(--error)";
      return;
    }

    if (message.type === "first_start") {
      const needsLanguage = Boolean(message.payload?.needsLanguage);
      const needsSettings = Boolean(message.payload?.needsSettings);

      if (needsLanguage) {
        showLanguageModal(needsSettings);
      } else if (needsSettings) {
        switchTab("settings");
        showToast(tr("settings.welcome"), true, 5000);
      }
      return;
    }

    if (message.type === "mod_list_data") {
      renderModList(message.payload);
      return;
    }

    if (message.type === "language_saved") {
      const lang = message.payload?.language;
      if (lang) {
        applyUiLanguage(lang);
      }
      // Refresh dynamic labels that i18n must not overwrite.
      updateInfoBanners({
        zomboidDataPath: document.getElementById("settings-zomboid-path")?.value || "",
        rconHost: document.getElementById("settings-rcon-host")?.value || "",
        rconPort: document.getElementById("settings-rcon-port")?.value || ""
      });
      requestServerStatus();
      showToast(tr("settings.langSaved"), true);
      return;
    }

    if (message.type === "server_console") {
      const line = message.payload?.line || message.payload?.message || "";
      if (line) {
        appendServerConsoleLine(line);
      }
      return;
    }

    if (message.type === "server_action_result") {
      const success = Boolean(message.payload?.success);
      const text = message.payload?.message || "";
      if (text) {
        showToast(text, success);
        appendServerConsoleLine(text);
      }
      return;
    }

    if (message.type === "backup_progress") {
      setBackupProgressModal(
        Boolean(message.payload?.active),
        message.payload?.done || 0,
        message.payload?.file || ""
      );
      return;
    }

    if (message.type === "backup_result") {
      setBackupProgressModal(false, 0, "");
      const success = Boolean(message.payload?.success);
      const text = message.payload?.message || (success ? tr("backup.created") : tr("backup.failed"));
      showToast(text, success);
      return;
    }

    if (message.type === "backup_list") {
      renderBackupList(message.payload);
      return;
    }

    if (message.type === "broadcast_status") {
      renderBroadcastSlots(message.payload);
      return;
    }

    if (message.type === "broadcast_send_result") {
      const success = Boolean(message.payload?.success);
      const text = message.payload?.message || (success ? tr("broadcast.sendNowOk") : tr("broadcast.sendNowFail"));
      showToast(text, success);
      return;
    }

    if (message.type === "backup_schedule_status") {
      applyBackupSchedule(message.payload);
      return;
    }

    if (message.type === "hardware_stats") {
      applyHardwareStats(message.payload);
      return;
    }

    if (message.type === "admin_commands_data") {
      applyAdminCommandsData(message.payload || {});
      return;
    }

    if (message.type === "log_list") {
      if (message.payload?.success === false) {
        showToast(message.payload?.message || tr("logViewer.empty"), false);
      }
      renderLogFileList(message.payload || {});
      return;
    }

    if (message.type === "log_content") {
      applyLogContent(message.payload || {});
      return;
    }

    if (message.type === "player_list") {
      const players = message.payload?.players || [];
      cachedPlayers = players;
      renderPlayerRows("players-list", players, true);
      renderPlayerRows("players-modal-list", players, false);
      const raw = document.getElementById("players-modal-raw");
      if (raw) {
        raw.textContent = message.payload?.raw || message.payload?.message || "";
      }
      if (message.payload?.success === false && message.payload?.message) {
        showToast(message.payload.message, false);
      }
      return;
    }

    if (message.type === "player_action_result") {
      const result = document.getElementById("pm-result");
      const success = Boolean(message.payload?.success);
      const text = message.payload?.message || "";
      if (result) {
        result.textContent = text;
        result.style.color = success ? "var(--success)" : "var(--error)";
      }
      showToast(text || (success ? "OK" : "Failed"), success);
      return;
    }

    if (message.type === "discord_settings_saved") {
      showToast(tr("discord.saved"), true);
      return;
    }

    if (message.type === "discord_test_result") {
      const el = document.getElementById("discord-result");
      const success = Boolean(message.payload?.success);
      const text = message.payload?.message || "";
      if (el) {
        el.textContent = text;
        el.style.color = success ? "var(--success)" : "var(--error)";
      }
      showToast(text, success);
      return;
    }

    if (message.type === "sandbox_loaded") {
      if (!message.payload?.success) {
        showToast(message.payload?.message || tr("sandbox.noFile"), false);
        return;
      }
      currentSandboxPath = message.payload?.path || "";
      const label = document.getElementById("sandbox-file-label");
      if (label) {
        label.textContent = currentSandboxPath;
      }
      renderIniForm(message.payload?.entries || [], {
        containerId: "sandbox-categories-container",
        editorCardId: "",
        searchId: "sandbox-search"
      });
      return;
    }

    if (message.type === "sandbox_saved") {
      const success = Boolean(message.payload?.success);
      const text = message.payload?.message || (success ? "Saved." : "Save failed.");
      showToast(text, success);
      return;
    }

    if (message.type === "ini_loaded") {
      const path = message.payload?.path || "";
      const entries = message.payload?.entries || [];
      currentIniPath = path;
      currentIniEntries = entries;
      setInputValue("ini-file-path", path);
      setConfigFileLabel(path);
      const container = document.getElementById("config-categories-container");
      if (container) {
        container.innerHTML = "";
      }
      renderIniForm(entries);
      applyConfigEditorMode("ini");
      return;
    }

    if (message.type === "ini_saved") {
      const success = Boolean(message.payload && message.payload.success);
      const text = (message.payload && message.payload.message) || (success ? "Saved." : "Save failed.");
      showToast(text, success);
    }
  };

  function setupCSharpBridge() {
    if (!(window.chrome && window.chrome.webview)) {
      console.warn("WebView2 bridge not available yet.");
      return;
    }

    window.chrome.webview.addEventListener("message", (event) => {
      let message = event.data;
      try {
        if (typeof message === "string") {
          message = JSON.parse(message);
        }
      } catch (error) {
        console.error("Failed to parse message from C#:", error, event.data);
        return;
      }

      window.handleCSharpMessage(message);
    });
  }

  document.addEventListener("DOMContentLoaded", () => {
    setupTabNavigation();
    setupRestartControls();
    setupRconControls();
    setupSettingsControls();
    setupConfigControls();
    setupLanguageAndModControls();
    setupPlayersAndDiscordControls();
    setupBroadcastControls();
    setupBackupScheduleControls();
    setupAdminCommandsControls();
    setupCSharpBridge();
    setupStatusPolling();
    applyConfigEditorMode("ini");
    requestSettings();
  });
})();
