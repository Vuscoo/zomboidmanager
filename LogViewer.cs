using System.Text;

namespace ZomboidManager;

public static class LogViewer
{
    private static readonly string[] ImportantNameParts =
    [
        "DebugLog",
        "Debug",
        "console",
        "connections",
        "chat",
        "server",
        "cmd",
        "User",
        "Coop",
        "cooperative"
    ];

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt",
        ".log"
    };

    /// <summary>Max bytes returned when reading a log (tail).</summary>
    public const int MaxReadBytes = 512 * 1024;

    public static List<LogFileInfo> ListImportantLogs(AppConfig config)
    {
        var results = new List<LogFileInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddFromFolder(string folder, string sourceLabel)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return;

            // Top-level files
            foreach (string file in SafeEnumerateFiles(folder, SearchOption.TopDirectoryOnly))
                TryAdd(file, sourceLabel, seen, results);

            // One level of dated subfolders (logs_YYYY-MM-DD etc.), newest first
            IEnumerable<string> subDirs = SafeEnumerateDirectories(folder)
                .OrderByDescending(d => Directory.GetLastWriteTime(d));

            foreach (string dir in subDirs.Take(14))
            {
                foreach (string file in SafeEnumerateFiles(dir, SearchOption.TopDirectoryOnly))
                    TryAdd(file, sourceLabel, seen, results);
            }
        }

        // Zomboid user data: ...\Zomboid\Logs plus console.txt at the data root
        if (!string.IsNullOrWhiteSpace(config.ZomboidDataPath))
        {
            AddFromFolder(Path.Combine(config.ZomboidDataPath, "Logs"), "Zomboid Logs");
            AddFromFolder(Path.Combine(config.ZomboidDataPath, "logs"), "Zomboid Logs");
            TryAdd(Path.Combine(config.ZomboidDataPath, "console.txt"), "Zomboid Logs", seen, results);
            TryAdd(Path.Combine(config.ZomboidDataPath, "server-console.txt"), "Zomboid Logs", seen, results);
        }

        // Dedicated server install: ...\pzServer\logs
        if (!string.IsNullOrWhiteSpace(config.ServerPath))
        {
            AddFromFolder(Path.Combine(config.ServerPath, "logs"), "Server logs");
            AddFromFolder(Path.Combine(config.ServerPath, "Logs"), "Server logs");
        }

        return results
            .OrderByDescending(f => f.LastWriteTime)
            .Take(80)
            .ToList();
    }

    public static (bool success, string message, string content, bool truncated) ReadLogTail(string path, AppConfig config)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return (false, "Log file not found.", string.Empty, false);

            string full = Path.GetFullPath(path);
            if (!IsUnderAllowedRoot(full, config))
                return (false, "Path is outside configured log folders.", string.Empty, false);
            if (!IsImportantLogFile(full))
                return (false, "This file is not an allowed log type.", string.Empty, false);

            var info = new FileInfo(full);
            if (info.Length <= MaxReadBytes)
            {
                string text = File.ReadAllText(full, Encoding.UTF8);
                return (true, "OK", text, false);
            }

            using FileStream stream = new(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long start = Math.Max(0, stream.Length - MaxReadBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string tail = reader.ReadToEnd();
            string header = $"[Showing last {MaxReadBytes / 1024} KB of {info.Length / 1024} KB]\n\n";
            return (true, "OK", header + tail, true);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, string.Empty, false);
        }
    }

    public static bool IsImportantLogFile(string path)
    {
        string name = Path.GetFileName(path);
        string ext = Path.GetExtension(path);
        if (!AllowedExtensions.Contains(ext))
            return false;
        if (name.Contains(".tmp", StringComparison.OrdinalIgnoreCase))
            return false;
        if (name.Contains("map_", StringComparison.OrdinalIgnoreCase))
            return false;

        return ImportantNameParts.Any(part =>
            name.Contains(part, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsConsoleLogFile(string path)
    {
        string name = Path.GetFileName(path);
        return name.Equals("console.txt", StringComparison.OrdinalIgnoreCase)
               || name.Equals("server-console.txt", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAllowedLogPath(string fullPath, AppConfig config)
    {
        if (IsUnderAllowedRoot(fullPath, config))
            return true;

        if (!IsConsoleLogFile(fullPath) || string.IsNullOrWhiteSpace(config.ZomboidDataPath))
            return false;

        try
        {
            string dataRoot = Path.GetFullPath(config.ZomboidDataPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string dir = Path.GetDirectoryName(fullPath) ?? "";
            return string.Equals(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                dataRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / (1024.0 * 1024.0):0.#} MB";
    }

    public static bool IsUnderAllowedRoot(string fullPath, AppConfig config)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(config.ZomboidDataPath))
        {
            roots.Add(Path.Combine(config.ZomboidDataPath.Trim(), "Logs"));
            roots.Add(Path.Combine(config.ZomboidDataPath.Trim(), "logs"));
        }
        if (!string.IsNullOrWhiteSpace(config.ServerPath))
        {
            roots.Add(Path.Combine(config.ServerPath.Trim(), "logs"));
            roots.Add(Path.Combine(config.ServerPath.Trim(), "Logs"));
        }

        string normalizedFile;
        try
        {
            normalizedFile = Path.GetFullPath(fullPath);
        }
        catch
        {
            return false;
        }

        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                // Do not require Directory.Exists — the file may live in a root that
                // was listed moments ago; Exists can race with case-aliases on Windows.
                string rootFull = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                if (normalizedFile.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                // ignore
            }
        }

        return false;
    }

    private static void TryAdd(string file, string source, HashSet<string> seen, List<LogFileInfo> results)
    {
        if (!File.Exists(file))
            return;
        if (!IsImportantLogFile(file) && !IsConsoleLogFile(file))
            return;

        string full = Path.GetFullPath(file);
        if (!seen.Add(full))
            return;

        var info = new FileInfo(full);
        results.Add(new LogFileInfo
        {
            Path = full,
            Name = info.Name,
            Source = source,
            SizeBytes = info.Length,
            LastWriteTime = info.LastWriteTime
        });
    }

    private static IEnumerable<string> SafeEnumerateFiles(string folder, SearchOption option)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.*", option);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string folder)
    {
        try
        {
            return Directory.EnumerateDirectories(folder);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}

public sealed class LogFileInfo
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime LastWriteTime { get; set; }
}
