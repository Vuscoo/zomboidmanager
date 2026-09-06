namespace ZomboidManager;

public class ScheduleManager
{
    private readonly HashSet<int> _selectedHours = new();
    private int? _lastTriggeredHour;
    private bool _announce10Min;
    private bool _announce5Min;
    private string? _last10AnnounceKey;
    private string? _last5AnnounceKey;
    private bool _active;

    public event Action<int>? RestartTriggered;
    /// <summary>Fired with minutes-before (10 or 5) when a pre-restart announcement should be sent.</summary>
    public event Action<int>? WarningAnnouncementTriggered;
    public event Action<string>? LogMessage;

    public bool IsRunning => _active;

    public void UpdateSelectedHours(IEnumerable<int> hours)
    {
        _selectedHours.Clear();
        foreach (int hour in hours)
        {
            if (hour is >= 0 and <= 23)
                _selectedHours.Add(hour);
        }
    }

    public void UpdateWarningSettings(bool announce10Min, bool announce5Min)
    {
        _announce10Min = announce10Min;
        _announce5Min = announce5Min;
    }

    public void Start()
    {
        _active = true;
        LogMessage?.Invoke("scheduler.started");
    }

    public void Stop()
    {
        _active = false;
        LogMessage?.Invoke("scheduler.stopped");
    }

    /// <summary>Invoked by <see cref="SchedulerHeartbeat"/>; no-op while the restart scheduler is off.</summary>
    public void Tick()
    {
        if (!_active)
            return;

        DateTime now = DateTime.Now;

        CheckPreRestartAnnouncements(now);

        if (now.Minute != 0)
        {
            _lastTriggeredHour = null;
            return;
        }

        int hour = now.Hour;
        if (!_selectedHours.Contains(hour))
            return;

        if (_lastTriggeredHour == hour)
            return;

        _lastTriggeredHour = hour;
        RestartTriggered?.Invoke(hour);
        LogMessage?.Invoke($"scheduler.restartHour|{hour}");
    }

    private void CheckPreRestartAnnouncements(DateTime now)
    {
        if (!_announce10Min && !_announce5Min)
            return;

        // Restart at H:00 → 10-min warn at (H-1):50, 5-min warn at (H-1):55
        if (now.Minute is not (50 or 55))
            return;

        int targetHour = (now.Hour + 1) % 24;
        if (!_selectedHours.Contains(targetHour))
            return;

        int minutesBefore = now.Minute == 50 ? 10 : 5;
        if (minutesBefore == 10 && !_announce10Min)
            return;
        if (minutesBefore == 5 && !_announce5Min)
            return;

        string key = $"{now:yyyy-MM-dd}-{targetHour}";
        if (minutesBefore == 10)
        {
            if (_last10AnnounceKey == key)
                return;
            _last10AnnounceKey = key;
        }
        else
        {
            if (_last5AnnounceKey == key)
                return;
            _last5AnnounceKey = key;
        }

        WarningAnnouncementTriggered?.Invoke(minutesBefore);
        LogMessage?.Invoke($"scheduler.preAnnounce|{minutesBefore}|{targetHour}");
    }
}
