namespace ZomboidManager;

public enum ModUpdateRestartPhase
{
    Idle,
    Countdown,
    WaitingForEmpty,
    ShuttingDown,
    Starting,
    Failed,
    Cancelled
}

/// <summary>
/// Orchestrates warn → optional wait-for-empty → clean save/quit/start for Workshop mod updates.
/// Process/RCON work is injected so MainForm can reuse existing server control logic.
/// </summary>
public sealed class ModUpdateRestartFlow
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private ModUpdateRestartPhase _phase = ModUpdateRestartPhase.Idle;
    private DateTime? _phaseStartedAt;
    private DateTime? _countdownEndsAt;
    private DateTime? _emptyWaitDeadline;
    private string _statusDetail = string.Empty;
    private List<string> _updatedMods = new();
    private int _warnMinutes;
    private bool _waitForEmpty;
    private int _maxWaitMinutes;
    private string _warningTemplate = string.Empty;

    public event Action? StatusChanged;
    public event Action<bool, string, IReadOnlyList<string>>? Completed;

    public ModUpdateRestartPhase Phase
    {
        get { lock (_gate) return _phase; }
    }

    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _phase is ModUpdateRestartPhase.Countdown
                    or ModUpdateRestartPhase.WaitingForEmpty
                    or ModUpdateRestartPhase.ShuttingDown
                    or ModUpdateRestartPhase.Starting;
            }
        }
    }

    public bool CanCancel
    {
        get
        {
            lock (_gate)
            {
                return _phase is ModUpdateRestartPhase.Countdown
                    or ModUpdateRestartPhase.WaitingForEmpty;
            }
        }
    }

    public IReadOnlyList<string> UpdatedMods
    {
        get { lock (_gate) return _updatedMods.ToList(); }
    }

    public bool TryCancel()
    {
        lock (_gate)
        {
            if (_phase is not (ModUpdateRestartPhase.Countdown or ModUpdateRestartPhase.WaitingForEmpty))
                return false;

            _cts?.Cancel();
            SetPhase_NoLock(ModUpdateRestartPhase.Cancelled, "Pending restart cancelled.");
        }

        RaiseStatusChanged();
        return true;
    }

    /// <summary>
    /// Called from the shared clean-restart path right before StartServer so the UI can show
    /// "Starting…" until the flow completes (SERVER STARTED watch is armed separately).
    /// </summary>
    public void NotifyEnteringStartPhase()
    {
        lock (_gate)
        {
            if (_phase != ModUpdateRestartPhase.ShuttingDown)
                return;
            SetPhase_NoLock(ModUpdateRestartPhase.Starting, "Starting server…");
        }

        RaiseStatusChanged();
    }

    public object BuildStatusPayload()
    {
        lock (_gate)
        {
            DateTime now = DateTime.Now;
            string label = _phase switch
            {
                ModUpdateRestartPhase.Idle => "Idle — no mod-update restart pending.",
                ModUpdateRestartPhase.Countdown when _countdownEndsAt is DateTime end =>
                    end > now
                        ? $"Restart scheduled in {FormatRemaining(end - now)}"
                        : "Restart scheduled — starting soon…",
                ModUpdateRestartPhase.WaitingForEmpty when _emptyWaitDeadline is DateTime deadline
                    && _phaseStartedAt is DateTime started =>
                    $"Waiting for server to be empty ({FormatElapsed(now - started)} elapsed of {_maxWaitMinutes} min max)",
                ModUpdateRestartPhase.WaitingForEmpty => "Waiting for server to be empty…",
                ModUpdateRestartPhase.ShuttingDown => "Shutting down server (save → quit)…",
                ModUpdateRestartPhase.Starting => "Starting server…",
                ModUpdateRestartPhase.Failed => string.IsNullOrWhiteSpace(_statusDetail)
                    ? "Mod-update restart failed."
                    : "Failed: " + _statusDetail,
                ModUpdateRestartPhase.Cancelled => "Pending restart cancelled.",
                _ => _statusDetail
            };

            return new
            {
                phase = _phase.ToString(),
                active = IsActiveUnsafe(),
                canCancel = _phase is ModUpdateRestartPhase.Countdown or ModUpdateRestartPhase.WaitingForEmpty,
                label,
                detail = _statusDetail,
                warnMinutes = _warnMinutes,
                waitForEmpty = _waitForEmpty,
                maxWaitMinutes = _maxWaitMinutes,
                countdownEndsAt = _countdownEndsAt?.ToString("o"),
                emptyWaitDeadline = _emptyWaitDeadline?.ToString("o"),
                updatedMods = _updatedMods.ToList()
            };
        }
    }

    public async Task RunAsync(
        ModUpdateAutoRestartConfig settings,
        IReadOnlyList<string> updatedMods,
        TimeSpan? countdownOverride,
        bool skipCountdown,
        Func<string, Task<string>> sendRconAsync,
        Func<Task<int>> getPlayerCountAsync,
        Func<CancellationToken, Task> runCleanSaveQuitStartAsync,
        Action<string> log,
        CancellationToken externalCancel = default)
    {
        CancellationTokenSource linked;
        lock (_gate)
        {
            if (IsActiveUnsafe())
                throw new InvalidOperationException("A mod-update restart is already in progress.");

            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCancel);
            linked = _cts;
            _updatedMods = (updatedMods ?? Array.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            _warnMinutes = Math.Max(0, settings.WarnMinutesBefore);
            _waitForEmpty = settings.WaitForEmpty;
            _maxWaitMinutes = Math.Max(1, settings.MaxWaitMinutes);
            _warningTemplate = string.IsNullOrWhiteSpace(settings.WarningMessage)
                ? "A mod has been updated. The server will restart in {minutes} minutes to apply the update."
                : settings.WarningMessage;
            _statusDetail = string.Empty;
        }

        CancellationToken ct = linked.Token;
        try
        {
            int minutesForMessage = skipCountdown
                ? 0
                : (int)Math.Ceiling((countdownOverride ?? TimeSpan.FromMinutes(_warnMinutes)).TotalMinutes);
            if (minutesForMessage < 0)
                minutesForMessage = 0;

            bool useEmptyWait = !skipCountdown && _waitForEmpty && countdownOverride is null;

            string warning;
            if (useEmptyWait)
            {
                // Avoid promising a fixed "in X minutes" countdown while waiting for empty.
                if (!string.IsNullOrWhiteSpace(_warningTemplate)
                    && !_warningTemplate.Contains("{minutes}", StringComparison.OrdinalIgnoreCase))
                {
                    warning = _warningTemplate;
                }
                else
                {
                    warning =
                        $"A mod has been updated. The server will restart when empty (at latest in {_maxWaitMinutes} minutes).";
                }
            }
            else
            {
                warning = _warningTemplate.Replace(
                    "{minutes}",
                    minutesForMessage.ToString(),
                    StringComparison.OrdinalIgnoreCase);
            }

            log("Mod-update restart: sending warning to players.");
            string warnCmd = BroadcastRconCommand.FormatOutgoingMessage(warning);
            string warnResp = await sendRconAsync(warnCmd);
            if (IsRconFailure(warnResp))
            {
                Fail("RCON warning failed: " + warnResp, log);
                return;
            }

            if (useEmptyWait)
            {
                lock (_gate)
                {
                    _phaseStartedAt = DateTime.Now;
                    _emptyWaitDeadline = DateTime.Now.AddMinutes(_maxWaitMinutes);
                    SetPhase_NoLock(ModUpdateRestartPhase.WaitingForEmpty, "Waiting for empty server…");
                }
                RaiseStatusChanged();

                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int players;
                    try
                    {
                        players = await getPlayerCountAsync();
                    }
                    catch (Exception ex)
                    {
                        Fail("Could not read player count: " + ex.Message, log);
                        return;
                    }

                    DateTime now = DateTime.Now;
                    bool timedOut;
                    lock (_gate)
                    {
                        timedOut = _emptyWaitDeadline is DateTime d && now >= d;
                        _statusDetail = players == 0
                            ? "Server empty — proceeding."
                            : $"Players online: {players}";
                    }
                    RaiseStatusChanged();

                    if (players <= 0 || timedOut)
                    {
                        if (timedOut && players > 0)
                            log($"Mod-update restart: max wait ({_maxWaitMinutes} min) reached with {players} player(s) still online.");
                        break;
                    }

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(15), ct);
                    }
                    catch (OperationCanceledException)
                    {
                        lock (_gate)
                            SetPhase_NoLock(ModUpdateRestartPhase.Cancelled, "Pending restart cancelled.");
                        RaiseStatusChanged();
                        Completed?.Invoke(false, "Cancelled.", UpdatedMods);
                        ResetToIdleSoon();
                        return;
                    }
                }
            }
            else if (!skipCountdown)
            {
                TimeSpan delay = countdownOverride ?? TimeSpan.FromMinutes(_warnMinutes);
                if (delay > TimeSpan.Zero)
                {
                    lock (_gate)
                    {
                        _countdownEndsAt = DateTime.Now + delay;
                        SetPhase_NoLock(ModUpdateRestartPhase.Countdown,
                            $"Waiting {FormatRemaining(delay)} before restart.");
                    }
                    RaiseStatusChanged();

                    try
                    {
                        await Task.Delay(delay, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        lock (_gate)
                            SetPhase_NoLock(ModUpdateRestartPhase.Cancelled, "Pending restart cancelled.");
                        RaiseStatusChanged();
                        Completed?.Invoke(false, "Cancelled.", UpdatedMods);
                        ResetToIdleSoon();
                        return;
                    }
                }
            }

            log("Mod-update restart: Restarting now.");
            string nowResp = await sendRconAsync(
                BroadcastRconCommand.FormatOutgoingMessage("Restarting now"));
            if (IsRconFailure(nowResp))
                log("Mod-update restart: final warning RCON failed (continuing): " + nowResp);

            lock (_gate)
                SetPhase_NoLock(ModUpdateRestartPhase.ShuttingDown, "Save → quit…");
            RaiseStatusChanged();

            try
            {
                await runCleanSaveQuitStartAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // Past the cancel point — treat as failure if shutdown was interrupted oddly.
                Fail("Restart interrupted during shutdown/start.", log);
                return;
            }
            catch (Exception ex)
            {
                Fail(ex.Message, log);
                return;
            }

            lock (_gate)
                SetPhase_NoLock(ModUpdateRestartPhase.Idle, "Restart completed.");
            RaiseStatusChanged();
            Completed?.Invoke(true, "Restart completed.", UpdatedMods);
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
                SetPhase_NoLock(ModUpdateRestartPhase.Cancelled, "Pending restart cancelled.");
            RaiseStatusChanged();
            Completed?.Invoke(false, "Cancelled.", UpdatedMods);
            ResetToIdleSoon();
        }
        catch (Exception ex)
        {
            Fail(ex.Message, log);
        }
    }

    public static async Task RunSelfTestAsync()
    {
        string outPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZomboidManager",
            "mod_update_restart_selftest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        var flow = new ModUpdateRestartFlow();
        var settings = new ModUpdateAutoRestartConfig
        {
            Enabled = true,
            WarnMinutesBefore = 5,
            WaitForEmpty = false,
            WarningMessage = "Restart in {minutes} minutes."
        };

        int rconCalls = 0;
        int cleanCalls = 0;
        bool completedOk = false;
        string completedMsg = "";

        flow.Completed += (ok, msg, _) =>
        {
            completedOk = ok;
            completedMsg = msg;
        };

        // Test 1: schedule short countdown then cancel
        var runTask = flow.RunAsync(
            settings,
            new[] { "Test Mod" },
            TimeSpan.FromSeconds(30),
            skipCountdown: false,
            sendRconAsync: cmd =>
            {
                rconCalls++;
                return Task.FromResult("ok");
            },
            getPlayerCountAsync: () => Task.FromResult(0),
            runCleanSaveQuitStartAsync: _ =>
            {
                cleanCalls++;
                return Task.CompletedTask;
            },
            log: _ => { });

        await Task.Delay(200);
        object statusDuring = flow.BuildStatusPayload();
        bool cancelled = flow.TryCancel();
        await runTask;

        // Test 2: skip countdown runs clean path
        var flow2 = new ModUpdateRestartFlow();
        int clean2 = 0;
        await flow2.RunAsync(
            settings,
            new[] { "Mod A" },
            null,
            skipCountdown: true,
            sendRconAsync: _ => Task.FromResult("ok"),
            getPlayerCountAsync: () => Task.FromResult(0),
            runCleanSaveQuitStartAsync: _ =>
            {
                clean2++;
                return Task.CompletedTask;
            },
            log: _ => { });

        // Test 3: simulated workshop detect via ModUpdateChecker state
        const string sampleId = "2875848298";
        IReadOnlyList<string> firstCheck;
        try
        {
            // Lower stored timestamp then check (reuse checker self-test idea briefly)
            await ModUpdateChecker.CheckForUpdatedModsAsync(new[] { sampleId }); // seed current
            string statePath = ModUpdateChecker.StateFilePath;
            if (File.Exists(statePath))
            {
                var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, long>>(
                    File.ReadAllText(statePath)) ?? new();
                if (dict.TryGetValue(sampleId, out long cur))
                {
                    dict[sampleId] = cur - 1;
                    File.WriteAllText(statePath, System.Text.Json.JsonSerializer.Serialize(dict));
                }
            }

            firstCheck = await ModUpdateChecker.CheckForUpdatedModsAsync(new[] { sampleId });
        }
        catch (Exception ex)
        {
            firstCheck = Array.Empty<string>();
            File.WriteAllText(outPath, System.Text.Json.JsonSerializer.Serialize(new
            {
                ok = false,
                error = "workshop check failed: " + ex.Message
            }));
            Console.WriteLine(File.ReadAllText(outPath));
            return;
        }

        bool ok = cancelled
                  && cleanCalls == 0
                  && !completedOk
                  && completedMsg == "Cancelled."
                  && clean2 == 1
                  && firstCheck.Count >= 1
                  && rconCalls >= 1;

        string json = System.Text.Json.JsonSerializer.Serialize(new
        {
            ok,
            cancelled,
            rconCalls,
            cleanCallsBeforeCancel = cleanCalls,
            completedOk,
            completedMsg,
            skipCountdownCleanCalls = clean2,
            simulatedUpdateMods = firstCheck,
            statusDuring
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(outPath, json);
        Console.WriteLine(json);
    }

    private void Fail(string detail, Action<string> log)
    {
        log("Mod-update restart failed: " + detail);
        lock (_gate)
            SetPhase_NoLock(ModUpdateRestartPhase.Failed, detail);
        RaiseStatusChanged();
        Completed?.Invoke(false, detail, UpdatedMods);
        ResetToIdleSoon();
    }

    private void ResetToIdleSoon()
    {
        // Keep Failed/Cancelled visible briefly via status; caller may push again later.
        lock (_gate)
        {
            if (_phase is ModUpdateRestartPhase.Failed or ModUpdateRestartPhase.Cancelled)
            {
                // leave phase for UI; next successful schedule clears it
            }
        }
    }

    private void SetPhase_NoLock(ModUpdateRestartPhase phase, string detail)
    {
        _phase = phase;
        _statusDetail = detail;
        if (phase is ModUpdateRestartPhase.Countdown or ModUpdateRestartPhase.WaitingForEmpty)
            _phaseStartedAt ??= DateTime.Now;
        if (phase == ModUpdateRestartPhase.Idle)
        {
            _countdownEndsAt = null;
            _emptyWaitDeadline = null;
            _phaseStartedAt = null;
        }
    }

    private bool IsActiveUnsafe() =>
        _phase is ModUpdateRestartPhase.Countdown
            or ModUpdateRestartPhase.WaitingForEmpty
            or ModUpdateRestartPhase.ShuttingDown
            or ModUpdateRestartPhase.Starting;

    private void RaiseStatusChanged() => StatusChanged?.Invoke();

    private static bool IsRconFailure(string? response) =>
        string.IsNullOrWhiteSpace(response)
        || response.StartsWith("RCON error", StringComparison.OrdinalIgnoreCase);

    private static string FormatRemaining(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;
        int totalSeconds = (int)Math.Ceiling(span.TotalSeconds);
        int mins = totalSeconds / 60;
        int secs = totalSeconds % 60;
        return $"{mins}:{secs:00}";
    }

    private static string FormatElapsed(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;
        int mins = (int)Math.Floor(span.TotalMinutes);
        return $"{mins} min";
    }
}
