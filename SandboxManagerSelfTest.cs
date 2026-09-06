using System.Text;
using System.Text.Json;

namespace ZomboidManager;

/// <summary>
/// Verifies SandboxVars.lua table parsing/roundtrip. Run:
/// ZomboidManager.exe --selftest-sandbox-tables [optionalPathToSandboxVars.lua]
/// Writes %LocalAppData%\ZomboidManager\sandbox-tables-selftest-result.json
/// </summary>
public static class SandboxManagerSelfTest
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void Run(string? sourcePath = null)
    {
        string resultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZomboidManager",
            "sandbox-tables-selftest-result.json");
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);

        string tempRoot = Path.Combine(Path.GetTempPath(), "ZM_SandboxTableSelfTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            string workFile = Path.Combine(tempRoot, "servertest_SandboxVars.lua");

            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                sourcePath = FindDefaultSandbox();
            }

            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath!))
            {
                // Synthetic fixture covering nested tables, arrays, and scalars.
                File.WriteAllText(workFile, BuildSyntheticFixture(), Utf8NoBom);
                sourcePath = workFile;
            }
            else
            {
                File.Copy(sourcePath!, workFile, overwrite: true);
            }

            // --- Old-bug baseline on a separate copy (line-regex would only keep "{") ---
            Dictionary<string, string> parsed = SandboxManager.ReadSandbox(workFile);
            List<string> tableKeys = parsed
                .Where(p => SandboxManager.IsLuaTableLiteral(p.Value))
                .Select(p => p.Key)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();

            List<string> truncated = tableKeys
                .Where(k => parsed[k].Trim() == "{")
                .ToList();

            string[] sampleNeeded = ["ammomakerOptions", "GamestaVehicleZones", "MoreImmersiveVehicles"];
            var sampleChecks = sampleNeeded.Select(k =>
            {
                bool present = parsed.TryGetValue(k, out string? val);
                bool ok = present
                    && SandboxManager.IsLuaTableLiteral(val)
                    && val!.Trim() != "{"
                    && val.Contains('}', StringComparison.Ordinal);
                return new { key = k, present, ok, preview = present ? Preview(val!) : null };
            }).ToList();

            // Nested keys must not leak as top-level entries.
            bool nestedLeak = parsed.ContainsKey("CraftingSpeed")
                || parsed.ContainsKey("ParkOpenedDoorChance")
                || parsed.ContainsKey("spawnRateModifiedTrafficJams");

            // Roundtrip: write same values back, re-read, compare every table value.
            SandboxManager.WriteSandbox(workFile, parsed);
            Dictionary<string, string> reloaded = SandboxManager.ReadSandbox(workFile);

            var mismatched = new List<object>();
            foreach (string key in tableKeys)
            {
                if (!reloaded.TryGetValue(key, out string? after))
                {
                    mismatched.Add(new { key, error = "missing after reload" });
                    continue;
                }

                string beforeNorm = NormalizeNewlines(parsed[key].Trim());
                string afterNorm = NormalizeNewlines(after.Trim());
                if (!string.Equals(beforeNorm, afterNorm, StringComparison.Ordinal))
                {
                    mismatched.Add(new
                    {
                        key,
                        error = "table value changed",
                        beforeLen = beforeNorm.Length,
                        afterLen = afterNorm.Length,
                        beforePreview = Preview(beforeNorm),
                        afterPreview = Preview(afterNorm)
                    });
                }
            }

            // Brace balance on written file
            string writtenText = File.ReadAllText(workFile);
            int opens = writtenText.Count(c => c == '{');
            int closes = writtenText.Count(c => c == '}');
            bool bracesBalanced = opens == closes;
            bool hasQuotedBraceCorruption = writtenText.Contains("=\"{\"", StringComparison.Ordinal)
                || writtenText.Contains("= \"{\"", StringComparison.Ordinal);

            bool sampleOk = sampleChecks.All(s => !s.present || s.ok);
            bool ok = truncated.Count == 0
                && !nestedLeak
                && mismatched.Count == 0
                && bracesBalanced
                && !hasQuotedBraceCorruption
                && sampleOk
                && tableKeys.Count > 0;

            var payload = new
            {
                ok,
                sourcePath,
                workFile,
                tableKeyCount = tableKeys.Count,
                tableKeys,
                truncatedAsOpenBraceOnly = truncated,
                nestedKeyLeak = nestedLeak,
                sampleChecks,
                mismatchedTables = mismatched,
                bracesBalanced,
                openBraces = opens,
                closeBraces = closes,
                hasQuotedBraceCorruption,
                message = ok
                    ? $"OK — {tableKeys.Count} table setting(s) round-tripped cleanly."
                    : "FAILED — see truncated / mismatched / corruption flags."
            };

            File.WriteAllText(resultPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new
            {
                ok = false,
                error = ex.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // ignore cleanup
            }
        }
    }

    private static string? FindDefaultSandbox()
    {
        string[] candidates =
        [
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Desktop", "fsdfsdf", "test2", "servertest_SandboxVars.lua"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Desktop", "servertest_SandboxVars.lua"),
        ];

        try
        {
            string cfgPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZomboidManager", "config.json");
            if (File.Exists(cfgPath))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
                if (doc.RootElement.TryGetProperty("LastIniFilePath", out JsonElement iniEl))
                {
                    string? ini = iniEl.GetString();
                    if (!string.IsNullOrWhiteSpace(ini))
                    {
                        string? dir = Path.GetDirectoryName(ini);
                        string baseName = Path.GetFileNameWithoutExtension(ini);
                        if (!string.IsNullOrWhiteSpace(dir))
                        {
                            string nearby = Path.Combine(dir, $"{baseName}_SandboxVars.lua");
                            if (File.Exists(nearby))
                                return nearby;
                        }
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string BuildSyntheticFixture() =>
        """
        SandboxVars = {
            VERSION = 6,
            Zombies = 4,
            ZombieLore = {
                Speed = 3,
                Strength = 2,
            },
            ammomakerOptions = {
                CraftingSpeed = 1,
                NitreYield = 10,
                AllowConvertRecipes = false,
            },
            MoreImmersiveVehicles = {
                ParkOpenedDoorChance = 5,
                RoadOpenedWindowChance = 15,
            },
            GamestaVehicleZones = {
                spawnRate = -1,
                trafficjamsLV = true,
                Nested = {
                    A = 1,
                    B = { 1, 2, 3 },
                },
            },
            EmptyThing = {},
        }
        """;

    private static string NormalizeNewlines(string s) =>
        s.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string Preview(string s)
    {
        string oneLine = NormalizeNewlines(s).Replace('\n', '↵');
        return oneLine.Length <= 120 ? oneLine : oneLine[..117] + "...";
    }
}
