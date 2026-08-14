using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZomboidManager;

public sealed class ConfigProfileInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastAppliedUtc { get; set; }
    public string IniFileName { get; set; } = "";
    public string SandboxFileName { get; set; } = "";
    public bool HasIni { get; set; }
    public bool HasSandbox { get; set; }
}

public static class ConfigProfileManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly Regex SafeNameRegex = new(
        @"[^A-Za-z0-9 _.\-()\[\]]+",
        RegexOptions.Compiled);

    public static string ProfilesRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZomboidManager",
        "Profiles");

    public static string BeforeSwitchRoot => Path.Combine(ProfilesRoot, "_before-switch");

    public static List<ConfigProfileInfo> ListProfiles()
    {
        Directory.CreateDirectory(ProfilesRoot);
        var list = new List<ConfigProfileInfo>();

        foreach (string dir in Directory.EnumerateDirectories(ProfilesRoot))
        {
            string folderName = Path.GetFileName(dir);
            if (folderName.StartsWith("_", StringComparison.Ordinal))
                continue;

            ConfigProfileInfo? info = TryReadMeta(dir);
            if (info is null)
                continue;
            list.Add(info);
        }

        return list
            .OrderByDescending(p => p.LastAppliedUtc ?? p.CreatedUtc)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static ConfigProfileInfo SaveCurrentAsProfile(string displayName, AppConfig config)
    {
        string name = NormalizeDisplayName(displayName);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Profile name is required.");

        string? iniPath = ResolveActiveIniPath(config);
        string? sandboxPath = SandboxManager.ResolveSandboxPath(config);

        if (string.IsNullOrWhiteSpace(iniPath) || !File.Exists(iniPath))
            throw new InvalidOperationException("No active server .ini loaded. Load a configuration first.");
        if (string.IsNullOrWhiteSpace(sandboxPath) || !File.Exists(sandboxPath))
            throw new InvalidOperationException("Matching SandboxVars.lua not found for the current .ini.");

        string id = MakeUniqueFolderId(name);
        string destDir = Path.Combine(ProfilesRoot, id);
        Directory.CreateDirectory(destDir);

        string iniName = Path.GetFileName(iniPath);
        string sandboxName = Path.GetFileName(sandboxPath);
        File.Copy(iniPath, Path.Combine(destDir, iniName), overwrite: true);
        File.Copy(sandboxPath, Path.Combine(destDir, sandboxName), overwrite: true);

        var info = new ConfigProfileInfo
        {
            Id = id,
            Name = name,
            CreatedUtc = DateTime.UtcNow,
            LastAppliedUtc = null,
            IniFileName = iniName,
            SandboxFileName = sandboxName,
            HasIni = true,
            HasSandbox = true
        };
        WriteMeta(destDir, info);
        return info;
    }

    public static ConfigProfileInfo RenameProfile(string id, string newDisplayName)
    {
        string dir = GetProfileDir(id);
        ConfigProfileInfo info = TryReadMeta(dir)
            ?? throw new InvalidOperationException("Profile not found.");

        string name = NormalizeDisplayName(newDisplayName);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Profile name is required.");

        info.Name = name;
        WriteMeta(dir, info);
        return info;
    }

    public static void DeleteProfile(string id)
    {
        string dir = GetProfileDir(id);
        if (!Directory.Exists(dir))
            throw new InvalidOperationException("Profile not found.");
        Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// Backs up the active configs, then copies the profile files over them.
    /// Returns the profile info and the active paths that were overwritten.
    /// </summary>
    public static (ConfigProfileInfo Info, string IniPath, string SandboxPath) ApplyProfile(
        string id, AppConfig config)
    {
        string dir = GetProfileDir(id);
        ConfigProfileInfo info = TryReadMeta(dir)
            ?? throw new InvalidOperationException("Profile not found.");

        string? activeIni = ResolveActiveIniPath(config);
        string? activeSandbox = SandboxManager.ResolveSandboxPath(config);

        if (string.IsNullOrWhiteSpace(activeIni))
            throw new InvalidOperationException("No active server .ini path. Load a configuration first.");
        if (string.IsNullOrWhiteSpace(activeSandbox))
            throw new InvalidOperationException("Matching SandboxVars.lua not found for the current .ini.");

        string profileIni = Path.Combine(dir, info.IniFileName);
        string profileSandbox = Path.Combine(dir, info.SandboxFileName);
        if (!File.Exists(profileIni) || !File.Exists(profileSandbox))
            throw new InvalidOperationException("Profile files are incomplete.");

        BackupActiveBeforeSwitch(activeIni, activeSandbox);

        Directory.CreateDirectory(Path.GetDirectoryName(activeIni)!);
        Directory.CreateDirectory(Path.GetDirectoryName(activeSandbox)!);
        File.Copy(profileIni, activeIni, overwrite: true);
        File.Copy(profileSandbox, activeSandbox, overwrite: true);

        info.LastAppliedUtc = DateTime.UtcNow;
        WriteMeta(dir, info);
        return (info, activeIni, activeSandbox);
    }

    private static void BackupActiveBeforeSwitch(string iniPath, string sandboxPath)
    {
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string dest = Path.Combine(BeforeSwitchRoot, stamp);
        Directory.CreateDirectory(dest);

        if (File.Exists(iniPath))
            File.Copy(iniPath, Path.Combine(dest, Path.GetFileName(iniPath)), overwrite: true);
        if (File.Exists(sandboxPath))
            File.Copy(sandboxPath, Path.Combine(dest, Path.GetFileName(sandboxPath)), overwrite: true);

        File.WriteAllText(
            Path.Combine(dest, "backup.txt"),
            $"Automatic backup before profile switch{Environment.NewLine}" +
            $"Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
            $"INI: {iniPath}{Environment.NewLine}" +
            $"Sandbox: {sandboxPath}{Environment.NewLine}",
            Encoding.UTF8);

        // Keep only the newest 10 before-switch backups
        try
        {
            foreach (string old in Directory.EnumerateDirectories(BeforeSwitchRoot)
                         .OrderByDescending(d => d)
                         .Skip(10))
            {
                Directory.Delete(old, recursive: true);
            }
        }
        catch
        {
            // non-fatal cleanup
        }
    }

    public static string? ResolveActiveIniPath(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.LastIniFilePath) && File.Exists(config.LastIniFilePath))
            return Path.GetFullPath(config.LastIniFilePath);
        return null;
    }

    private static string GetProfileDir(string id)
    {
        if (string.IsNullOrWhiteSpace(id)
            || id.Contains("..", StringComparison.Ordinal)
            || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || id.StartsWith("_", StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid profile id.");
        }

        string dir = Path.Combine(ProfilesRoot, id);
        if (!Directory.Exists(dir))
            throw new InvalidOperationException("Profile not found.");
        return dir;
    }

    private static string NormalizeDisplayName(string? name)
    {
        string trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0)
            return "";
        if (trimmed.Length > 64)
            trimmed = trimmed[..64];
        return SafeNameRegex.Replace(trimmed, " ").Trim();
    }

    private static string MakeUniqueFolderId(string displayName)
    {
        string baseId = SafeNameRegex.Replace(displayName, "_").Trim('_', ' ', '.');
        if (string.IsNullOrWhiteSpace(baseId))
            baseId = "profile";
        if (baseId.Length > 40)
            baseId = baseId[..40];

        Directory.CreateDirectory(ProfilesRoot);
        string id = baseId;
        int n = 2;
        while (Directory.Exists(Path.Combine(ProfilesRoot, id)))
        {
            id = $"{baseId}_{n}";
            n++;
        }

        return id;
    }

    private static ConfigProfileInfo? TryReadMeta(string dir)
    {
        try
        {
            string metaPath = Path.Combine(dir, "profile.json");
            if (!File.Exists(metaPath))
            {
                // Recover bare folders that only have the two files
                string[] inis = Directory.GetFiles(dir, "*.ini");
                string[] sandboxes = Directory.GetFiles(dir, "*SandboxVars.lua");
                if (inis.Length == 0 || sandboxes.Length == 0)
                    return null;

                var recovered = new ConfigProfileInfo
                {
                    Id = Path.GetFileName(dir),
                    Name = Path.GetFileName(dir),
                    CreatedUtc = Directory.GetCreationTimeUtc(dir),
                    IniFileName = Path.GetFileName(inis[0]),
                    SandboxFileName = Path.GetFileName(sandboxes[0]),
                    HasIni = true,
                    HasSandbox = true
                };
                WriteMeta(dir, recovered);
                return recovered;
            }

            string json = File.ReadAllText(metaPath);
            ConfigProfileInfo? info = JsonSerializer.Deserialize<ConfigProfileInfo>(json, JsonOptions);
            if (info is null)
                return null;

            info.Id = Path.GetFileName(dir);
            info.HasIni = !string.IsNullOrWhiteSpace(info.IniFileName)
                          && File.Exists(Path.Combine(dir, info.IniFileName));
            info.HasSandbox = !string.IsNullOrWhiteSpace(info.SandboxFileName)
                              && File.Exists(Path.Combine(dir, info.SandboxFileName));
            if (string.IsNullOrWhiteSpace(info.Name))
                info.Name = info.Id;
            return info;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteMeta(string dir, ConfigProfileInfo info)
    {
        Directory.CreateDirectory(dir);
        string metaPath = Path.Combine(dir, "profile.json");
        File.WriteAllText(metaPath, JsonSerializer.Serialize(info, JsonOptions), Encoding.UTF8);
    }
}
