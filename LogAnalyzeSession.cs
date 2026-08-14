using System.Text;

namespace ZomboidManager;

public sealed class LogAnalyzeSession
{
    public const long MaxParseBytes = 80L * 1024 * 1024;
    public const long HeaderScanBytes = 2L * 1024 * 1024;
    public const int ContextRadius = 5;
    public const int MaxPageSize = 400;

    public string FilePath { get; private set; } = "";
    public long FileSize { get; private set; }
    public bool Truncated { get; private set; }
    public string TruncationNote { get; private set; } = "";
    public IReadOnlyList<LogEntry> Entries => _entries;
    public IReadOnlyList<string> RawLines => _rawLines;
    public IReadOnlyList<string> Categories => _categories;
    public int ErrorCount { get; private set; }
    public int WarnCount { get; private set; }
    public int LogCount { get; private set; }

    private List<LogEntry> _entries = new();
    private List<string> _rawLines = new();
    private List<string> _categories = new();
    private List<LogSystemSpec> _systemSpecs = new();
    private List<LogLoadedMod> _mods = new();
    private List<LogFileOverride> _overrides = new();

    public static LogAnalyzeSession Load(
        string path,
        AppConfig config,
        IReadOnlyList<ConfiguredModRef> configuredMods,
        CancellationToken ct,
        Action<int>? onProgress = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("Log file not found.", path);

        string full = System.IO.Path.GetFullPath(path);
        if (!LogViewer.IsAllowedLogPath(full, config))
            throw new InvalidOperationException("Path is outside configured log folders.");
        if (!LogViewer.IsImportantLogFile(full) && !LogViewer.IsConsoleLogFile(full))
            throw new InvalidOperationException("This file is not an allowed log type.");

        var session = new LogAnalyzeSession { FilePath = full };
        var info = new FileInfo(full);
        session.FileSize = info.Length;

        using FileStream stream = new(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var lines = new List<string>();
        var headerLines = new List<string>();

        if (stream.Length <= MaxParseBytes)
        {
            ReadAllLines(stream, lines, ct, onProgress, stream.Length);
        }
        else
        {
            session.Truncated = true;
            session.TruncationNote =
                $"File is {FormatMb(stream.Length)} — parsed last {FormatMb(MaxParseBytes)} plus header scan.";

            long headerLen = Math.Min(HeaderScanBytes, stream.Length);
            ReadBytesAsLines(stream, 0, headerLen, headerLines, ct, null);

            long bodyStart = stream.Length - MaxParseBytes;
            ReadBytesAsLines(stream, bodyStart, stream.Length - bodyStart, lines, ct, onProgress);
        }

        ct.ThrowIfCancellationRequested();
        session._rawLines = lines;
        session._entries = PzLogParser.ParseLines(lines, ct);
        session.Tally();

        IEnumerable<LogEntry> headerEntries = session._entries;
        if (headerLines.Count > 0)
            headerEntries = PzLogParser.ParseLines(headerLines, ct);

        session._systemSpecs = PzLogParser.ExtractSystemInfo(headerEntries);
        (session._mods, session._overrides) = PzLogParser.ExtractModInfo(headerEntries, configuredMods);
        if (headerLines.Count > 0)
        {
            var extraMods = PzLogParser.ExtractModInfo(session._entries, configuredMods);
            MergeMods(session._mods, extraMods.Mods);
            session._overrides.AddRange(extraMods.Overrides);
        }

        session._categories = session._entries
            .Select(e => string.IsNullOrWhiteSpace(e.Category) ? "Other" : e.Category)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        onProgress?.Invoke(100);
        return session;
    }

    public object Query(string level, string category, string search, string sortBy, string sortDir, int offset, int limit)
    {
        limit = Math.Clamp(limit <= 0 ? 150 : limit, 1, MaxPageSize);
        offset = Math.Max(0, offset);
        string levelFilter = (level ?? "").Trim().ToUpperInvariant();
        if (levelFilter is "ALL")
            levelFilter = "";
        string categoryFilter = (category ?? "").Trim();
        if (categoryFilter.Equals("All", StringComparison.OrdinalIgnoreCase))
            categoryFilter = "";
        string needle = (search ?? "").Trim();

        IEnumerable<LogEntry> q = _entries;
        if (levelFilter is "LOG" or "WARN" or "ERROR")
            q = q.Where(e => string.Equals(e.Level, levelFilter, StringComparison.OrdinalIgnoreCase));
        if (categoryFilter.Length > 0)
            q = q.Where(e => string.Equals(e.Category, categoryFilter, StringComparison.OrdinalIgnoreCase));
        if (needle.Length > 0)
            q = q.Where(e => (e.Message?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
                             || (e.Raw?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));

        q = ApplySort(q, sortBy, sortDir);
        var filtered = q.ToList();
        var page = filtered.Skip(offset).Take(limit).Select(ToRow).ToList();

        return new
        {
            total = filtered.Count,
            offset,
            limit,
            rows = page
        };
    }

    public object GetContext(int entryIndex)
    {
        if (entryIndex < 0 || entryIndex >= _entries.Count)
            return new { success = false, message = "Entry not found." };

        LogEntry entry = _entries[entryIndex];
        int from = Math.Max(1, entry.StartLine - ContextRadius);
        int to = Math.Min(_rawLines.Count, entry.EndLine + ContextRadius);
        var lines = new List<object>();
        for (int n = from; n <= to; n++)
        {
            bool inEntry = n >= entry.StartLine && n <= entry.EndLine;
            bool selected = n == entry.StartLine;
            lines.Add(new
            {
                number = n,
                text = n - 1 < _rawLines.Count ? _rawLines[n - 1] : "",
                inEntry,
                selected
            });
        }

        return new
        {
            success = true,
            entryIndex,
            startLine = entry.StartLine,
            endLine = entry.EndLine,
            raw = entry.Raw,
            message = entry.Message,
            lines
        };
    }

    public object GetSystemInfo() => new
    {
        success = true,
        specs = _systemSpecs.Select(s => new { label = s.Label, value = s.Value }).ToList(),
        empty = _systemSpecs.Count == 0
    };

    public object GetModInfo() => new
    {
        success = true,
        mods = _mods.Select(m => new
        {
            id = m.Id,
            displayName = m.DisplayName,
            workshopId = m.WorkshopId
        }).ToList(),
        overrides = _overrides.Select(o => new
        {
            modId = o.ModId,
            displayName = o.DisplayName,
            path = o.Path
        }).ToList(),
        empty = _mods.Count == 0 && _overrides.Count == 0
    };

    public object Meta() => new
    {
        success = true,
        path = FilePath,
        fileName = System.IO.Path.GetFileName(FilePath),
        sizeBytes = FileSize,
        sizeLabel = LogViewer.FormatSize(FileSize),
        truncated = Truncated,
        truncationNote = TruncationNote,
        entryCount = _entries.Count,
        errorCount = ErrorCount,
        warnCount = WarnCount,
        logCount = LogCount,
        categories = _categories,
        hasSystemInfo = _systemSpecs.Count > 0,
        hasModInfo = _mods.Count > 0 || _overrides.Count > 0
    };

    private void Tally()
    {
        foreach (LogEntry e in _entries)
        {
            if (e.Level == "ERROR") ErrorCount++;
            else if (e.Level == "WARN") WarnCount++;
            else LogCount++;
        }
    }

    private static object ToRow(LogEntry e)
    {
        string preview = e.Message ?? "";
        int nl = preview.IndexOf('\n');
        if (nl >= 0)
            preview = preview[..nl];
        if (preview.Length > 240)
            preview = preview[..240] + "…";

        return new
        {
            index = e.Index,
            startLine = e.StartLine,
            timestamp = e.Timestamp,
            level = e.Level,
            category = e.Category,
            preview,
            message = e.Message,
            raw = e.Raw
        };
    }

    private static IEnumerable<LogEntry> ApplySort(IEnumerable<LogEntry> q, string sortBy, string sortDir)
    {
        bool desc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
        return (sortBy ?? "").ToLowerInvariant() switch
        {
            "level" => desc
                ? q.OrderByDescending(LevelRank).ThenBy(e => e.Index)
                : q.OrderBy(LevelRank).ThenBy(e => e.Index),
            "category" => desc
                ? q.OrderByDescending(e => e.Category, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Index)
                : q.OrderBy(e => e.Category, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Index),
            "message" => desc
                ? q.OrderByDescending(e => e.Message, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Index)
                : q.OrderBy(e => e.Message, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Index),
            _ => desc ? q.OrderByDescending(e => e.Index) : q.OrderBy(e => e.Index)
        };
    }

    private static int LevelRank(LogEntry e) => e.Level switch
    {
        "ERROR" => 0,
        "WARN" => 1,
        "LOG" => 2,
        _ => 3
    };

    private static void ReadAllLines(
        FileStream stream, List<string> lines, CancellationToken ct, Action<int>? onProgress, long totalBytes)
    {
        stream.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true);
        int lastPct = -1;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            ct.ThrowIfCancellationRequested();
            lines.Add(line);
            if (onProgress is not null && totalBytes > 0)
            {
                int pct = (int)Math.Clamp(stream.Position * 90.0 / totalBytes, 0, 90);
                if (pct != lastPct && pct % 5 == 0)
                {
                    lastPct = pct;
                    onProgress(pct);
                }
            }
        }
    }

    private static void ReadBytesAsLines(
        FileStream stream, long start, long length, List<string> lines, CancellationToken ct, Action<int>? onProgress)
    {
        start = Math.Max(0, start);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: start == 0, bufferSize: 64 * 1024, leaveOpen: true);
        if (start > 0)
            reader.ReadLine(); // drop partial first line

        long end = start + length;
        int lastPct = -1;
        string? line;
        while (stream.Position <= end && (line = reader.ReadLine()) is not null)
        {
            ct.ThrowIfCancellationRequested();
            lines.Add(line);
            if (onProgress is not null && length > 0)
            {
                int pct = (int)Math.Clamp((stream.Position - start) * 90.0 / length, 0, 90);
                if (pct != lastPct && pct % 5 == 0)
                {
                    lastPct = pct;
                    onProgress(pct);
                }
            }
        }
    }

    private static void MergeMods(List<LogLoadedMod> into, List<LogLoadedMod> extra)
    {
        var seen = new HashSet<string>(into.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        foreach (LogLoadedMod mod in extra)
        {
            if (seen.Add(mod.Id))
                into.Add(mod);
        }
    }

    private static string FormatMb(long bytes) => $"{bytes / (1024.0 * 1024.0):0.#} MB";
}
