namespace ZomboidManager;

public class IniEntry
{
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Category { get; set; } = "Allgemein";
    public string InputType { get; set; } = "text";
    public string Description { get; set; } = string.Empty;
    public int Order { get; set; } = 100;
}

public static class IniManager
{
    public static Dictionary<string, string> ReadIni(string filePath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!File.Exists(filePath))
                throw new ArgumentException($"INI file not found: {filePath}", nameof(filePath));

            foreach (string line in File.ReadAllLines(filePath))
            {
                string trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;

                if (trimmed.StartsWith('#') || trimmed.StartsWith(';'))
                    continue;

                int separatorIndex = trimmed.IndexOf('=');
                if (separatorIndex < 0)
                    continue;

                string key = trimmed[..separatorIndex].Trim();
                string value = trimmed[(separatorIndex + 1)..].Trim();
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                result[key] = value;
            }
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"IniManager.ReadIni failed: {ex.Message}");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return result;
    }

    public static void WriteIni(string filePath, Dictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        List<string> lines = File.Exists(filePath)
            ? File.ReadAllLines(filePath).ToList()
            : new List<string>();

        var remaining = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
                continue;

            int separatorIndex = trimmed.IndexOf('=');
            if (separatorIndex < 0)
                continue;

            string key = trimmed[..separatorIndex].Trim();
            if (string.IsNullOrWhiteSpace(key))
                continue;

            if (!remaining.TryGetValue(key, out string? newValue))
                continue;

            lines[i] = $"{key}={newValue}";
            remaining.Remove(key);
        }

        foreach (KeyValuePair<string, string> pair in remaining)
            lines.Add($"{pair.Key}={pair.Value}");

        File.WriteAllLines(filePath, lines);
    }

    public static List<IniEntry> ParseToEntries(Dictionary<string, string> raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var entries = new List<IniEntry>();
        foreach (KeyValuePair<string, string> pair in raw)
        {
            string key = pair.Key;
            string value = pair.Value ?? string.Empty;
            IniCatalog.Meta meta = IniCatalog.Resolve(key, value);

            string inputType = meta.InputType;
            bool isBoolValue = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
            if (isBoolValue)
                inputType = "checkbox";

            entries.Add(new IniEntry
            {
                Key = key,
                DisplayName = meta.DisplayName,
                Value = value,
                Category = meta.Category,
                InputType = inputType,
                Description = key,
                Order = meta.Order
            });
        }

        return entries
            .OrderBy(e => IniCatalog.CategorySortIndex(e.Category))
            .ThenBy(e => e.Order)
            .ThenBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static (List<string> WorkshopIds, List<string> ModIds) ParseModLists(Dictionary<string, string> raw)
    {
        static List<string> Split(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? new List<string>()
                : value.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();

        raw.TryGetValue("WorkshopItems", out string? workshop);
        raw.TryGetValue("Mods", out string? mods);
        return (Split(workshop), Split(mods));
    }
}
