namespace ZomboidManager;

/// <summary>
/// Single WinForms timer that drives restart / backup / broadcast / Discord
/// schedule checks every few seconds instead of four independent timers.
/// </summary>
public sealed class SchedulerHeartbeat : IDisposable
{
    public const int IntervalMs = 5000;

    private readonly System.Windows.Forms.Timer _timer;
    private readonly List<Action> _ticks = new();
    private bool _disposed;

    public SchedulerHeartbeat()
    {
        _timer = new System.Windows.Forms.Timer { Interval = IntervalMs };
        _timer.Tick += OnTick;
    }

    public bool IsRunning => _timer.Enabled;

    public void Register(Action tick)
    {
        ArgumentNullException.ThrowIfNull(tick);
        _ticks.Add(tick);
    }

    public void Start()
    {
        if (_disposed)
            return;
        if (!_timer.Enabled)
            _timer.Start();
    }

    public void Stop()
    {
        if (_disposed)
            return;
        _timer.Stop();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer.Dispose();
        _ticks.Clear();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // Snapshot so Register during a tick cannot mutate the enumerator.
        Action[] snapshot = _ticks.ToArray();
        foreach (Action tick in snapshot)
        {
            try
            {
                tick();
            }
            catch
            {
                // one schedule must not break the others
            }
        }
    }
}
