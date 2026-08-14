using Microsoft.Data.Sqlite;

namespace ZomboidManager;

public static class RestartReasons
{
    public const string Scheduled = "scheduled";
    public const string Manual = "manual";
    public const string ModUpdate = "mod-update";
    public const string CrashRecovery = "crash-recovery";
}

public sealed class StatsSample
{
    public long Ts { get; init; }
    public int? PlayerCount { get; init; }
    public double CpuPct { get; init; }
    public double RamPct { get; init; }
    public long DiskFreeBytes { get; init; }
}

public sealed class StatsRestartEvent
{
    public long Ts { get; init; }
    public string Reason { get; init; } = "";
}

public sealed class StatsSession
{
    public long StartTs { get; init; }
    public long? EndTs { get; init; }
}

public sealed class StatsDashboard
{
    public string Range { get; init; } = "24h";
    public long FromTs { get; init; }
    public long ToTs { get; init; }
    public int IntervalMinutes { get; init; }
    public int RetentionDays { get; init; }
    public int SampleCount { get; init; }
    public IReadOnlyList<StatsSample> Samples { get; init; } = Array.Empty<StatsSample>();
    public IReadOnlyList<StatsRestartEvent> Restarts { get; init; } = Array.Empty<StatsRestartEvent>();
    public StatsUptime Uptime { get; init; } = new();
}

public sealed class StatsUptime
{
    public bool HasData { get; init; }
    public double Percent { get; init; }
    public double ExpectedHours { get; init; }
    public double ActualHours { get; init; }
    public double UnexpectedDowntimeMinutes { get; init; }
    public double PlannedDowntimeMinutes { get; init; }
}

/// <summary>
/// Rolling SQLite time-series store at %LocalAppData%\ZomboidManager\stats_history.db.
/// </summary>
public sealed class StatsHistoryStore : IDisposable
{
    public const int DefaultIntervalMinutes = 2;
    public const int DefaultRetentionDays = 30;
    public const int MinIntervalMinutes = 1;
    public const int MaxIntervalMinutes = 5;
    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 365;

    private static readonly TimeSpan PlannedRestartWindow = TimeSpan.FromMinutes(5);

    private readonly string _dbPath;
    private readonly object _gate = new();
    private SqliteConnection? _conn;
    private bool _disposed;
    private long _lastPruneTs;

    public StatsHistoryStore(string? dbPath = null)
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZomboidManager");
        Directory.CreateDirectory(dir);
        _dbPath = dbPath ?? Path.Combine(dir, "stats_history.db");
        EnsureParentDirectory();
        Open();
    }

    public string DbPath => _dbPath;

    public static int ClampIntervalMinutes(int minutes) =>
        Math.Clamp(minutes, MinIntervalMinutes, MaxIntervalMinutes);

    public static int ClampRetentionDays(int days) =>
        Math.Clamp(days, MinRetentionDays, MaxRetentionDays);

    public static long ToUnixMs(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    public static DateTime FromUnixMs(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    public void AddSample(DateTime utc, int? playerCount, double cpuPct, double ramPct, long diskFreeBytes)
    {
        ThrowIfDisposed();
        long ts = ToUnixMs(utc);
        lock (_gate)
        {
            EnsureOpen();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText =
                """
                INSERT OR REPLACE INTO samples (ts, player_count, cpu_pct, ram_pct, disk_free_bytes)
                VALUES ($ts, $players, $cpu, $ram, $disk)
                """;
            cmd.Parameters.AddWithValue("$ts", ts);
            cmd.Parameters.AddWithValue("$players", playerCount.HasValue ? playerCount.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("$cpu", cpuPct);
            cmd.Parameters.AddWithValue("$ram", ramPct);
            cmd.Parameters.AddWithValue("$disk", diskFreeBytes);
            cmd.ExecuteNonQuery();
        }
    }

    public void AddRestart(DateTime utc, string reason)
    {
        ThrowIfDisposed();
        string normalized = NormalizeReason(reason);
        long ts = ToUnixMs(utc);
        lock (_gate)
        {
            EnsureOpen();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "INSERT INTO restarts (ts, reason) VALUES ($ts, $reason)";
            cmd.Parameters.AddWithValue("$ts", ts);
            cmd.Parameters.AddWithValue("$reason", normalized);
            cmd.ExecuteNonQuery();
        }
    }

    public void NoteOnline(DateTime utc)
    {
        ThrowIfDisposed();
        long ts = ToUnixMs(utc);
        lock (_gate)
        {
            EnsureOpen();
            using var check = _conn!.CreateCommand();
            check.CommandText = "SELECT id FROM sessions WHERE end_ts IS NULL ORDER BY start_ts DESC LIMIT 1";
            object? open = check.ExecuteScalar();
            if (open is not null and not DBNull)
                return;

            using var insert = _conn.CreateCommand();
            insert.CommandText = "INSERT INTO sessions (start_ts, end_ts) VALUES ($ts, NULL)";
            insert.Parameters.AddWithValue("$ts", ts);
            insert.ExecuteNonQuery();
        }
    }

    public void NoteOffline(DateTime utc)
    {
        ThrowIfDisposed();
        long ts = ToUnixMs(utc);
        lock (_gate)
        {
            EnsureOpen();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText =
                """
                UPDATE sessions
                SET end_ts = $ts
                WHERE id = (SELECT id FROM sessions WHERE end_ts IS NULL ORDER BY start_ts DESC LIMIT 1)
                """;
            cmd.Parameters.AddWithValue("$ts", ts);
            cmd.ExecuteNonQuery();
        }
    }

    public void PruneOlderThan(TimeSpan retention)
    {
        ThrowIfDisposed();
        long cutoff = ToUnixMs(DateTime.UtcNow - retention);
        lock (_gate)
        {
            EnsureOpen();
            using var tx = _conn!.BeginTransaction();
            ExecuteNonQuery(tx, "DELETE FROM samples WHERE ts < $c", cutoff);
            ExecuteNonQuery(tx, "DELETE FROM restarts WHERE ts < $c", cutoff);
            ExecuteNonQuery(tx, "DELETE FROM sessions WHERE COALESCE(end_ts, start_ts) < $c", cutoff);
            tx.Commit();
            _lastPruneTs = ToUnixMs(DateTime.UtcNow);
            using var vacuum = _conn.CreateCommand();
            vacuum.CommandText = "PRAGMA incremental_vacuum(64)";
            vacuum.ExecuteNonQuery();
        }
    }

    public bool ShouldPrune(TimeSpan minInterval)
    {
        if (_lastPruneTs == 0)
            return true;
        return DateTime.UtcNow - FromUnixMs(_lastPruneTs) >= minInterval;
    }

    public StatsDashboard QueryDashboard(string range, int intervalMinutes, int retentionDays)
    {
        ThrowIfDisposed();
        DateTime now = DateTime.UtcNow;
        TimeSpan window = ParseRange(range);
        long toTs = ToUnixMs(now);
        long fromTs = ToUnixMs(now - window);
        long bucketMs = ResolveBucketMs(range);

        List<StatsSample> samples;
        List<StatsRestartEvent> restarts;
        List<StatsSession> sessions;
        long? trackingStart;

        lock (_gate)
        {
            EnsureOpen();
            samples = ReadSamples(fromTs, toTs, bucketMs);
            restarts = ReadRestarts(fromTs, toTs);
            sessions = ReadSessions(fromTs, toTs);
            trackingStart = ReadTrackingStart();
        }

        StatsUptime uptime = ComputeUptime(fromTs, toTs, trackingStart, sessions, restarts);

        return new StatsDashboard
        {
            Range = NormalizeRange(range),
            FromTs = fromTs,
            ToTs = toTs,
            IntervalMinutes = ClampIntervalMinutes(intervalMinutes),
            RetentionDays = ClampRetentionDays(retentionDays),
            SampleCount = samples.Count,
            Samples = samples,
            Restarts = restarts,
            Uptime = uptime
        };
    }

    public object ToPayload(StatsDashboard dash)
    {
        return new
        {
            range = dash.Range,
            fromTs = dash.FromTs,
            toTs = dash.ToTs,
            intervalMinutes = dash.IntervalMinutes,
            retentionDays = dash.RetentionDays,
            sampleCount = dash.SampleCount,
            dbPath = _dbPath,
            samples = dash.Samples.Select(s => new
            {
                t = s.Ts,
                players = s.PlayerCount,
                cpu = s.CpuPct,
                ram = s.RamPct,
                diskFreeBytes = s.DiskFreeBytes,
                diskFreeGb = Math.Round(s.DiskFreeBytes / (1024.0 * 1024 * 1024), 1)
            }),
            restarts = dash.Restarts.Select(r => new
            {
                t = r.Ts,
                reason = r.Reason,
                at = FromUnixMs(r.Ts).ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss")
            }),
            uptime = new
            {
                hasData = dash.Uptime.HasData,
                percent = dash.Uptime.Percent,
                expectedHours = dash.Uptime.ExpectedHours,
                actualHours = dash.Uptime.ActualHours,
                unexpectedDowntimeMinutes = dash.Uptime.UnexpectedDowntimeMinutes,
                plannedDowntimeMinutes = dash.Uptime.PlannedDowntimeMinutes
            }
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (_gate)
        {
            _conn?.Dispose();
            _conn = null;
        }
    }

    public static void RunSelfTest()
    {
        string dir = Path.Combine(Path.GetTempPath(), "zm-stats-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string db = Path.Combine(dir, "stats_history.db");
        try
        {
            using var store = new StatsHistoryStore(db);
            DateTime t0 = DateTime.UtcNow.AddHours(-6);

            store.NoteOnline(t0);
            for (int i = 0; i < 12; i++)
            {
                store.AddSample(
                    t0.AddMinutes(i * 2),
                    playerCount: 3 + (i % 4),
                    cpuPct: 20 + i,
                    ramPct: 40 + i * 0.5,
                    diskFreeBytes: 120L * 1024 * 1024 * 1024);
            }

            int afterFirstBatch = store.QueryDashboard("24h", 2, 30).Samples.Count;

            store.AddRestart(t0.AddHours(1), RestartReasons.Scheduled);
            store.NoteOffline(t0.AddHours(1));
            store.NoteOnline(t0.AddHours(1).AddMinutes(3));
            store.AddSample(t0.AddHours(1).AddMinutes(4), 2, 18, 41, 119L * 1024 * 1024 * 1024);
            store.AddRestart(t0.AddHours(2), RestartReasons.Manual);
            store.AddRestart(t0.AddHours(3), RestartReasons.ModUpdate);
            store.AddRestart(t0.AddHours(4), RestartReasons.CrashRecovery);
            store.NoteOffline(t0.AddHours(4));
            store.NoteOnline(t0.AddHours(4).AddMinutes(8));

            store.AddSample(t0.AddDays(-40), 1, 10, 10, 100);
            store.PruneOlderThan(TimeSpan.FromDays(30));

            StatsDashboard dash = store.QueryDashboard("24h", 2, 30);

            bool samplesOk = dash.Samples.Count == afterFirstBatch + 1 && afterFirstBatch == 12;
            bool pruneOk;
            lock (store._gate)
            {
                store.EnsureOpen();
                using var cmd = store._conn!.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM samples WHERE ts < $c";
                cmd.Parameters.AddWithValue("$c", ToUnixMs(DateTime.UtcNow - TimeSpan.FromDays(30)));
                long leftover = Convert.ToInt64(cmd.ExecuteScalar());
                pruneOk = leftover == 0;
            }

            using var verify = new StatsHistoryStore(db);
            bool restartCount;
            lock (verify._gate)
            {
                verify.EnsureOpen();
                using var cmd = verify._conn!.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM restarts";
                restartCount = Convert.ToInt64(cmd.ExecuteScalar()) == 4;
            }

            bool reasonsOk;
            lock (store._gate)
            {
                store.EnsureOpen();
                using var cmd = store._conn!.CreateCommand();
                cmd.CommandText = "SELECT reason FROM restarts ORDER BY ts";
                var reasons = new List<string>();
                using SqliteDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                    reasons.Add(reader.GetString(0));
                reasonsOk = reasons.SequenceEqual(new[]
                {
                    RestartReasons.Scheduled,
                    RestartReasons.Manual,
                    RestartReasons.ModUpdate,
                    RestartReasons.CrashRecovery
                });
            }

            var uptimeSessions = new List<StatsSession>
            {
                new() { StartTs = 0, EndTs = 3_600_000 }
            };
            StatsUptime uptime = ComputeUptime(
                0,
                7_200_000,
                0,
                uptimeSessions,
                Array.Empty<StatsRestartEvent>());
            bool uptimeOk = uptime.HasData && Math.Abs(uptime.Percent - 50) < 0.2;

            bool ok = samplesOk && pruneOk && restartCount && reasonsOk && uptimeOk && File.Exists(db);

            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                ok,
                samplesOk,
                pruneOk,
                restartCount,
                reasonsOk,
                uptimeOk,
                db,
                sampleCount = dash.Samples.Count,
                afterFirstBatch,
                uptimeHasData = dash.Uptime.HasData
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Environment.Exit(ok ? 0 : 1);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    private void Open()
    {
        lock (_gate)
        {
            EnsureOpen();
        }
    }

    private void EnsureParentDirectory()
    {
        string? parent = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(parent))
            Directory.CreateDirectory(parent);
    }

    private void EnsureOpen()
    {
        if (_conn is { State: System.Data.ConnectionState.Open })
            return;

        _conn?.Dispose();
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        _conn = new SqliteConnection(builder.ToString());
        _conn.Open();
        using (var pragma = _conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }
        using (var pragma = _conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();
        }
        using (var pragma = _conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout=4000;";
            pragma.ExecuteNonQuery();
        }
        using (var pragma = _conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA auto_vacuum=INCREMENTAL;";
            pragma.ExecuteNonQuery();
        }

        using (var samples = _conn.CreateCommand())
        {
            samples.CommandText =
                """
                CREATE TABLE IF NOT EXISTS samples (
                  ts INTEGER NOT NULL PRIMARY KEY,
                  player_count INTEGER,
                  cpu_pct REAL NOT NULL,
                  ram_pct REAL NOT NULL,
                  disk_free_bytes INTEGER NOT NULL
                )
                """;
            samples.ExecuteNonQuery();
        }
        using (var restarts = _conn.CreateCommand())
        {
            restarts.CommandText =
                """
                CREATE TABLE IF NOT EXISTS restarts (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  ts INTEGER NOT NULL,
                  reason TEXT NOT NULL
                )
                """;
            restarts.ExecuteNonQuery();
        }
        using (var ixRestarts = _conn.CreateCommand())
        {
            ixRestarts.CommandText = "CREATE INDEX IF NOT EXISTS ix_restarts_ts ON restarts(ts)";
            ixRestarts.ExecuteNonQuery();
        }
        using (var sessions = _conn.CreateCommand())
        {
            sessions.CommandText =
                """
                CREATE TABLE IF NOT EXISTS sessions (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  start_ts INTEGER NOT NULL,
                  end_ts INTEGER
                )
                """;
            sessions.ExecuteNonQuery();
        }
        using (var ixSessions = _conn.CreateCommand())
        {
            ixSessions.CommandText = "CREATE INDEX IF NOT EXISTS ix_sessions_start ON sessions(start_ts)";
            ixSessions.ExecuteNonQuery();
        }
    }

    private List<StatsSample> ReadSamples(long fromTs, long toTs, long bucketMs)
    {
        var result = new List<StatsSample>();
        using var cmd = _conn!.CreateCommand();
        if (bucketMs <= 0)
        {
            cmd.CommandText =
                """
                SELECT ts, player_count, cpu_pct, ram_pct, disk_free_bytes
                FROM samples
                WHERE ts >= $from AND ts <= $to
                ORDER BY ts
                """;
        }
        else
        {
            cmd.CommandText =
                """
                SELECT
                  (ts / $bucket) * $bucket AS bucket_ts,
                  AVG(player_count),
                  AVG(cpu_pct),
                  AVG(ram_pct),
                  AVG(disk_free_bytes)
                FROM samples
                WHERE ts >= $from AND ts <= $to
                GROUP BY bucket_ts
                ORDER BY bucket_ts
                """;
            cmd.Parameters.AddWithValue("$bucket", bucketMs);
        }

        cmd.Parameters.AddWithValue("$from", fromTs);
        cmd.Parameters.AddWithValue("$to", toTs);
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int? players = reader.IsDBNull(1) ? null : Convert.ToInt32(Math.Round(Convert.ToDouble(reader.GetValue(1))));
            result.Add(new StatsSample
            {
                Ts = Convert.ToInt64(reader.GetValue(0)),
                PlayerCount = players,
                CpuPct = Math.Round(Convert.ToDouble(reader.GetValue(2)), 1),
                RamPct = Math.Round(Convert.ToDouble(reader.GetValue(3)), 1),
                DiskFreeBytes = Convert.ToInt64(Math.Round(Convert.ToDouble(reader.GetValue(4))))
            });
        }

        return result;
    }

    private List<StatsRestartEvent> ReadRestarts(long fromTs, long toTs)
    {
        var result = new List<StatsRestartEvent>();
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText =
            """
            SELECT ts, reason FROM restarts
            WHERE ts >= $from AND ts <= $to
            ORDER BY ts DESC
            """;
        cmd.Parameters.AddWithValue("$from", fromTs);
        cmd.Parameters.AddWithValue("$to", toTs);
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new StatsRestartEvent
            {
                Ts = reader.GetInt64(0),
                Reason = reader.GetString(1)
            });
        }

        return result;
    }

    private List<StatsSession> ReadSessions(long fromTs, long toTs)
    {
        var result = new List<StatsSession>();
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText =
            """
            SELECT start_ts, end_ts FROM sessions
            WHERE start_ts <= $to AND COALESCE(end_ts, $to) >= $from
            ORDER BY start_ts
            """;
        cmd.Parameters.AddWithValue("$from", fromTs);
        cmd.Parameters.AddWithValue("$to", toTs);
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new StatsSession
            {
                StartTs = reader.GetInt64(0),
                EndTs = reader.IsDBNull(1) ? null : reader.GetInt64(1)
            });
        }

        return result;
    }

    private long? ReadTrackingStart()
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText =
            """
            SELECT MIN(ts) FROM (
              SELECT MIN(ts) AS ts FROM samples
              UNION ALL
              SELECT MIN(ts) FROM restarts
              UNION ALL
              SELECT MIN(start_ts) FROM sessions
            )
            """;
        object? value = cmd.ExecuteScalar();
        if (value is null or DBNull)
            return null;
        long ts = Convert.ToInt64(value);
        return ts > 0 ? ts : null;
    }

    internal static StatsUptime ComputeUptime(
        long fromTs,
        long toTs,
        long? trackingStart,
        IReadOnlyList<StatsSession> sessions,
        IReadOnlyList<StatsRestartEvent> restarts)
    {
        if (trackingStart is null || toTs <= fromTs)
            return new StatsUptime();

        long effectiveFrom = Math.Max(fromTs, trackingStart.Value);
        if (effectiveFrom >= toTs)
            return new StatsUptime();

        long expected = toTs - effectiveFrom;
        long actual = 0;
        var clipped = new List<(long start, long end)>();
        foreach (StatsSession session in sessions)
        {
            long start = Math.Max(session.StartTs, effectiveFrom);
            long end = Math.Min(session.EndTs ?? toTs, toTs);
            if (end <= start)
                continue;
            actual += end - start;
            clipped.Add((start, end));
        }

        clipped.Sort((a, b) => a.start.CompareTo(b.start));

        var planned = restarts
            .Where(r => r.Reason is RestartReasons.Scheduled or RestartReasons.Manual or RestartReasons.ModUpdate)
            .Select(r => r.Ts)
            .ToList();

        long plannedMs = 0;
        long unexpectedMs = 0;
        long cursor = effectiveFrom;
        foreach ((long start, long end) in clipped)
        {
            if (start > cursor)
            {
                ClassifyGap(cursor, start, planned, ref plannedMs, ref unexpectedMs);
            }

            cursor = Math.Max(cursor, end);
        }

        if (cursor < toTs)
            ClassifyGap(cursor, toTs, planned, ref plannedMs, ref unexpectedMs);

        actual = Math.Clamp(actual, 0, expected);
        double percent = expected > 0 ? Math.Round(actual * 100.0 / expected, 1) : 0;

        return new StatsUptime
        {
            HasData = true,
            Percent = percent,
            ExpectedHours = Math.Round(expected / 3_600_000.0, 2),
            ActualHours = Math.Round(actual / 3_600_000.0, 2),
            UnexpectedDowntimeMinutes = Math.Round(unexpectedMs / 60_000.0, 1),
            PlannedDowntimeMinutes = Math.Round(plannedMs / 60_000.0, 1)
        };
    }

    private static void ClassifyGap(
        long gapStart,
        long gapEnd,
        List<long> plannedRestarts,
        ref long plannedMs,
        ref long unexpectedMs)
    {
        long duration = gapEnd - gapStart;
        if (duration <= 0)
            return;

        long window = (long)PlannedRestartWindow.TotalMilliseconds;
        bool planned = plannedRestarts.Any(ts => ts >= gapStart - window && ts <= gapEnd + window);
        if (planned)
            plannedMs += duration;
        else
            unexpectedMs += duration;
    }

    private static void ExecuteNonQuery(SqliteTransaction tx, string sql, long cutoff)
    {
        using var cmd = tx.Connection!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$c", cutoff);
        cmd.ExecuteNonQuery();
    }

    private static string NormalizeReason(string reason)
    {
        string value = (reason ?? "").Trim().ToLowerInvariant();
        return value switch
        {
            RestartReasons.Scheduled => RestartReasons.Scheduled,
            RestartReasons.Manual => RestartReasons.Manual,
            RestartReasons.ModUpdate => RestartReasons.ModUpdate,
            RestartReasons.CrashRecovery => RestartReasons.CrashRecovery,
            "modupdate" or "mod_update" => RestartReasons.ModUpdate,
            "crash" or "crashrecovery" => RestartReasons.CrashRecovery,
            _ => string.IsNullOrWhiteSpace(value) ? RestartReasons.Manual : value
        };
    }

    private static string NormalizeRange(string range) =>
        range?.Trim().ToLowerInvariant() switch
        {
            "7d" or "7days" or "week" => "7d",
            "30d" or "30days" or "month" => "30d",
            _ => "24h"
        };

    private static TimeSpan ParseRange(string range) =>
        NormalizeRange(range) switch
        {
            "7d" => TimeSpan.FromDays(7),
            "30d" => TimeSpan.FromDays(30),
            _ => TimeSpan.FromHours(24)
        };

    private static long ResolveBucketMs(string range) =>
        NormalizeRange(range) switch
        {
            "7d" => 15 * 60 * 1000L,
            "30d" => 60 * 60 * 1000L,
            _ => 0
        };

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(StatsHistoryStore));
    }
}
