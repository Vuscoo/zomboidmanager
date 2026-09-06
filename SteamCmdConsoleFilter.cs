using System.Text.RegularExpressions;

namespace ZomboidManager;

/// <summary>Reduces noisy SteamCMD output in the server console.</summary>
internal static class SteamCmdConsoleFilter
{
    private static readonly Regex ProgressRegex = new(
        @"progress:\s*([\d.]+)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static int _lastProgressBucket = -1;

    public static void ResetProgressTracking() => _lastProgressBucket = -1;

    public static bool ShouldForwardToConsole(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        string trimmed = line.Trim();

        if (trimmed.StartsWith("Redirecting stderr", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Logging directory", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Suche nach verfügbaren Updates", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Installation wird überprüft", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Steam Console Client", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Loading Steam API", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Unloading Steam API", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("-- type 'quit'", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("[----]", StringComparison.Ordinal))
            return false;

        if (trimmed.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Success!", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!trimmed.Contains("Update state", StringComparison.OrdinalIgnoreCase))
            return false;

        Match match = ProgressRegex.Match(trimmed);
        if (!match.Success)
            return true;

        if (!double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double pct))
            return false;

        int bucket = (int)(pct / 25);
        if (bucket <= _lastProgressBucket)
            return false;

        _lastProgressBucket = bucket;
        return true;
    }

    public static string? FormatProgressLine(string line)
    {
        Match match = ProgressRegex.Match(line);
        if (!match.Success)
            return null;

        if (!double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double pct))
            return null;

        return $"SteamCMD: {pct:F0}%";
    }
}
