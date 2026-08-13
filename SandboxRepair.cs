using System.Text;
using System.Text.RegularExpressions;

namespace ZomboidManager;

public sealed class SandboxRepairIssue
{
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
    public int? Line { get; init; }
}

public sealed class SandboxRepairResult
{
    public bool Success { get; init; }
    public string Path { get; init; } = "";
    public string? BackupPath { get; init; }
    public string? ReferencePath { get; init; }
    public List<SandboxRepairIssue> Issues { get; init; } = new();
    public List<string> Fixes { get; init; } = new();
    public string Summary { get; init; } = "";
}

/// <summary>
/// Diagnoses and repairs Project Zomboid SandboxVars.lua files.
/// Never writes UTF-8 BOM; never rewrites nested table openers as quoted strings.
/// </summary>
public static class SandboxRepair
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly Regex CorruptedTableOpenerRegex = new(
        @"^(\s*[A-Za-z_][A-Za-z0-9_]*\s*=\s*)""\{""(\s*,\s*)?$",
        RegexOptions.Compiled);

    private static readonly Regex CorruptedTableAnywhereRegex = new(
        @"=\s*""\{""",
        RegexOptions.Compiled);

    private static readonly Regex TableOpenerRegex = new(
        @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*\{\s*$",
        RegexOptions.Compiled);

    private static readonly Regex ScalarAssignmentRegex = new(
        @"^(\s*)([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.+?)\s*,?\s*$",
        RegexOptions.Compiled);

    private static readonly HashSet<string> VanillaSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "Basement", "Map", "ZombieLore", "ZombieConfig", "MultiplierConfig"
    };

    public static SandboxRepairResult Analyze(string filePath, string? referencePath = null)
    {
        var issues = new List<SandboxRepairIssue>();
        if (!File.Exists(filePath))
        {
            issues.Add(new SandboxRepairIssue { Code = "missing", Message = "Datei nicht gefunden." });
            return new SandboxRepairResult
            {
                Success = false,
                Path = filePath,
                Issues = issues,
                Summary = "Datei fehlt."
            };
        }

        byte[] bytes = File.ReadAllBytes(filePath);
        if (HasUtf8Bom(bytes))
        {
            issues.Add(new SandboxRepairIssue
            {
                Code = "bom",
                Message = "UTF-8-BOM am Dateianfang — Project Zomboid kann daran scheitern."
            });
        }

        string text = DecodeUtf8(bytes);
        string[] lines = SplitLines(text);

        if (!Regex.IsMatch(text, @"^\s*SandboxVars\s*=\s*\{", RegexOptions.Multiline))
        {
            issues.Add(new SandboxRepairIssue
            {
                Code = "header",
                Message = "Datei beginnt nicht mit 'SandboxVars = {'."
            });
        }

        int open = CountChar(text, '{');
        int close = CountChar(text, '}');
        if (open != close)
        {
            issues.Add(new SandboxRepairIssue
            {
                Code = "braces",
                Message = $"Geschweifte Klammern unausgeglichen ({{ {open} / }} {close})."
            });
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimEnd();
            if (CorruptedTableOpenerRegex.IsMatch(trimmed) || CorruptedTableAnywhereRegex.IsMatch(trimmed))
            {
                issues.Add(new SandboxRepairIssue
                {
                    Code = "quoted_brace",
                    Line = i + 1,
                    Message = $"Tabellen-Öffner als String zerstört: {trimmed.Trim()}"
                });
            }

            // Trailing junk after closing root
            if (i > 0 && lines[i - 1].Trim() == "}" && !string.IsNullOrWhiteSpace(trimmed)
                && !trimmed.StartsWith("--", StringComparison.Ordinal))
            {
                // Only flag if we already closed SandboxVars (heuristic: brace depth would be 0)
            }
        }

        // Missing comma before next assignment (non-table)
        for (int i = 0; i < lines.Length - 1; i++)
        {
            string cur = lines[i].TrimEnd();
            string next = lines[i + 1].Trim();
            if (string.IsNullOrWhiteSpace(cur) || cur.TrimStart().StartsWith("--", StringComparison.Ordinal))
                continue;
            if (!cur.Contains('=', StringComparison.Ordinal))
                continue;
            if (cur.TrimEnd().EndsWith('{') || cur.TrimEnd().EndsWith(','))
                continue;
            if (next.StartsWith('}') || string.IsNullOrWhiteSpace(next) || next.StartsWith("--", StringComparison.Ordinal))
                continue;
            if (next.Contains('=', StringComparison.Ordinal) || next == "{")
            {
                issues.Add(new SandboxRepairIssue
                {
                    Code = "missing_comma",
                    Line = i + 1,
                    Message = $"Möglicherweise fehlendes Komma: {cur.Trim()}"
                });
            }
        }

        if (!string.IsNullOrWhiteSpace(referencePath) && File.Exists(referencePath))
        {
            var brokenSections = GetTopLevelSections(lines);
            var refSections = GetTopLevelSections(SplitLines(DecodeUtf8(File.ReadAllBytes(referencePath))));
            foreach (string section in VanillaSections)
            {
                if (refSections.Contains(section) && !brokenSections.Contains(section))
                {
                    issues.Add(new SandboxRepairIssue
                    {
                        Code = "missing_section",
                        Message = $"Vanilla-Sektion '{section}' fehlt (in Referenz vorhanden)."
                    });
                }
            }

            // MultiplierConfig lifestyle skills often corrupted to tables
            foreach (string skill in new[] { "Dancing", "Meditation", "Music" })
            {
                if (IsMultiplierSkillCorruptedToTable(lines, skill))
                {
                    issues.Add(new SandboxRepairIssue
                    {
                        Code = "multiplier_table",
                        Message = $"MultiplierConfig.{skill} ist eine Tabelle statt Zahl (typischer Save-Bug)."
                    });
                }
            }
        }
        else
        {
            foreach (string skill in new[] { "Dancing", "Meditation", "Music" })
            {
                if (IsMultiplierSkillCorruptedToTable(lines, skill))
                {
                    issues.Add(new SandboxRepairIssue
                    {
                        Code = "multiplier_table",
                        Message = $"MultiplierConfig.{skill} ist eine Tabelle statt Zahl (typischer Save-Bug)."
                    });
                }
            }
        }

        string summary = issues.Count == 0
            ? "Keine bekannten Probleme gefunden."
            : $"{issues.Count} Problem(e) gefunden.";

        return new SandboxRepairResult
        {
            Success = true,
            Path = filePath,
            ReferencePath = referencePath,
            Issues = issues,
            Summary = summary
        };
    }

    public static SandboxRepairResult Repair(string filePath, string? referencePath = null)
    {
        var analysis = Analyze(filePath, referencePath);
        if (!File.Exists(filePath))
            return analysis;

        string backupPath = filePath + ".pre-repair.bak";
        File.Copy(filePath, backupPath, overwrite: true);

        var fixes = new List<string>();
        byte[] originalBytes = File.ReadAllBytes(filePath);
        if (HasUtf8Bom(originalBytes))
            fixes.Add("UTF-8-BOM entfernt.");

        string text = DecodeUtf8(originalBytes);
        string[] lines = SplitLines(text);

        // 1) Fix quoted table openers
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimEnd();
            Match match = CorruptedTableOpenerRegex.Match(trimmed);
            if (!match.Success)
                continue;

            bool hadComma = match.Groups[2].Success;
            lines[i] = hadComma
                ? match.Groups[1].Value + "1,"
                : match.Groups[1].Value + "{";
            fixes.Add($"Zeile {i + 1}: zerstörten Tabellen-Öffner repariert.");
        }

        // 2) Fix MultiplierConfig skills that became nested empty/broken tables
        lines = FixMultiplierSkillsTurnedIntoTables(lines, fixes);

        // 3) Reference-assisted: restore missing vanilla sections; fill missing MultiplierConfig skills
        if (!string.IsNullOrWhiteSpace(referencePath) && File.Exists(referencePath))
        {
            string[] refLines = SplitLines(DecodeUtf8(File.ReadAllBytes(referencePath)));
            lines = EnsureVanillaSectionsFromReference(lines, refLines, fixes);
            lines = EnsureMultiplierSkillsFromReference(lines, refLines, fixes);
        }
        else
        {
            lines = EnsureMultiplierSkillsDefaults(lines, fixes);
        }

        // 4) Ensure header / trailing root close
        string joined = string.Join("\n", lines);
        if (!Regex.IsMatch(joined, @"^\s*SandboxVars\s*=\s*\{", RegexOptions.Multiline))
        {
            var list = lines.ToList();
            list.Insert(0, "SandboxVars = {");
            lines = list.ToArray();
            fixes.Add("Fehlenden Header 'SandboxVars = {' ergänzt.");
            joined = string.Join("\n", lines);
        }

        int open = CountChar(joined, '{');
        int close = CountChar(joined, '}');
        if (open > close)
        {
            var list = lines.ToList();
            for (int i = 0; i < open - close; i++)
                list.Add("}");
            lines = list.ToArray();
            fixes.Add($"Fehlende schließende Klammer(n) ergänzt ({open - close}).");
            joined = string.Join("\n", lines);
        }

        // Normalize to CRLF like PZ usually writes, UTF-8 without BOM
        string output = string.Join("\r\n", lines);
        if (!output.EndsWith("\r\n", StringComparison.Ordinal))
            output += "\r\n";

        File.WriteAllText(filePath, output, Utf8NoBom);

        // Verify no quoted braces remain
        string verify = File.ReadAllText(filePath, Utf8NoBom);
        if (CorruptedTableAnywhereRegex.IsMatch(verify))
        {
            File.Copy(backupPath, filePath, overwrite: true);
            return new SandboxRepairResult
            {
                Success = false,
                Path = filePath,
                BackupPath = backupPath,
                ReferencePath = referencePath,
                Issues = analysis.Issues,
                Fixes = fixes,
                Summary = "Reparatur abgebrochen: Nach dem Schreiben waren noch zerstörte Tabellen vorhanden. Backup wiederhergestellt."
            };
        }

        if (HasUtf8Bom(File.ReadAllBytes(filePath)))
        {
            File.Copy(backupPath, filePath, overwrite: true);
            return new SandboxRepairResult
            {
                Success = false,
                Path = filePath,
                BackupPath = backupPath,
                ReferencePath = referencePath,
                Issues = analysis.Issues,
                Fixes = fixes,
                Summary = "Reparatur abgebrochen: BOM konnte nicht entfernt werden. Backup wiederhergestellt."
            };
        }

        var post = Analyze(filePath, referencePath);
        string summary = fixes.Count == 0
            ? "Keine Änderungen nötig — Datei war bereits in Ordnung."
            : $"Reparatur abgeschlossen: {fixes.Count} Fix(es). Backup: {Path.GetFileName(backupPath)}";

        if (post.Issues.Count > 0)
            summary += $" Verbleibende Hinweise: {post.Issues.Count}.";

        return new SandboxRepairResult
        {
            Success = true,
            Path = filePath,
            BackupPath = backupPath,
            ReferencePath = referencePath,
            Issues = post.Issues,
            Fixes = fixes,
            Summary = summary
        };
    }

    private static bool IsMultiplierSkillCorruptedToTable(string[] lines, string skill)
    {
        int start = FindTopLevelSectionStart(lines, "MultiplierConfig");
        if (start < 0)
            return false;
        int end = FindSectionEnd(lines, start);
        for (int i = start + 1; i < end; i++)
        {
            if (TableOpenerRegex.IsMatch(lines[i]) &&
                lines[i].TrimStart().StartsWith(skill + " ", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            // also: Skill = "{"
            if (CorruptedTableOpenerRegex.IsMatch(lines[i].TrimEnd())
                && lines[i].Contains(skill, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string[] FixMultiplierSkillsTurnedIntoTables(string[] lines, List<string> fixes)
    {
        int start = FindTopLevelSectionStart(lines, "MultiplierConfig");
        if (start < 0)
            return lines;

        int end = FindSectionEnd(lines, start);
        var result = new List<string>(lines.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            if (i <= start || i >= end)
            {
                result.Add(lines[i]);
                continue;
            }

            Match opener = TableOpenerRegex.Match(lines[i]);
            if (opener.Success
                && opener.Groups[1].Value is "Dancing" or "Meditation" or "Music"
                && i + 1 < end
                && lines[i + 1].Trim().StartsWith('}'))
            {
                // Empty nested table → scalar 1
                string indent = lines[i][..^lines[i].TrimStart().Length];
                result.Add($"{indent}{opener.Groups[1].Value} = 1,");
                fixes.Add($"MultiplierConfig.{opener.Groups[1].Value}: leere Tabelle → 1.");
                i++; // skip closing }
                continue;
            }

            result.Add(lines[i]);
        }

        return result.ToArray();
    }

    private static string[] EnsureMultiplierSkillsDefaults(string[] lines, List<string> fixes)
    {
        int start = FindTopLevelSectionStart(lines, "MultiplierConfig");
        if (start < 0)
            return lines;
        int end = FindSectionEnd(lines, start);
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = start + 1; i < end; i++)
        {
            Match m = ScalarAssignmentRegex.Match(lines[i]);
            if (m.Success && !TableOpenerRegex.IsMatch(lines[i]))
                existing.Add(m.Groups[2].Value);
        }

        var list = lines.ToList();
        int insertAt = end;
        foreach (string skill in new[] { "Dancing", "Meditation", "Music" })
        {
            if (existing.Contains(skill))
                continue;
            list.Insert(insertAt, $"        {skill} = 1,");
            insertAt++;
            fixes.Add($"MultiplierConfig.{skill} = 1 ergänzt.");
        }

        return list.ToArray();
    }

    private static string[] EnsureMultiplierSkillsFromReference(string[] lines, string[] refLines, List<string> fixes)
    {
        int start = FindTopLevelSectionStart(lines, "MultiplierConfig");
        int refStart = FindTopLevelSectionStart(refLines, "MultiplierConfig");
        if (start < 0 || refStart < 0)
            return EnsureMultiplierSkillsDefaults(lines, fixes);

        int end = FindSectionEnd(lines, start);
        int refEnd = FindSectionEnd(refLines, refStart);

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = start + 1; i < end; i++)
        {
            Match m = ScalarAssignmentRegex.Match(lines[i]);
            if (m.Success && !TableOpenerRegex.IsMatch(lines[i]))
                existing.Add(m.Groups[2].Value);
        }

        var list = lines.ToList();
        int insertAt = end;
        for (int i = refStart + 1; i < refEnd; i++)
        {
            Match m = ScalarAssignmentRegex.Match(refLines[i]);
            if (!m.Success || TableOpenerRegex.IsMatch(refLines[i]))
                continue;
            string key = m.Groups[2].Value;
            if (existing.Contains(key))
                continue;
            // Only auto-add the lifestyle skills that commonly get wiped; don't flood with all ref keys
            if (key is not ("Dancing" or "Meditation" or "Music"))
                continue;

            string indent = m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : "        ";
            string value = m.Groups[3].Value.Trim().TrimEnd(',');
            list.Insert(insertAt, $"{indent}{key} = {value},");
            insertAt++;
            existing.Add(key);
            fixes.Add($"MultiplierConfig.{key} aus Referenz ergänzt ({value}).");
        }

        return list.ToArray();
    }

    private static string[] EnsureVanillaSectionsFromReference(string[] lines, string[] refLines, List<string> fixes)
    {
        var list = lines.ToList();
        var present = GetTopLevelSections(lines);

        foreach (string section in VanillaSections)
        {
            if (present.Contains(section))
                continue;

            int refStart = FindTopLevelSectionStart(refLines, section);
            if (refStart < 0)
                continue;
            int refEnd = FindSectionEnd(refLines, refStart);
            // Insert before final closing of SandboxVars
            int insertAt = FindRootCloseIndex(list);
            if (insertAt < 0)
                insertAt = list.Count;

            for (int i = refStart; i <= refEnd; i++)
            {
                list.Insert(insertAt, refLines[i]);
                insertAt++;
            }

            fixes.Add($"Fehlende Sektion '{section}' aus Referenz eingefügt.");
            present.Add(section);
        }

        return list.ToArray();
    }

    private static int FindRootCloseIndex(List<string> lines)
    {
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (lines[i].Trim() == "}")
                return i;
        }
        return -1;
    }

    private static int FindTopLevelSectionStart(string[] lines, string name)
    {
        string pattern = @"^\s{4}" + Regex.Escape(name) + @"\s*=\s*\{\s*$";
        for (int i = 0; i < lines.Length; i++)
        {
            if (Regex.IsMatch(lines[i], pattern))
                return i;
        }

        // Fallback: any indent
        for (int i = 0; i < lines.Length; i++)
        {
            Match m = TableOpenerRegex.Match(lines[i]);
            if (m.Success && string.Equals(m.Groups[1].Value, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static int FindSectionEnd(string[] lines, int start)
    {
        // start points at "Name = {"
        int depth = 0;
        for (int i = start; i < lines.Length; i++)
        {
            string t = lines[i];
            foreach (char c in t)
            {
                if (c == '{') depth++;
                else if (c == '}') depth--;
            }

            if (i > start && depth <= 0)
                return i;
        }

        return lines.Length - 1;
    }

    private static HashSet<string> GetTopLevelSections(string[] lines)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines)
        {
            if (Regex.IsMatch(line, @"^\s{4}([A-Za-z_][A-Za-z0-9_]*)\s*=\s*\{\s*$"))
            {
                Match m = Regex.Match(line, @"^\s{4}([A-Za-z_][A-Za-z0-9_]*)\s*=");
                if (m.Success)
                    set.Add(m.Groups[1].Value);
            }
        }
        return set;
    }

    private static bool HasUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    private static string DecodeUtf8(byte[] bytes)
    {
        int offset = HasUtf8Bom(bytes) ? 3 : 0;
        return Utf8NoBom.GetString(bytes, offset, bytes.Length - offset);
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    private static int CountChar(string text, char c)
    {
        int n = 0;
        foreach (char ch in text)
        {
            if (ch == c) n++;
        }
        return n;
    }
}
