using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZomboidManager;

public sealed class LogEntry
{
    public int Index { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string Timestamp { get; set; } = "";
    public string Level { get; set; } = "";
    public string Category { get; set; } = "";
    public string Message { get; set; } = "";
    public string Location { get; set; } = "";
    public string Raw { get; set; } = "";
}

public sealed class LogSystemSpec
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class LogLoadedMod
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string WorkshopId { get; set; } = "";
}

public sealed class LogFileOverride
{
    public string ModId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed class ConfiguredModRef
{
    public string WorkshopId { get; set; } = "";
    public string ModId { get; set; } = "";
}

/// <summary>
/// Parses Project Zomboid DebugLog / console / chat / connections lines (Build 41–42).
/// </summary>
public static class PzLogParser
{
    private static readonly Regex TimestampedLevel = new(
        @"^\[(\d{2}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(?:\.\d{1,3})?)\]\s+(LOG|WARN|ERROR)\s*:\s+(\S+)\s*(.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BareLevel = new(
        @"^(LOG|WARN|ERROR)\s*:\s+(\S+)\s*(.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ChatLine = new(
        @"^\[(\d{2}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(?:\.\d{1,3})?)\]\[(info|warn|error)\]\s*(.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex TimestampedOther = new(
        @"^\[(\d{2}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(?:\.\d{1,3})?)\]\s*(.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // f:/st: may use ',' or '.' as thousands separators (server locale).
    private static readonly Regex MessageTail = new(
        @"^\s*(?:f:[\d.,]+)?(?:,\s*t:\d+)?(?:\s+st:[\d.,]+)?(?:\s+at\s+(.*?))?\s*>\s*(.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex OldCommaEpoch = new(
        @"^\s*,\s*\d+>\s*(.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex EventName = new(
        @"event=""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex LoadingMod = new(
        @"^loading (.+?)\.?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex OverrideLine = new(
        @"^mod ""([^""]+)"" overrides (.+?)\.?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static List<LogEntry> ParseLines(IReadOnlyList<string> lines, CancellationToken ct = default)
    {
        var entries = new List<LogEntry>(Math.Max(256, lines.Count / 2));
        LogEntry? current = null;

        for (int i = 0; i < lines.Count; i++)
        {
            if ((i & 1023) == 0)
                ct.ThrowIfCancellationRequested();
            string line = StripLineJunk(lines[i]);
            int lineNo = i + 1;

            if (TryParseHeader(line, out LogEntry parsed))
            {
                current = parsed;
                current.Index = entries.Count;
                current.StartLine = lineNo;
                current.EndLine = lineNo;
                current.Raw = line;
                entries.Add(current);
                continue;
            }

            if (current is not null)
            {
                current.EndLine = lineNo;
                current.Raw += "\n" + line;
                if (!string.IsNullOrWhiteSpace(line))
                {
                    if (current.Message.Length > 0)
                        current.Message += "\n" + line;
                    else
                        current.Message = line;
                }
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
                continue;

            current = new LogEntry
            {
                Index = entries.Count,
                StartLine = lineNo,
                EndLine = lineNo,
                Level = "LOG",
                Category = "Other",
                Message = line,
                Raw = line
            };
            entries.Add(current);
        }

        return entries;
    }

    public static bool TryParseHeader(string line, out LogEntry entry)
    {
        entry = new LogEntry();
        if (string.IsNullOrWhiteSpace(line))
            return false;

        Match m = TimestampedLevel.Match(line);
        if (m.Success)
        {
            SplitRemainder(m.Groups[4].Value, out string location, out string message);
            entry.Timestamp = m.Groups[1].Value;
            entry.Level = m.Groups[2].Value;
            entry.Category = m.Groups[3].Value;
            entry.Location = location;
            entry.Message = message;
            entry.Raw = line;
            return true;
        }

        m = ChatLine.Match(line);
        if (m.Success)
        {
            entry.Timestamp = m.Groups[1].Value;
            entry.Level = MapChatLevel(m.Groups[2].Value);
            entry.Category = "Chat";
            entry.Message = m.Groups[3].Value.Trim();
            entry.Raw = line;
            return true;
        }

        m = TimestampedOther.Match(line);
        if (m.Success)
        {
            string rest = m.Groups[2].Value.Trim();
            Match ev = EventName.Match(rest);
            entry.Timestamp = m.Groups[1].Value;
            entry.Level = "LOG";
            entry.Category = ev.Success ? ev.Groups[1].Value : "Other";
            entry.Message = rest;
            entry.Raw = line;
            return true;
        }

        m = BareLevel.Match(line);
        if (m.Success)
        {
            SplitRemainder(m.Groups[3].Value, out string location, out string message);
            entry.Level = m.Groups[1].Value;
            entry.Category = m.Groups[2].Value;
            entry.Location = location;
            entry.Message = message;
            entry.Raw = line;
            return true;
        }

        return false;
    }

    public static List<LogSystemSpec> ExtractSystemInfo(IEnumerable<LogEntry> entries)
    {
        var specs = new List<LogSystemSpec>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int scanned = 0;

        foreach (LogEntry entry in entries)
        {
            scanned++;
            if (scanned > 800)
                break;

            string msg = NormalizeMessage(entry.Message);
            if (msg.Length == 0)
                continue;

            if (TryTakePrefixed(msg, "CPU:", "CPU", specs, seen)) continue;
            if (TryTakePrefixed(msg, "RAM:", "RAM", specs, seen)) continue;
            if (TryTakePrefixed(msg, "Base board:", "Base board", specs, seen)) continue;
            if (TryTakePrefixed(msg, "GPU:", "GPU", specs, seen)) continue;
            if (TryTakePrefixed(msg, "Disk info:", "Disk", specs, seen)) continue;
            if (TryTakePrefixed(msg, "OS:", "OS", specs, seen)) continue;
            if (TryTakePrefixed(msg, "JVM ", "JVM", specs, seen)) continue;
            if (TryTakePrefixed(msg, "GraphicsCard:", "Graphics card", specs, seen)) continue;
            if (TryTakePrefixed(msg, "OpenGL version:", "OpenGL", specs, seen)) continue;
            if (TryTakePrefixed(msg, "Desktop resolution", "Desktop resolution", specs, seen)) continue;
            if (TryTakeEquals(msg, "java.runtime.version", "Java runtime", specs, seen)) continue;
            if (TryTakeEquals(msg, "java.version", "Java version", specs, seen)) continue;
            if (TryTakeEquals(msg, "java.vm.vendor", "JVM vendor", specs, seen)) continue;
            if (TryTakeEquals(msg, "java.vendor", "Java vendor", specs, seen)) continue;
            if (TryTakeEquals(msg, "java.vm.name", "JVM name", specs, seen)) continue;
            if (TryTakeEquals(msg, "os.name", "OS name", specs, seen)) continue;
            if (TryTakeEquals(msg, "os.arch", "Architecture", specs, seen)) continue;
            if (TryTakeEquals(msg, "java.home", "Java home", specs, seen)) continue;

            if (msg.StartsWith("version=", StringComparison.OrdinalIgnoreCase)
                && msg.Contains("demo=", StringComparison.OrdinalIgnoreCase))
            {
                AddSpec("Game version", msg["version=".Length..].Trim().TrimEnd('.'), specs, seen);
            }
            else if (msg.StartsWith("revision=", StringComparison.OrdinalIgnoreCase))
            {
                AddSpec("Revision", msg["revision=".Length..].Trim().TrimEnd('.'), specs, seen);
            }
        }

        return specs;
    }

    public static (List<LogLoadedMod> Mods, List<LogFileOverride> Overrides) ExtractModInfo(
        IEnumerable<LogEntry> entries,
        IReadOnlyList<ConfiguredModRef> configured)
    {
        var mods = new List<LogLoadedMod>();
        var overrides = new List<LogFileOverride>();
        var seenMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (LogEntry entry in entries)
        {
            if (!string.Equals(entry.Category, "Mod", StringComparison.OrdinalIgnoreCase)
                && !entry.Message.StartsWith("loading ", StringComparison.OrdinalIgnoreCase)
                && !entry.Message.StartsWith("mod \"", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string msg = NormalizeMessage(entry.Message);
            if (msg.StartsWith("texturepack:", StringComparison.OrdinalIgnoreCase))
                continue;

            Match load = LoadingMod.Match(msg);
            if (load.Success)
            {
                string id = load.Groups[1].Value.Trim().TrimEnd('.');
                if (id.Length == 0 || !seenMods.Add(id))
                    continue;
                (string display, string workshop) = ResolveModName(id, configured);
                mods.Add(new LogLoadedMod { Id = id, DisplayName = display, WorkshopId = workshop });
                continue;
            }

            Match ov = OverrideLine.Match(msg);
            if (ov.Success)
            {
                string id = ov.Groups[1].Value.Trim();
                string path = ov.Groups[2].Value.Trim().TrimEnd('.');
                (string display, _) = ResolveModName(id, configured);
                overrides.Add(new LogFileOverride { ModId = id, DisplayName = display, Path = path });
            }
        }

        return (mods, overrides);
    }

    public static void RunSelfTest()
    {
        string sample = string.Join("\n", new[]
        {
            "[12-08-26 20:33:52.520] LOG  : General      f:0> ===== System specs =====.",
            "[12-08-26 20:33:52.520] LOG  : General      f:0> CPU: AMD Ryzen 7 5700X 8-Core Processor, vendor: AuthenticAMD, cores: 8, threads: 16.",
            "[12-08-26 20:33:52.526] LOG  : General      f:0> RAM: 65459 Mb.",
            "[12-08-26 20:33:52.666] LOG  : General      f:0> OS: Windows 11, version: 10.0, arch: amd64, build: 26200.",
            "[12-08-26 20:33:52.673] LOG  : General      f:0> java.runtime.version=25.0.1+8-LTS.",
            "[12-08-26 20:33:52.687] LOG  : General      f:0> version=42.20.2 ffe7a8a4b1 demo=false.",
            "[12-08-26 20:34:04.116] WARN : Lua          f:0 at Lua(Vanilla).corpseStorageCheck     > require(\"ISUI/ISInventoryPaneContextMenu\") failed.",
            "[12-08-26 20:34:10.185] ERROR: Mod          f:0 at ChooseGameInfo.readModInfoAux       > tiledef=fe_def 15000 file number must be from 100 to 8189.",
            "[12-08-26 20:34:26.645] LOG  : Mod          f:0 st:0> loading ModLoadOrderSorter_b42.",
            "[12-08-26 20:34:26.646] LOG  : Mod          f:0 st:0> mod \"ModLoadOrderSorter_b42\" overrides media/lua/shared/translate/en/ui.json.",
            "[12-08-26 20:34:37.671] ERROR: General      f:0 st:0> AdvancedAnimator$1.visitFileFailed> Exception thrown",
            "\tjava.nio.file.NoSuchFileException: D:\\workshop\\mods\\common\\media\\AnimSets at WindowsException.translateToIOException(null:-1).",
            "\tStack trace:",
            "\t\tjava.base/sun.nio.fs.WindowsException.translateToIOException(Unknown Source)",
            "\t\tjava.base/java.lang.Thread.run(Unknown Source).",
            "LOG  : General      f:0> console line without timestamp.",
            "[12-08-26 20:34:20.522] event=\"RakNet\" message=\"connection-request-accepted\" guid=540432542881363381.",
            "[12-08-26 20:35:47.781][info] Init chat system....",
            "[12-08-26 20:35:47.783][warn] Got message from server: hello.",
            "[28-02-26 14:03:14.615] LOG  : General      f:0, t:1772283794564> 28-02-2026 14:03:14.",
            "[05-03-26 06:55:38.968] WARN : General     , 1772690138968> TextManager.Init> font \"MediumNew\" not found in fonts.txt."
        });

        string[] lines = sample.Replace("\r\n", "\n").Split('\n');
        List<LogEntry> entries = ParseLines(lines);

        LogEntry? stack = entries.FirstOrDefault(e => e.Message.Contains("Exception thrown", StringComparison.Ordinal));
        LogEntry? luaWarn = entries.FirstOrDefault(e => e.Category == "Lua");
        LogEntry? console = entries.FirstOrDefault(e => e.Message.Contains("console line without timestamp", StringComparison.Ordinal));
        LogEntry? chat = entries.FirstOrDefault(e => e.Category == "Chat" && e.Level == "WARN");
        LogEntry? conn = entries.FirstOrDefault(e => e.Category == "RakNet");
        LogEntry? oldT = entries.FirstOrDefault(e => e.Message.Contains("28-02-2026", StringComparison.Ordinal));
        LogEntry? oldComma = entries.FirstOrDefault(e => e.Message.Contains("TextManager.Init", StringComparison.Ordinal));

        var configured = new List<ConfiguredModRef>
        {
            new() { ModId = "ModLoadOrderSorter_b42", WorkshopId = "3423660713" }
        };
        (List<LogLoadedMod> mods, List<LogFileOverride> overrides) = ExtractModInfo(entries, configured);
        List<LogSystemSpec> specs = ExtractSystemInfo(entries);

        bool ok =
            entries.Count >= 16
            && stack is not null
            && stack.Level == "ERROR"
            && stack.Raw.Contains("Stack trace:", StringComparison.Ordinal)
            && stack.StartLine < stack.EndLine
            && luaWarn is { Level: "WARN", Category: "Lua" }
            && console is { Level: "LOG", Category: "General" }
            && chat is { Level: "WARN" }
            && conn is not null
            && oldT is { Level: "LOG" }
            && oldComma is { Level: "WARN" }
            && mods.Any(m => m.Id == "ModLoadOrderSorter_b42" && m.WorkshopId == "3423660713")
            && overrides.Count >= 1
            && specs.Any(s => s.Label == "CPU")
            && specs.Any(s => s.Label == "OS")
            && specs.Any(s => s.Label == "Java runtime");

        var result = new
        {
            ok,
            entryCount = entries.Count,
            stackLines = stack is null ? 0 : stack.EndLine - stack.StartLine + 1,
            mods = mods.Count,
            overrides = overrides.Count,
            specs = specs.Select(s => s.Label).ToArray()
        };

        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

        string realLog = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Zomboid", "Logs", "2026-08-12_20-33_DebugLog.txt");
        if (File.Exists(realLog))
        {
            string[] realLines = File.ReadAllLines(realLog);
            List<LogEntry> real = ParseLines(realLines);
            int errors = real.Count(e => e.Level == "ERROR");
            int warns = real.Count(e => e.Level == "WARN");
            int stacks = real.Count(e => e.EndLine > e.StartLine);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                realFile = realLog,
                lines = realLines.Length,
                entries = real.Count,
                errors,
                warns,
                multiline = stacks,
                categories = real.Select(e => e.Category).Distinct().OrderBy(c => c).ToArray()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        Environment.Exit(ok ? 0 : 1);
    }

    private static void SplitRemainder(string remainder, out string location, out string message)
    {
        location = "";
        remainder = remainder ?? "";
        Match tail = MessageTail.Match(remainder);
        if (tail.Success)
        {
            location = tail.Groups[1].Success ? tail.Groups[1].Value.Trim() : "";
            message = NormalizeMessage(tail.Groups[2].Value);
            return;
        }

        Match old = OldCommaEpoch.Match(remainder);
        if (old.Success)
        {
            message = NormalizeMessage(old.Groups[1].Value);
            return;
        }

        message = NormalizeMessage(remainder);
    }

    private static string MapChatLevel(string raw)
    {
        if (raw.Equals("warn", StringComparison.OrdinalIgnoreCase))
            return "WARN";
        if (raw.Equals("error", StringComparison.OrdinalIgnoreCase))
            return "ERROR";
        return "LOG";
    }

    private static string StripLineJunk(string line)
    {
        if (string.IsNullOrEmpty(line))
            return "";
        return line.TrimEnd('\r');
    }

    private static string NormalizeMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
            return "";
        return message.Replace("\r", "").Trim();
    }

    private static bool TryTakePrefixed(
        string msg, string prefix, string label,
        List<LogSystemSpec> specs, HashSet<string> seen)
    {
        if (!msg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        string value = msg[prefix.Length..].Trim().TrimEnd('.');
        AddSpec(label, value, specs, seen);
        return true;
    }

    private static bool TryTakeEquals(
        string msg, string key, string label,
        List<LogSystemSpec> specs, HashSet<string> seen)
    {
        if (!msg.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            return false;
        string value = msg[(key.Length + 1)..].Trim().TrimEnd('.');
        AddSpec(label, value, specs, seen);
        return true;
    }

    private static void AddSpec(string label, string value, List<LogSystemSpec> specs, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(value) || !seen.Add(label))
            return;
        specs.Add(new LogSystemSpec { Label = label, Value = value });
    }

    private static (string Display, string WorkshopId) ResolveModName(
        string logId, IReadOnlyList<ConfiguredModRef> configured)
    {
        foreach (ConfiguredModRef mod in configured)
        {
            if (!string.IsNullOrWhiteSpace(mod.ModId)
                && string.Equals(mod.ModId, logId, StringComparison.OrdinalIgnoreCase))
            {
                return (mod.ModId, mod.WorkshopId);
            }
        }

        foreach (ConfiguredModRef mod in configured)
        {
            if (!string.IsNullOrWhiteSpace(mod.ModId)
                && (logId.Contains(mod.ModId, StringComparison.OrdinalIgnoreCase)
                    || mod.ModId.Contains(logId, StringComparison.OrdinalIgnoreCase)))
            {
                return (mod.ModId, mod.WorkshopId);
            }
        }

        return (logId, "");
    }
}
