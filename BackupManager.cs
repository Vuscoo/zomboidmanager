using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace ZomboidManager;

public static class BackupManager
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] SkipDirNames = ["caches", "cache", "logs", "Logs"];

    /// <summary>Progress throttle: emit at most this often (wall clock).</summary>
    public static readonly TimeSpan ProgressMinInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Progress throttle: emit at most once per this many files (if sooner than interval).</summary>
    public const int ProgressFileBatch = 200;

    public static string GetBackupsRoot() =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backups");

    /// <summary>
    /// Creates a single .zip under Backups/ with Server configs + Multiplayer saves.
    /// Runs synchronously — call from a background Task. Supports cancel + throttled progress.
    /// </summary>
    public static BackupResult CreateBackup(
        AppConfig config,
        IProgress<BackupProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string zomboidData = config.ZomboidDataPath;
        if (string.IsNullOrWhiteSpace(zomboidData) || !Directory.Exists(zomboidData))
            return BackupResult.Fail("Zomboid data folder is not configured or missing.");

        string serverDir = Path.Combine(zomboidData, "Server");
        if (!Directory.Exists(serverDir))
            return BackupResult.Fail($"Server folder not found: {serverDir}");

        string? baseName = null;
        if (!string.IsNullOrWhiteSpace(config.LastIniFilePath) && File.Exists(config.LastIniFilePath))
            baseName = Path.GetFileNameWithoutExtension(config.LastIniFilePath);

        List<BackupFileEntry> entries = CollectEntries(serverDir, zomboidData, baseName);
        if (entries.Count == 0)
            return BackupResult.Fail("No important files found to backup. Check Zomboid data path / loaded INI.");

        Directory.CreateDirectory(GetBackupsRoot());
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        string zipPath = Path.Combine(GetBackupsRoot(), stamp + ".zip");
        string partialPath = zipPath + ".partial";

        TryDelete(partialPath);

        int total = entries.Count;
        int done = 0;
        var throttle = new ProgressThrottle(ProgressMinInterval, ProgressFileBatch);

        void Report(string current, bool force = false)
        {
            if (force || throttle.ShouldReport(done, force: force))
            {
                progress?.Report(new BackupProgressUpdate(done, total, current));
                throttle.MarkReported(done);
            }
        }

        Report("Preparing…", force: true);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using (var fs = new FileStream(
                       partialPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 128 * 1024,
                       FileOptions.SequentialScan))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (BackupFileEntry entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    ZipArchiveEntry zipEntry = archive.CreateEntry(entry.ZipPath, CompressionLevel.Fastest);
                    using (Stream src = File.Open(entry.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (Stream dest = zipEntry.Open())
                    {
                        src.CopyTo(dest, 128 * 1024);
                    }

                    done++;
                    Report(entry.ZipPath);
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Metadata sidecar inside the zip (same fields as the old folder backup).
                var info = new
                {
                    createdAt = DateTime.Now.ToString("o"),
                    zomboidDataPath = zomboidData,
                    lastIniFilePath = config.LastIniFilePath,
                    fileCount = entries.Count,
                    format = "zip"
                };
                ZipArchiveEntry meta = archive.CreateEntry("backup_info.json", CompressionLevel.Optimal);
                using (Stream metaStream = meta.Open())
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(info, JsonOptions));
                    metaStream.Write(bytes, 0, bytes.Length);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(zipPath))
                File.Delete(zipPath);
            File.Move(partialPath, zipPath);

            Report(Path.GetFileName(zipPath), force: true);
            return BackupResult.Ok(zipPath, entries.Count);
        }
        catch (OperationCanceledException)
        {
            TryDelete(partialPath);
            TryDelete(zipPath);
            return BackupResult.CancelledResult();
        }
        catch (Exception ex)
        {
            TryDelete(partialPath);
            TryDelete(zipPath);
            return BackupResult.Fail(ex.Message);
        }
    }

    public static List<BackupInfo> ListBackups()
    {
        string root = GetBackupsRoot();
        if (!Directory.Exists(root))
            return new List<BackupInfo>();

        var list = new List<BackupInfo>();

        foreach (string zip in Directory.GetFiles(root, "*.zip"))
        {
            if (zip.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
                continue;
            var info = new FileInfo(zip);
            list.Add(new BackupInfo
            {
                Name = info.Name,
                Path = zip,
                CreatedAt = info.CreationTime,
                IsZip = true,
                SizeBytes = info.Length
            });
        }

        // Legacy folder backups from before zip migration.
        foreach (string dir in Directory.GetDirectories(root))
        {
            list.Add(new BackupInfo
            {
                Name = Path.GetFileName(dir),
                Path = dir,
                CreatedAt = Directory.GetCreationTime(dir),
                IsZip = false,
                SizeBytes = 0
            });
        }

        // Orphaned partials are not listed (and cleaned when possible).
        foreach (string partial in Directory.GetFiles(root, "*.partial"))
            TryDelete(partial);

        return list.OrderByDescending(b => b.CreatedAt).ToList();
    }

    private static List<BackupFileEntry> CollectEntries(string serverDir, string zomboidData, string? baseName)
    {
        var entries = new List<BackupFileEntry>();

        string[] patterns =
        [
            "*.ini",
            "*_SandboxVars.lua",
            "*SandboxVars.lua",
            "*_spawnpoints.lua",
            "*_spawnregions.lua",
            "*_map.ini"
        ];

        foreach (string pattern in patterns)
        {
            foreach (string file in Directory.GetFiles(serverDir, pattern))
            {
                string name = Path.GetFileName(file);
                if (!string.IsNullOrWhiteSpace(baseName)
                    && !name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(Path.GetFileNameWithoutExtension(file), baseName, StringComparison.OrdinalIgnoreCase))
                {
                    if (!name.Contains("SandboxVars", StringComparison.OrdinalIgnoreCase)
                        && !name.Contains("spawn", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                entries.Add(new BackupFileEntry(file, ToZipPath("Server", name)));
            }
        }

        string savesRoot = Path.Combine(zomboidData, "Saves", "Multiplayer");
        if (!Directory.Exists(savesRoot))
            return entries;

        IEnumerable<string> worlds = Directory.GetDirectories(savesRoot);
        if (!string.IsNullOrWhiteSpace(baseName))
        {
            worlds = worlds.Where(d =>
                string.Equals(Path.GetFileName(d), baseName, StringComparison.OrdinalIgnoreCase));
        }

        foreach (string worldDir in worlds)
        {
            string worldName = Path.GetFileName(worldDir);
            CollectDirectoryFiles(
                worldDir,
                ToZipPath("Saves", "Multiplayer", worldName),
                entries);
        }

        return entries;
    }

    private static void CollectDirectoryFiles(string sourceDir, string zipPrefix, List<BackupFileEntry> entries)
    {
        foreach (string dir in Directory.GetDirectories(sourceDir))
        {
            string name = Path.GetFileName(dir);
            if (SkipDirNames.Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase)))
                continue;
            CollectDirectoryFiles(dir, ToZipPath(zipPrefix, name), entries);
        }

        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string name = Path.GetFileName(file);
            entries.Add(new BackupFileEntry(file, ToZipPath(zipPrefix, name)));
        }
    }

    private static string ToZipPath(params string[] parts)
    {
        // Zip entries use forward slashes for portability.
        return string.Join("/", parts.Select(p => p.Replace('\\', '/').Trim('/')));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore cleanup races
        }
    }

    private sealed class ProgressThrottle
    {
        private readonly TimeSpan _minInterval;
        private readonly int _fileBatch;
        private DateTime _lastReportUtc = DateTime.MinValue;
        private int _lastReportedDone;

        public ProgressThrottle(TimeSpan minInterval, int fileBatch)
        {
            _minInterval = minInterval;
            _fileBatch = Math.Max(1, fileBatch);
        }

        public bool ShouldReport(int done, bool force)
        {
            if (force)
                return true;
            if (done - _lastReportedDone >= _fileBatch)
                return true;
            if (DateTime.UtcNow - _lastReportUtc >= _minInterval)
                return true;
            return false;
        }

        public void MarkReported(int done)
        {
            _lastReportedDone = done;
            _lastReportUtc = DateTime.UtcNow;
        }
    }
}

internal readonly record struct BackupFileEntry(string SourcePath, string ZipPath);

public sealed class BackupProgressUpdate
{
    public BackupProgressUpdate(int done, int total, string currentFile)
    {
        Done = done;
        Total = total;
        CurrentFile = currentFile ?? string.Empty;
    }

    public int Done { get; }
    public int Total { get; }
    public string CurrentFile { get; }
    public int Percent => Total > 0 ? (int)Math.Clamp(Math.Round(Done * 100.0 / Total), 0, 100) : 0;
}

public sealed class BackupResult
{
    public bool Success { get; init; }
    public bool Cancelled { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Path { get; init; }
    public int FileCount { get; init; }

    public static BackupResult Ok(string path, int count) => new()
    {
        Success = true,
        Path = path,
        FileCount = count,
        Message = $"Backup created ({count} files → {System.IO.Path.GetFileName(path)})."
    };

    public static BackupResult Fail(string message) => new()
    {
        Success = false,
        Message = message
    };

    public static BackupResult CancelledResult() => new()
    {
        Success = false,
        Cancelled = true,
        Message = "Backup cancelled."
    };
}

public sealed class BackupInfo
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public bool IsZip { get; set; }
    public long SizeBytes { get; set; }
}
