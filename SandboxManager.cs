using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ZomboidManager;

public static class SandboxManager
{
    private static readonly Regex AssignmentRegex = new(
        @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.+?)\s*,?\s*$",
        RegexOptions.Compiled);

    public static string? ResolveSandboxPath(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.LastIniFilePath))
        {
            string dir = Path.GetDirectoryName(config.LastIniFilePath) ?? string.Empty;
            string baseName = Path.GetFileNameWithoutExtension(config.LastIniFilePath);
            string candidate = Path.Combine(dir, $"{baseName}_SandboxVars.lua");
            if (File.Exists(candidate))
                return candidate;

            candidate = Path.Combine(dir, "SandboxVars.lua");
            if (File.Exists(candidate))
                return candidate;
        }

        if (!string.IsNullOrWhiteSpace(config.ZomboidDataPath))
        {
            string serverDir = Path.Combine(config.ZomboidDataPath, "Server");
            if (Directory.Exists(serverDir))
            {
                string[] files = Directory.GetFiles(serverDir, "*SandboxVars.lua");
                if (files.Length == 1)
                    return files[0];
                if (files.Length > 1 && !string.IsNullOrWhiteSpace(config.LastIniFilePath))
                {
                    string baseName = Path.GetFileNameWithoutExtension(config.LastIniFilePath);
                    string? match = files.FirstOrDefault(f =>
                        Path.GetFileName(f).StartsWith(baseName, StringComparison.OrdinalIgnoreCase));
                    if (match is not null)
                        return match;
                }
            }
        }

        return null;
    }

    public static Dictionary<string, string> ReadSandbox(string filePath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(filePath))
            throw new FileNotFoundException("SandboxVars file not found.", filePath);

        foreach (string rawLine in File.ReadAllLines(filePath))
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("--") || line.StartsWith("SandboxVars")
                || line == "{" || line == "}" || line.StartsWith("return", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Match match = AssignmentRegex.Match(line);
            if (!match.Success)
                continue;

            string key = match.Groups[1].Value.Trim();
            string value = match.Groups[2].Value.Trim().TrimEnd(',');
            if (string.Equals(key, "VERSION", StringComparison.OrdinalIgnoreCase))
                continue;

            result[key] = NormalizeLuaValue(value);
        }

        return result;
    }

    public static void WriteSandbox(string filePath, Dictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!File.Exists(filePath))
            throw new FileNotFoundException("SandboxVars file not found.", filePath);

        string[] lines = File.ReadAllLines(filePath);
        var remaining = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        var output = new List<string>();

        foreach (string rawLine in lines)
        {
            string trimmed = rawLine.Trim();
            Match match = AssignmentRegex.Match(trimmed);
            if (!match.Success)
            {
                output.Add(rawLine);
                continue;
            }

            string key = match.Groups[1].Value.Trim();
            if (!remaining.TryGetValue(key, out string? newValue))
            {
                output.Add(rawLine);
                continue;
            }

            string indent = rawLine[..^rawLine.TrimStart().Length];
            bool hasComma = trimmed.EndsWith(',');
            output.Add($"{indent}{key} = {ToLuaLiteral(newValue)}{(hasComma ? "," : string.Empty)}");
            remaining.Remove(key);
        }

        // Append unknown new keys before the closing brace if possible.
        if (remaining.Count > 0)
        {
            int closeIndex = output.FindLastIndex(l => l.Trim() == "}");
            if (closeIndex >= 0)
            {
                foreach (KeyValuePair<string, string> pair in remaining)
                {
                    output.Insert(closeIndex, $"    {pair.Key} = {ToLuaLiteral(pair.Value)},");
                    closeIndex++;
                }
            }
        }

        File.WriteAllLines(filePath, output, Encoding.UTF8);
    }

    public static List<IniEntry> ParseToEntries(Dictionary<string, string> raw)
    {
        var entries = new List<IniEntry>();
        foreach (KeyValuePair<string, string> pair in raw)
        {
            SandboxCatalog.Meta meta = SandboxCatalog.Resolve(pair.Key, pair.Value);
            string category = meta.Category;
            string inputType = meta.InputType;

            if (string.Equals(pair.Value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(pair.Value, "false", StringComparison.OrdinalIgnoreCase))
            {
                inputType = "checkbox";
            }
            else if (double.TryParse(pair.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                inputType = "number";
            }

            entries.Add(new IniEntry
            {
                Key = pair.Key,
                DisplayName = meta.DisplayName,
                Value = pair.Value,
                Category = category,
                InputType = inputType,
                Description = pair.Key,
                Order = meta.Order
            });
        }

        return entries
            .OrderBy(e => SandboxCatalog.CategorySortIndex(e.Category))
            .ThenBy(e => e.Order)
            .ThenBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string NormalizeLuaValue(string value)
    {
        value = value.Trim();
        if ((value.StartsWith('"') && value.EndsWith('"'))
            || (value.StartsWith('\'') && value.EndsWith('\'')))
        {
            return value[1..^1];
        }

        return value;
    }

    private static string ToLuaLiteral(string value)
    {
        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            return value.ToLowerInvariant();
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            return number.ToString(CultureInfo.InvariantCulture);

        string escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }
}
