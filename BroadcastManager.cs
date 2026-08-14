namespace ZomboidManager;

/// <summary>
/// Polls enabled broadcast slots and raises events when a message is due.
/// Does not send RCON itself — MainForm handles delivery and success/failure.
/// Ticked by <see cref="SchedulerHeartbeat"/>.
/// </summary>
public sealed class BroadcastManager
{
    public const int MaxSlots = 5;

    private List<BroadcastMessageSlot> _slots = Normalize(null);
    private bool _tickInProgress;

    /// <summary>slotIndex, slot — raised when a send should be attempted.</summary>
    public event Action<int, BroadcastMessageSlot>? BroadcastDue;

    public IReadOnlyList<BroadcastMessageSlot> Slots => _slots;

    public void UpdateSlots(IEnumerable<BroadcastMessageSlot>? slots)
    {
        _slots = Normalize(slots);
        RecalculateNextSendTimes(forcePastReschedule: true);
    }

    public static List<BroadcastMessageSlot> Normalize(IEnumerable<BroadcastMessageSlot>? slots)
    {
        var list = (slots ?? Enumerable.Empty<BroadcastMessageSlot>())
            .Select(CloneSlot)
            .Take(MaxSlots)
            .ToList();

        while (list.Count < MaxSlots)
            list.Add(new BroadcastMessageSlot());

        for (int i = 0; i < list.Count; i++)
        {
            BroadcastMessageSlot slot = list[i];
            slot.Mode = string.Equals(slot.Mode, "recurring", StringComparison.OrdinalIgnoreCase)
                ? "recurring"
                : "oneTime";
            slot.IntervalUnit = string.Equals(slot.IntervalUnit, "minutes", StringComparison.OrdinalIgnoreCase)
                ? "minutes"
                : "hours";
            if (slot.IntervalValue < 1)
                slot.IntervalValue = 1;
            slot.Message ??= string.Empty;
            slot.ScheduledTime ??= string.Empty;
        }

        return list;
    }

    public void RecalculateNextSendTimes(bool forcePastReschedule = false)
    {
        DateTime now = DateTime.Now;
        foreach (BroadcastMessageSlot slot in _slots)
        {
            if (!slot.Enabled || string.IsNullOrWhiteSpace(slot.Message))
            {
                slot.NextSendAt = null;
                continue;
            }

            if (IsRecurring(slot))
            {
                if (slot.NextSendAt is null || (forcePastReschedule && slot.NextSendAt <= now))
                    slot.NextSendAt = now + GetInterval(slot);
            }
            else if (TryParseLocal(slot.ScheduledTime, out DateTime scheduled))
            {
                slot.NextSendAt = scheduled;
            }
            else
            {
                slot.NextSendAt = null;
            }
        }
    }

    /// <summary>Call after a successful send to advance or disable the slot.</summary>
    public void MarkSent(int index)
    {
        if (index < 0 || index >= _slots.Count)
            return;

        BroadcastMessageSlot slot = _slots[index];
        if (IsRecurring(slot))
        {
            slot.NextSendAt = DateTime.Now + GetInterval(slot);
        }
        else
        {
            slot.Enabled = false;
            slot.NextSendAt = null;
        }
    }

    public object BuildStatusPayload()
    {
        DateTime now = DateTime.Now;
        return new
        {
            broadcastMessages = _slots.Select((slot, index) => new
            {
                index,
                message = slot.Message ?? string.Empty,
                mode = slot.Mode,
                scheduledTime = slot.ScheduledTime ?? string.Empty,
                intervalValue = slot.IntervalValue,
                intervalUnit = slot.IntervalUnit,
                enabled = slot.Enabled,
                nextSendAt = slot.NextSendAt?.ToString("o"),
                nextLabel = FormatNextLabel(slot, now)
            }).ToList()
        };
    }

    /// <summary>Invoked by <see cref="SchedulerHeartbeat"/>.</summary>
    public void Tick()
    {
        if (_tickInProgress)
            return;

        _tickInProgress = true;
        try
        {
            DateTime now = DateTime.Now;
            for (int i = 0; i < _slots.Count; i++)
            {
                BroadcastMessageSlot slot = _slots[i];
                if (!slot.Enabled || string.IsNullOrWhiteSpace(slot.Message))
                    continue;

                if (slot.NextSendAt is null)
                {
                    if (!IsRecurring(slot) && TryParseLocal(slot.ScheduledTime, out DateTime scheduled))
                        slot.NextSendAt = scheduled;
                    else if (IsRecurring(slot))
                        slot.NextSendAt = now + GetInterval(slot);
                    else
                        continue;
                }

                if (slot.NextSendAt > now)
                    continue;

                BroadcastDue?.Invoke(i, slot);
            }
        }
        finally
        {
            _tickInProgress = false;
        }
    }

    public static bool IsRecurring(BroadcastMessageSlot slot) =>
        string.Equals(slot.Mode, "recurring", StringComparison.OrdinalIgnoreCase);

    public static TimeSpan GetInterval(BroadcastMessageSlot slot)
    {
        int value = Math.Max(1, slot.IntervalValue);
        return string.Equals(slot.IntervalUnit, "minutes", StringComparison.OrdinalIgnoreCase)
            ? TimeSpan.FromMinutes(value)
            : TimeSpan.FromHours(value);
    }

    public static bool TryParseLocal(string? value, out DateTime result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (DateTime.TryParse(value, out DateTime parsed))
        {
            result = DateTime.SpecifyKind(parsed, DateTimeKind.Local);
            return true;
        }

        return false;
    }

    private static string FormatNextLabel(BroadcastMessageSlot slot, DateTime now)
    {
        if (!slot.Enabled || slot.NextSendAt is null)
            return string.Empty;

        DateTime next = slot.NextSendAt.Value;
        if (next <= now)
            return "Next: due now";

        TimeSpan delta = next - now;
        if (delta.TotalMinutes < 1)
            return "Next: in under a minute";
        if (delta.TotalHours < 1)
            return $"Next: in {(int)Math.Ceiling(delta.TotalMinutes)} minutes";
        if (delta.TotalDays < 1)
        {
            int hours = (int)Math.Floor(delta.TotalHours);
            int mins = delta.Minutes;
            return mins > 0 ? $"Next: in {hours}h {mins}m" : $"Next: in {hours} hours";
        }

        return $"Next: {next:g}";
    }

    private static BroadcastMessageSlot CloneSlot(BroadcastMessageSlot source) => new()
    {
        Message = source.Message ?? string.Empty,
        Mode = source.Mode ?? "oneTime",
        ScheduledTime = source.ScheduledTime ?? string.Empty,
        IntervalValue = source.IntervalValue,
        IntervalUnit = source.IntervalUnit ?? "hours",
        Enabled = source.Enabled,
        NextSendAt = source.NextSendAt
    };
}
