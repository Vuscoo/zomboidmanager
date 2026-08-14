namespace ZomboidManager;

/// <summary>
/// Lightweight background sampler. Records one point only while the dedicated server is online.
/// </summary>
public sealed class StatsCollector : IDisposable
{
    private readonly StatsHistoryStore _store;
    private readonly Func<bool> _isServerOnline;
    private readonly Func<HardwareSnapshot> _takeSnapshot;
    private readonly Func<Task<int?>> _getPlayerCountAsync;
    private readonly Func<int> _getIntervalMinutes;
    private readonly Func<int> _getRetentionDays;
    private readonly object _gate = new();
    private readonly System.Threading.Timer _timer;
    private int _inFlight;
    private bool _disposed;
    private bool _wasOnline;

    public StatsCollector(
        StatsHistoryStore store,
        Func<bool> isServerOnline,
        Func<HardwareSnapshot> takeSnapshot,
        Func<Task<int?>> getPlayerCountAsync,
        Func<int> getIntervalMinutes,
        Func<int> getRetentionDays)
    {
        _store = store;
        _isServerOnline = isServerOnline;
        _takeSnapshot = takeSnapshot;
        _getPlayerCountAsync = getPlayerCountAsync;
        _getIntervalMinutes = getIntervalMinutes;
        _getRetentionDays = getRetentionDays;
        _timer = new System.Threading.Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        if (_disposed)
            return;
        // First sample quickly so history starts accumulating without waiting a full interval.
        _timer.Change(TimeSpan.FromSeconds(12), Interval);
    }

    public void ApplyInterval()
    {
        if (_disposed)
            return;
        _timer.Change(Interval, Interval);
    }

    public void Stop()
    {
        if (_disposed)
            return;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        try
        {
            if (_wasOnline)
            {
                _store.NoteOffline(DateTime.UtcNow);
                _wasOnline = false;
            }
        }
        catch
        {
            // ignore shutdown races
        }
    }

    public void NotifyUnexpectedExit()
    {
        try
        {
            _store.AddRestart(DateTime.UtcNow, RestartReasons.CrashRecovery);
            _store.NoteOffline(DateTime.UtcNow);
            _wasOnline = false;
        }
        catch
        {
            // never throw from process-exit path
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
        _timer.Dispose();
    }

    private TimeSpan Interval
    {
        get
        {
            int minutes = StatsHistoryStore.ClampIntervalMinutes(_getIntervalMinutes());
            return TimeSpan.FromMinutes(minutes);
        }
    }

    private void OnTick(object? state)
    {
        if (_disposed)
            return;
        if (Interlocked.Exchange(ref _inFlight, 1) == 1)
            return;

        _ = TickAsync();
    }

    private async Task TickAsync()
    {
        try
        {
            bool online = false;
            try
            {
                online = _isServerOnline();
            }
            catch
            {
                online = false;
            }

            DateTime now = DateTime.UtcNow;
            if (!online)
            {
                if (_wasOnline)
                {
                    _store.NoteOffline(now);
                    _wasOnline = false;
                }

                MaybePrune();
                return;
            }

            _store.NoteOnline(now);
            _wasOnline = true;

            HardwareSnapshot hw;
            try
            {
                hw = _takeSnapshot();
            }
            catch
            {
                return;
            }

            int? players = null;
            try
            {
                players = await _getPlayerCountAsync().ConfigureAwait(false);
            }
            catch
            {
                players = null;
            }

            _store.AddSample(now, players, hw.CpuUsage, hw.RamUsagePercent, hw.DiskFreeBytes);
            MaybePrune();
        }
        catch
        {
            // collection must never disturb the UI
        }
        finally
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    private void MaybePrune()
    {
        try
        {
            if (!_store.ShouldPrune(TimeSpan.FromHours(6)))
                return;
            int days = StatsHistoryStore.ClampRetentionDays(_getRetentionDays());
            _store.PruneOlderThan(TimeSpan.FromDays(days));
        }
        catch
        {
            // ignore prune failures
        }
    }
}
