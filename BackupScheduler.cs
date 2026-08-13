namespace ZomboidManager;

public sealed class BackupScheduler : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer;
    private BackupScheduleConfig _schedule = new();
    private string? _lastFiredKey;
    private bool _tickBusy;

    public event Action? BackupDue;
    public event Action? ScheduleDisabled;

    public BackupScheduler()
    {
        _timer = new System.Windows.Forms.Timer { Interval = 20000 };
        _timer.Tick += OnTick;
    }

    public BackupScheduleConfig Schedule => _schedule;

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }

    public void UpdateSchedule(BackupScheduleConfig? schedule)
    {
        _schedule = Normalize(schedule);
        _lastFiredKey = null;
    }

    public static BackupScheduleConfig Normalize(BackupScheduleConfig? source)
    {
        var s = source ?? new BackupScheduleConfig();
        s.Mode = string.Equals(s.Mode, "oneTime", StringComparison.OrdinalIgnoreCase)
            ? "oneTime"
            : "recurring";
        s.Time = string.IsNullOrWhiteSpace(s.Time) ? "03:00" : s.Time.Trim();
        s.OneTimeDateTime ??= string.Empty;
        s.Days ??= new List<int>();
        s.Days = s.Days.Where(d => d is >= 0 and <= 6).Distinct().OrderBy(d => d).ToList();
        return s;
    }

    public DateTime? GetNextBackupTime(DateTime? from = null)
    {
        DateTime now = from ?? DateTime.Now;
        if (!_schedule.Enabled)
            return null;

        if (string.Equals(_schedule.Mode, "oneTime", StringComparison.OrdinalIgnoreCase))
        {
            if (TryParseLocal(_schedule.OneTimeDateTime, out DateTime once) && once > now)
                return once;
            return null;
        }

        if (!TryParseTimeOfDay(_schedule.Time, out TimeSpan tod) || _schedule.Days.Count == 0)
            return null;

        for (int add = 0; add <= 8; add++)
        {
            DateTime day = now.Date.AddDays(add);
            int dow = (int)day.DayOfWeek; // Sunday=0 ... Saturday=6
            if (!_schedule.Days.Contains(dow))
                continue;

            DateTime candidate = day + tod;
            if (candidate > now)
                return candidate;
        }

        return null;
    }

    public string FormatNextLabel()
    {
        DateTime? next = GetNextBackupTime();
        if (next is null)
            return string.Empty;
        return $"Next backup: {next.Value:dddd HH:mm}";
    }

    public object BuildScheduleDto() => new
    {
        enabled = _schedule.Enabled,
        mode = _schedule.Mode,
        days = _schedule.Days,
        time = _schedule.Time,
        oneTimeDateTime = _schedule.OneTimeDateTime,
        nextLabel = FormatNextLabel(),
        nextBackupAt = GetNextBackupTime()?.ToString("o")
    };

    public object BuildStatusPayload() => new
    {
        backupSchedule = BuildScheduleDto()
    };

    /// <summary>Disable one-time schedule after a successful run.</summary>
    public void MarkOneTimeDone()
    {
        if (!string.Equals(_schedule.Mode, "oneTime", StringComparison.OrdinalIgnoreCase))
            return;
        _schedule.Enabled = false;
        ScheduleDisabled?.Invoke();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_tickBusy || !_schedule.Enabled)
            return;

        _tickBusy = true;
        try
        {
            DateTime now = DateTime.Now;

            if (string.Equals(_schedule.Mode, "oneTime", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseLocal(_schedule.OneTimeDateTime, out DateTime once))
                    return;
                if (now < once)
                    return;

                string key = $"once:{once:o}";
                if (_lastFiredKey == key)
                    return;
                _lastFiredKey = key;
                BackupDue?.Invoke();
                return;
            }

            if (!TryParseTimeOfDay(_schedule.Time, out TimeSpan tod) || _schedule.Days.Count == 0)
                return;

            int dow = (int)now.DayOfWeek;
            if (!_schedule.Days.Contains(dow))
                return;

            // Fire in the first ~minute window of the scheduled time
            DateTime scheduled = now.Date + tod;
            if (now < scheduled || now >= scheduled.AddMinutes(1))
                return;

            string recurringKey = $"{now:yyyy-MM-dd}-{tod:hh\\:mm}";
            if (_lastFiredKey == recurringKey)
                return;
            _lastFiredKey = recurringKey;
            BackupDue?.Invoke();
        }
        finally
        {
            _tickBusy = false;
        }
    }

    private static bool TryParseTimeOfDay(string value, out TimeSpan result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        return TimeSpan.TryParseExact(value.Trim(), new[] { @"hh\:mm", @"h\:mm" }, null, out result)
               || TimeSpan.TryParse(value.Trim(), out result);
    }

    private static bool TryParseLocal(string? value, out DateTime result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (!DateTime.TryParse(value, out DateTime parsed))
            return false;
        result = DateTime.SpecifyKind(parsed, DateTimeKind.Local);
        return true;
    }
}
