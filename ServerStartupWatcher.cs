namespace ZomboidManager;

/// <summary>
/// Waits until Project Zomboid reports a finished boot via a log line containing
/// <c>SERVER STARTED</c> (case-insensitive; covers <c>*** SERVER STARTED ****</c> variants).
/// The manager launches the server in a separate console without stdout redirect, so this watches
/// DebugLog-server / console log files under the server and Zomboid data folders, plus any lines
/// forwarded through <see cref="ObserveLine"/>.
/// </summary>
internal sealed class ServerStartupWatcher
{
    /// <summary>
    /// Substring used in timeout/UI messages. Matching uses <see cref="LineContainsStartedMarker"/>
    /// (case-insensitive <c>SERVER STARTED</c>) so three/four trailing asterisks both work.
    /// </summary>
    public const string StartedMarker = "*** SERVER STARTED ***";

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(20);

    private readonly object _gate = new();
    private readonly TaskCompletionSource<bool> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DateTime _watchStartedUtc = DateTime.UtcNow;
    private bool _completed;

    public static bool LineContainsStartedMarker(string? line) =>
        !string.IsNullOrEmpty(line)
        && line.Contains("SERVER STARTED", StringComparison.OrdinalIgnoreCase);

    public void ObserveLine(string? line)
    {
        if (!LineContainsStartedMarker(line))
            return;
        TryComplete(true);
    }

    public async Task<bool> WaitAsync(
        IEnumerable<string> logRoots,
        TimeSpan timeout,
        CancellationToken ct,
        Action<string>? log = null)
    {
        string[] roots = (logRoots ?? Array.Empty<string>())
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var positions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        SeedExistingFiles(roots, positions);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);

        try
        {
            while (!linked.Token.IsCancellationRequested)
            {
                if (_tcs.Task.IsCompleted)
                    return await _tcs.Task;

                ScanLogs(roots, positions, log);
                if (_tcs.Task.IsCompleted)
                    return await _tcs.Task;

                await Task.Delay(1000, linked.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // timeout or external cancel
        }

        if (_tcs.Task.IsCompleted)
            return await _tcs.Task;

        TryComplete(false);
        return false;
    }

    private void TryComplete(bool found)
    {
        lock (_gate)
        {
            if (_completed)
                return;
            _completed = true;
        }

        _tcs.TrySetResult(found);
    }

    private void SeedExistingFiles(IEnumerable<string> roots, Dictionary<string, long> positions)
    {
        foreach (string file in EnumerateCandidateFiles(roots))
        {
            try
            {
                positions[file] = new FileInfo(file).Length;
            }
            catch
            {
                // ignore inaccessible files — ScanLogs will treat late-discovered
                // pre-existing files as historical (start at EOF), not read from 0.
            }
        }
    }

    private void ScanLogs(
        IEnumerable<string> roots,
        Dictionary<string, long> positions,
        Action<string>? log)
    {
        foreach (string file in EnumerateCandidateFiles(roots))
        {
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists)
                    continue;

                if (!positions.TryGetValue(file, out long offset))
                {
                    // First sight of this path during this watch.
                    // Pre-existing logs often contain an old SERVER STARTED — skip to EOF.
                    // Only session logs created after we armed the watch are read from the start.
                    offset = IsPreExistingLog(info) ? info.Length : 0L;
                    positions[file] = offset;
                }

                long length = info.Length;
                if (length < offset)
                {
                    // File truncated / rotated
                    offset = 0;
                    positions[file] = 0;
                }

                if (length == offset)
                    continue;

                using var stream = new FileStream(
                    file,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                stream.Seek(offset, SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (!LineContainsStartedMarker(line))
                        continue;

                    log?.Invoke($"Detected server ready marker in {Path.GetFileName(file)}.");
                    TryComplete(true);
                    positions[file] = stream.Position;
                    return;
                }

                positions[file] = stream.Position;
            }
            catch
            {
                // file locked or deleted — retry next tick
            }
        }
    }

    /// <summary>
    /// True if the file existed before this watch (or was last written before arming).
    /// Small clock skew allowance avoids racing a brand-new session log.
    /// </summary>
    private bool IsPreExistingLog(FileInfo info)
    {
        DateTime threshold = _watchStartedUtc.AddSeconds(-2);
        try
        {
            if (info.CreationTimeUtc < threshold)
                return true;
            if (info.LastWriteTimeUtc < threshold && info.Length > 0)
                return true;
        }
        catch
        {
            // If timestamps are unavailable, treat as historical to avoid false Discord pings.
            return true;
        }

        return false;
    }

    private static IEnumerable<string> EnumerateCandidateFiles(IEnumerable<string> roots)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            // Top-level console dumps (Zomboid data folder).
            foreach (string name in new[] { "console.txt", "server-console.txt" })
            {
                string path = Path.Combine(root, name);
                if (File.Exists(path) && seen.Add(path))
                    yield return path;
            }

            // Dedicated server session logs live in .../logs or .../Logs.
            foreach (string sub in new[] { "logs", "Logs" })
            {
                string dir = Path.Combine(root, sub);
                if (!Directory.Exists(dir))
                    continue;

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(dir, "*.txt", SearchOption.AllDirectories);
                }
                catch
                {
                    continue;
                }

                foreach (string file in files)
                {
                    if (!IsCandidateLogName(Path.GetFileName(file)))
                        continue;
                    if (!seen.Add(file))
                        continue;
                    yield return file;
                }
            }
        }
    }

    private static bool IsCandidateLogName(string fileName)
    {
        if (fileName.Equals("console.txt", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("server-console.txt", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fileName.Contains("DebugLog-server", StringComparison.OrdinalIgnoreCase)
            || (fileName.Contains("DebugLog", StringComparison.OrdinalIgnoreCase)
                && fileName.Contains("server", StringComparison.OrdinalIgnoreCase)
                && fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
    }
}
