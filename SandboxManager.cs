using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ZomboidManager;

public static class SandboxManager
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Top-level (or any single-line) assignment: key = value, with optional trailing comma.
    /// </summary>
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

        string[] lines = File.ReadAllLines(filePath);
        int depth = 0;
        int i = 0;

        while (i < lines.Length)
        {
            string rawLine = lines[i];
            string trimmed = rawLine.Trim();
            int depthBefore = depth;

            // Only parse assignments directly under SandboxVars = { ... } (depth 1).
            if (depthBefore == 1
                && !string.IsNullOrWhiteSpace(trimmed)
                && !trimmed.StartsWith("--", StringComparison.Ordinal)
                && !trimmed.StartsWith("return", StringComparison.OrdinalIgnoreCase))
            {
                Match match = AssignmentRegex.Match(trimmed);
                if (match.Success)
                {
                    string key = match.Groups[1].Value.Trim();
                    string rhs = match.Groups[2].Value.Trim().TrimEnd(',').Trim();

                    if (!string.Equals(key, "VERSION", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(key, "SandboxVars", StringComparison.OrdinalIgnoreCase))
                    {
                        if (rhs.StartsWith('{'))
                        {
                            if (!TryExtractTable(lines, i, out string tableText, out int endIndex))
                                throw new InvalidDataException(
                                    $"Unbalanced Lua table for '{key}' starting at line {i + 1}.");

                            result[key] = tableText;
                            for (int j = i; j <= endIndex; j++)
                                depth += NetBraceDelta(lines[j]);
                            i = endIndex + 1;
                            continue;
                        }

                        result[key] = NormalizeLuaValue(rhs);
                    }
                }
            }

            depth += NetBraceDelta(rawLine);
            i++;
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
        var output = new List<string>(lines.Length + remaining.Count);

        int depth = 0;
        int i = 0;

        while (i < lines.Length)
        {
            string rawLine = lines[i];
            string trimmed = rawLine.Trim();
            int depthBefore = depth;

            if (depthBefore == 1
                && !string.IsNullOrWhiteSpace(trimmed)
                && !trimmed.StartsWith("--", StringComparison.Ordinal))
            {
                Match match = AssignmentRegex.Match(trimmed);
                if (match.Success)
                {
                    string key = match.Groups[1].Value.Trim();
                    string rhs = match.Groups[2].Value.Trim().TrimEnd(',').Trim();

                    if (remaining.TryGetValue(key, out string? newValue))
                    {
                        string indent = rawLine[..(rawLine.Length - rawLine.TrimStart().Length)];
                        bool originalWasTable = rhs.StartsWith('{');

                        if (originalWasTable)
                        {
                            if (!TryExtractTable(lines, i, out _, out int endIndex))
                                throw new InvalidDataException(
                                    $"Unbalanced Lua table for '{key}' starting at line {i + 1}.");

                            bool hasComma = LineHasTrailingCommaAfterTable(lines, i, endIndex);
                            AppendAssignment(output, indent, key, newValue, hasComma);

                            for (int j = i; j <= endIndex; j++)
                                depth += NetBraceDelta(lines[j]);
                            remaining.Remove(key);
                            i = endIndex + 1;
                            continue;
                        }

                        // Scalar (or user replaced a scalar with a table).
                        bool hasCommaScalar = trimmed.EndsWith(',');
                        AppendAssignment(output, indent, key, newValue, hasCommaScalar);
                        depth += NetBraceDelta(rawLine);
                        remaining.Remove(key);
                        i++;
                        continue;
                    }

                    // Key not in save payload: if it is a table, keep the whole block untouched.
                    if (rhs.StartsWith('{'))
                    {
                        if (!TryExtractTable(lines, i, out _, out int endIndex))
                            throw new InvalidDataException(
                                $"Unbalanced Lua table for '{key}' starting at line {i + 1}.");

                        for (int j = i; j <= endIndex; j++)
                        {
                            output.Add(lines[j]);
                            depth += NetBraceDelta(lines[j]);
                        }

                        i = endIndex + 1;
                        continue;
                    }
                }
            }

            output.Add(rawLine);
            depth += NetBraceDelta(rawLine);
            i++;
        }

        // Append unknown new keys before the closing root brace if possible.
        if (remaining.Count > 0)
        {
            int closeIndex = output.FindLastIndex(l => l.Trim() == "}");
            if (closeIndex >= 0)
            {
                foreach (KeyValuePair<string, string> pair in remaining)
                {
                    AppendAssignment(output, "    ", pair.Key, pair.Value, trailingComma: true, insertAt: closeIndex);
                    closeIndex++;
                }
            }
        }

        File.WriteAllText(filePath, string.Join(Environment.NewLine, output) + Environment.NewLine, Utf8NoBom);
    }

    public static List<IniEntry> ParseToEntries(Dictionary<string, string> raw)
    {
        var entries = new List<IniEntry>();
        foreach (KeyValuePair<string, string> pair in raw)
        {
            SandboxCatalog.Meta meta = SandboxCatalog.Resolve(pair.Key, pair.Value);
            string category = meta.Category;
            string inputType = meta.InputType;

            if (IsLuaTableLiteral(pair.Value))
            {
                inputType = "lua_table";
            }
            else if (string.Equals(pair.Value, "true", StringComparison.OrdinalIgnoreCase)
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

    public static bool IsLuaTableLiteral(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        string trimmed = value.Trim();
        return trimmed.StartsWith('{') && trimmed.EndsWith('}');
    }

    /// <summary>
    /// Extracts a balanced Lua table starting at the first '{' on <paramref name="startIndex"/>.
    /// Returns the exact table text from '{' through the matching '}'.
    /// </summary>
    public static bool TryExtractTable(string[] lines, int startIndex, out string tableText, out int endIndex)
    {
        tableText = string.Empty;
        endIndex = startIndex;

        if (startIndex < 0 || startIndex >= lines.Length)
            return false;

        int braceStart = lines[startIndex].IndexOf('{');
        if (braceStart < 0)
            return false;

        var sb = new StringBuilder();
        int depth = 0;
        bool inSingle = false;
        bool inDouble = false;

        for (int lineIdx = startIndex; lineIdx < lines.Length; lineIdx++)
        {
            string line = lines[lineIdx];
            int charStart = lineIdx == startIndex ? braceStart : 0;

            for (int c = charStart; c < line.Length; c++)
            {
                char ch = line[c];
                char prev = c > 0 ? line[c - 1] : '\0';

                // Line comments outside strings — stop counting braces for the rest of the line,
                // but still include comment text in the captured table body.
                if (!inSingle && !inDouble && ch == '-' && c + 1 < line.Length && line[c + 1] == '-')
                {
                    sb.Append(line.AsSpan(c));
                    break;
                }

                if (!inDouble && ch == '\'' && prev != '\\')
                    inSingle = !inSingle;
                else if (!inSingle && ch == '"' && prev != '\\')
                    inDouble = !inDouble;
                else if (!inSingle && !inDouble)
                {
                    if (ch == '{')
                        depth++;
                    else if (ch == '}')
                        depth--;
                }

                sb.Append(ch);

                if (depth == 0)
                {
                    tableText = sb.ToString();
                    endIndex = lineIdx;
                    return true;
                }
            }

            if (lineIdx < lines.Length - 1)
                sb.Append('\n');
        }

        return false;
    }

    private static bool LineHasTrailingCommaAfterTable(string[] lines, int startIndex, int endIndex)
    {
        string endLine = lines[endIndex];
        int close = endLine.LastIndexOf('}');
        if (close < 0)
            return false;
        string after = endLine[(close + 1)..].Trim();
        return after.StartsWith(',');
    }

    private static void AppendAssignment(
        List<string> output,
        string indent,
        string key,
        string value,
        bool trailingComma,
        int? insertAt = null)
    {
        string literal = ToLuaLiteral(value);
        string comma = trailingComma ? "," : string.Empty;

        // Multiline tables: keep internal newlines; first line shares the assignment.
        string[] valueLines = literal.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (valueLines.Length == 1)
        {
            string line = $"{indent}{key} = {valueLines[0]}{comma}";
            if (insertAt is int at)
                output.Insert(at, line);
            else
                output.Add(line);
            return;
        }

        var block = new List<string>(valueLines.Length);
        block.Add($"{indent}{key} = {valueLines[0]}");
        for (int v = 1; v < valueLines.Length; v++)
        {
            string part = valueLines[v];
            if (v == valueLines.Length - 1)
                part += comma;
            block.Add(part);
        }

        if (insertAt is int idx)
            output.InsertRange(idx, block);
        else
            output.AddRange(block);
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
        if (IsLuaTableLiteral(value))
            return value.Trim();

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

    /// <summary>
    /// Net change in brace depth for a line, ignoring braces inside strings and after '--' comments.
    /// </summary>
    public static int NetBraceDelta(string line)
    {
        int depth = 0;
        bool inSingle = false;
        bool inDouble = false;

        for (int c = 0; c < line.Length; c++)
        {
            char ch = line[c];
            char prev = c > 0 ? line[c - 1] : '\0';

            if (!inSingle && !inDouble && ch == '-' && c + 1 < line.Length && line[c + 1] == '-')
                break;

            if (!inDouble && ch == '\'' && prev != '\\')
                inSingle = !inSingle;
            else if (!inSingle && ch == '"' && prev != '\\')
                inDouble = !inDouble;
            else if (!inSingle && !inDouble)
            {
                if (ch == '{')
                    depth++;
                else if (ch == '}')
                    depth--;
            }
        }

        return depth;
    }
}
