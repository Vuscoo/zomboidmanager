using System.Globalization;
using System.Text.Json;

namespace ZomboidManager;

public static class BackupManager
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string GetBackupsRoot() =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backups");

    public static BackupResult CreateBackup(AppConfig config, IProgress<(int done, string file)>? progress = null)
    {
        string zomboidData = config.ZomboidDataPath;
        if (string.IsNullOrWhiteSpace(zomboidData) || !Directory.Exists(zomboidData))
        {
            return BackupResult.Fail("Zomboid data folder is not configured or missing.");
        }

        string serverDir = Path.Combine(zomboidData, "Server");
        if (!Directory.Exists(serverDir))
        {
            return BackupResult.Fail($"Server folder not found: {serverDir}");
        }

        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        string backupDir = Path.Combine(GetBackupsRoot(), stamp);
        Directory.CreateDirectory(backupDir);

        string serverBackup = Path.Combine(backupDir, "Server");
        Directory.CreateDirectory(serverBackup);

        var copied = new List<string>();
        string? baseName = null;

        if (!string.IsNullOrWhiteSpace(config.LastIniFilePath) && File.Exists(config.LastIniFilePath))
            baseName = Path.GetFileNameWithoutExtension(config.LastIniFilePath);

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

                string dest = Path.Combine(serverBackup, name);
                File.Copy(file, dest, overwrite: true);
                string relative = Path.Combine("Server", name);
                copied.Add(relative);
                progress?.Report((copied.Count, relative));
            }
        }

        string savesRoot = Path.Combine(zomboidData, "Saves", "Multiplayer");
        if (Directory.Exists(savesRoot))
        {
            IEnumerable<string> worlds = Directory.GetDirectories(savesRoot);
            if (!string.IsNullOrWhiteSpace(baseName))
            {
                worlds = worlds.Where(d =>
                    string.Equals(Path.GetFileName(d), baseName, StringComparison.OrdinalIgnoreCase));
            }

            foreach (string worldDir in worlds)
            {
                string worldName = Path.GetFileName(worldDir);
                string destWorld = Path.Combine(backupDir, "Saves", "Multiplayer", worldName);
                CopyDirectoryImportant(worldDir, destWorld, copied, $"Saves/Multiplayer/{worldName}", progress);
            }
        }

        var info = new
        {
            createdAt = DateTime.Now.ToString("o"),
            zomboidDataPath = zomboidData,
            lastIniFilePath = config.LastIniFilePath,
            files = copied
        };
        File.WriteAllText(Path.Combine(backupDir, "backup_info.json"), JsonSerializer.Serialize(info, JsonOptions));

        if (copied.Count == 0)
            return BackupResult.Fail("No important files found to backup. Check Zomboid data path / loaded INI.");

        return BackupResult.Ok(backupDir, copied.Count);
    }

    public static List<BackupInfo> ListBackups()
    {
        string root = GetBackupsRoot();
        if (!Directory.Exists(root))
            return new List<BackupInfo>();

        return Directory.GetDirectories(root)
            .Select(dir => new BackupInfo
            {
                Name = Path.GetFileName(dir),
                Path = dir,
                CreatedAt = Directory.GetCreationTime(dir)
            })
            .OrderByDescending(b => b.CreatedAt)
            .ToList();
    }

    private static void CopyDirectoryImportant(
        string source,
        string dest,
        List<string> copied,
        string relativePrefix,
        IProgress<(int done, string file)>? progress)
    {
        Directory.CreateDirectory(dest);

        string[] skipDirNames = ["caches", "cache", "logs", "Logs"];

        foreach (string dir in Directory.GetDirectories(source))
        {
            string name = Path.GetFileName(dir);
            if (skipDirNames.Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase)))
                continue;
            CopyDirectoryImportant(dir, Path.Combine(dest, name), copied, $"{relativePrefix}/{name}", progress);
        }

        foreach (string file in Directory.GetFiles(source))
        {
            string name = Path.GetFileName(file);
            string destFile = Path.Combine(dest, name);
            File.Copy(file, destFile, overwrite: true);
            string relative = $"{relativePrefix}/{name}";
            copied.Add(relative);
            progress?.Report((copied.Count, relative));
        }
    }
}

public sealed class BackupResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Path { get; init; }
    public int FileCount { get; init; }

    public static BackupResult Ok(string path, int count) => new()
    {
        Success = true,
        Path = path,
        FileCount = count,
        Message = $"Backup created ({count} files)."
    };

    public static BackupResult Fail(string message) => new()
    {
        Success = false,
        Message = message
    };
}

public sealed class BackupInfo
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
