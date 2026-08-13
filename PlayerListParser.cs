using System.Text.RegularExpressions;

namespace ZomboidManager;

public static class PlayerListParser
{
    private static readonly Regex SteamIdRegex = new(
        @"\b(\d{17})\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses Project Zomboid RCON "players" output into structured entries.
    /// Formats vary; we extract names and Steam IDs when present.
    /// </summary>
    public static List<PlayerInfo> Parse(string? raw)
    {
        var result = new List<PlayerInfo>();
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        string text = raw.Trim();
        if (text.StartsWith("RCON error", StringComparison.OrdinalIgnoreCase))
            return result;

        // Split by commas / newlines / semicolons after stripping the header line.
        string body = text;
        int colon = text.IndexOf(':');
        if (colon >= 0 && colon < 80)
            body = text[(colon + 1)..];

        string[] chunks = body.Split(new[] { ',', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string chunk in chunks)
        {
            string piece = chunk.Trim();
            if (string.IsNullOrWhiteSpace(piece))
                continue;
            if (piece.Contains("Players connected", StringComparison.OrdinalIgnoreCase))
                continue;
            if (piece.Equals("none", StringComparison.OrdinalIgnoreCase)
                || piece.Equals("0", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? steamId = null;
            Match idMatch = SteamIdRegex.Match(piece);
            if (idMatch.Success)
                steamId = idMatch.Groups[1].Value;

            string name = SteamIdRegex.Replace(piece, "").Trim();
            name = name.Trim('(', ')', '[', ']', '-', ' ', '\t');
            // Common "Name (steamid)" already stripped
            if (string.IsNullOrWhiteSpace(name) && steamId is not null)
                name = steamId;

            if (string.IsNullOrWhiteSpace(name))
                continue;

            // Avoid duplicates
            if (result.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                continue;

            result.Add(new PlayerInfo
            {
                Name = name,
                SteamId = steamId ?? string.Empty,
                Raw = piece
            });
        }

        // Fallback: if nothing parsed but response looks non-empty and not "0", keep raw line.
        if (result.Count == 0
            && !string.IsNullOrWhiteSpace(text)
            && !text.Equals("0", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("Players connected (0)", StringComparison.OrdinalIgnoreCase))
        {
            result.Add(new PlayerInfo { Name = text, Raw = text });
        }

        return result;
    }
}

public sealed class PlayerInfo
{
    public string Name { get; set; } = string.Empty;
    public string SteamId { get; set; } = string.Empty;
    public string Raw { get; set; } = string.Empty;
}
