using System.Text.Json;

namespace ZomboidManager;

internal static class ConfigProfileManagerSelfTest
{
    public static void Run()
    {
        var config = ConfigManager.Load();
        string? ini = ConfigProfileManager.ResolveActiveIniPath(config);
        string? sandbox = SandboxManager.ResolveSandboxPath(config);

        if (string.IsNullOrWhiteSpace(ini) || !File.Exists(ini)
            || string.IsNullOrWhiteSpace(sandbox) || !File.Exists(sandbox))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ok = false,
                error = "Need LastIniFilePath + matching SandboxVars.lua",
                ini,
                sandbox
            }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.Exit(1);
            return;
        }

        string marker = $"__selftest_{DateTime.Now:HHmmss}";
        string originalIni = File.ReadAllText(ini);
        string originalSandbox = File.ReadAllText(sandbox);

        try
        {
            // Save profile A from current files
            ConfigProfileInfo a = ConfigProfileManager.SaveCurrentAsProfile($"SelfTest A {marker}", config);

            // Mutate live files
            File.WriteAllText(ini, originalIni + Environment.NewLine + $"; {marker}");
            File.WriteAllText(sandbox, originalSandbox); // keep valid lua; just ensure copy works

            // Save profile B from mutated ini
            ConfigProfileInfo b = ConfigProfileManager.SaveCurrentAsProfile($"SelfTest B {marker}", config);

            // Rename B
            ConfigProfileInfo renamed = ConfigProfileManager.RenameProfile(b.Id, $"SelfTest B Renamed {marker}");

            // Restore A over live files
            (ConfigProfileInfo applied, string appliedIni, string appliedSandbox) =
                ConfigProfileManager.ApplyProfile(a.Id, config);

            string afterIni = File.ReadAllText(appliedIni);
            bool restored = afterIni == originalIni;
            bool beforeBackupExists = Directory.Exists(ConfigProfileManager.BeforeSwitchRoot)
                && Directory.EnumerateDirectories(ConfigProfileManager.BeforeSwitchRoot).Any();

            // Delete both test profiles
            ConfigProfileManager.DeleteProfile(a.Id);
            ConfigProfileManager.DeleteProfile(renamed.Id);

            bool aGone = !Directory.Exists(Path.Combine(ConfigProfileManager.ProfilesRoot, a.Id));
            bool bGone = !Directory.Exists(Path.Combine(ConfigProfileManager.ProfilesRoot, renamed.Id));

            // Ensure live files still original after restore
            File.WriteAllText(ini, originalIni);
            File.WriteAllText(sandbox, originalSandbox);

            bool ok = restored && beforeBackupExists && aGone && bGone
                      && applied.LastAppliedUtc is not null
                      && renamed.Name.Contains("Renamed", StringComparison.Ordinal);

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ok,
                restored,
                beforeBackupExists,
                aGone,
                bGone,
                profileA = a.Name,
                profileB = renamed.Name,
                appliedIni,
                appliedSandbox
            }, new JsonSerializerOptions { WriteIndented = true }));

            Environment.Exit(ok ? 0 : 1);
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(ini, originalIni);
                File.WriteAllText(sandbox, originalSandbox);
            }
            catch
            {
                // best effort restore
            }

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ok = false,
                error = ex.Message
            }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.Exit(1);
        }
    }
}
