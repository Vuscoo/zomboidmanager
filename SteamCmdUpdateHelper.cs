using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ZomboidManager;

public sealed class SteamCmdUpdateCheckResult
{
    public bool NeedsUpdate { get; init; }
    public bool CheckSucceeded { get; init; }
    public string? LocalBuildId { get; init; }
    public string? RemoteBuildId { get; init; }
    public string? LocalGameVersion { get; init; }
    public string Branch { get; init; } = "public";
    public string Message { get; init; } = "";
}

/// <summary>
/// Pre-flight check: compare local appmanifest buildid with Steam depot before stopping the server.
/// B42 Stable (42.20.x) uses the default Steam branch "public" — not the legacy "42.19" unstable beta.
/// </summary>
public static class SteamCmdUpdateHelper
{
    public const string DedicatedServerAppId = "380870";
    private static readonly Regex BuildIdRegex = new(
        @"""buildid""\s+""(\d+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BetaBranchRegex = new(
        @"""beta""\s+""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ServerGeneralVersionRegex = new(
        @"LOG\s*:\s*General\b.*?\bversion=([\d.]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BranchDescriptionRegex = new(
        @"""description""\s+""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string NormalizeBranch(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
            return "public";

        string trimmed = branch.Trim();
        return string.Equals(trimmed, "public", StringComparison.OrdinalIgnoreCase)
            ? "public"
            : trimmed;
    }

    public static string GetEffectiveBranch(string? configBranch)
    {
        if (!string.IsNullOrWhiteSpace(configBranch))
            return NormalizeBranch(configBranch);

        // B42 Stable (42.20.x) is the public Steam branch — always default to latest stable.
        return "public";
    }

    public static string? GetInstalledBranchMismatchWarning(
        string serverPath,
        string targetBranch,
        string? uiLanguage = null)
    {
        string installed = ReadInstalledBranch(serverPath);
        string target = NormalizeBranch(targetBranch);
        if (string.Equals(installed, target, StringComparison.OrdinalIgnoreCase))
            return null;

        return AppLocalizer.Format(
            uiLanguage,
            "steamcmd.branchMismatch",
            installed,
            GetBranchDisplayLabel(target, uiLanguage));
    }

    public static string GetBranchDisplayLabel(string branch, string? uiLanguage = null)
    {
        string normalized = NormalizeBranch(branch);
        if (string.Equals(normalized, "public", StringComparison.OrdinalIgnoreCase))
            return AppLocalizer.Get(uiLanguage, "steamcmd.branch.b42Stable");

        if (string.Equals(normalized, "legacy41", StringComparison.OrdinalIgnoreCase))
            return AppLocalizer.Get(uiLanguage, "steamcmd.branch.build41");

        if (string.Equals(normalized, "42.19", StringComparison.OrdinalIgnoreCase))
            return AppLocalizer.Get(uiLanguage, "steamcmd.branch.b42Unstable");

        return AppLocalizer.Format(uiLanguage, "steamcmd.branch.beta", normalized);
    }

    public static string FormatCheckResultMessage(SteamCmdUpdateCheckResult result, string? uiLanguage = null)
    {
        if (!result.CheckSucceeded)
            return AppLocalizer.Get(uiLanguage, "steamcmd.checkUnclear");

        if (!result.NeedsUpdate)
        {
            if (string.IsNullOrWhiteSpace(result.LocalGameVersion))
                return AppLocalizer.Get(uiLanguage, "steamcmd.upToDate");
            return AppLocalizer.Format(uiLanguage, "steamcmd.upToDateVersion", result.LocalGameVersion);
        }

        if (string.IsNullOrWhiteSpace(result.LocalGameVersion))
            return AppLocalizer.Get(uiLanguage, "steamcmd.updateStartingNoVersion");
        return AppLocalizer.Format(uiLanguage, "steamcmd.updateStarting", result.LocalGameVersion);
    }

    public static string? ReadLocalBuildId(string serverPath)
    {
        if (string.IsNullOrWhiteSpace(serverPath))
            return null;

        string manifestPath = GetManifestPath(serverPath)!;

        if (!File.Exists(manifestPath))
            return null;

        try
        {
            string text = File.ReadAllText(manifestPath);
            Match match = BuildIdRegex.Match(text);
            return match.Success ? match.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }

    public static string ReadInstalledBranch(string serverPath)
    {
        string? manifestPath = GetManifestPath(serverPath);
        if (manifestPath == null || !File.Exists(manifestPath))
            return "public";

        try
        {
            string text = File.ReadAllText(manifestPath);
            Match match = BetaBranchRegex.Match(text);
            if (match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
                return NormalizeBranch(match.Groups[1].Value);
        }
        catch
        {
            // ignore
        }

        return "public";
    }

    public static string? ReadInstalledGameVersion(string serverPath, string? zomboidDataPath = null)
    {
        if (string.IsNullOrWhiteSpace(serverPath) || !Directory.Exists(serverPath))
            return null;

        DateTime? installFreshness = GetInstallFreshnessUtc(serverPath);

        string? fromServerLogs = ReadGameVersionFromLogs(Path.Combine(serverPath, "logs"), installFreshness);
        if (!string.IsNullOrWhiteSpace(fromServerLogs))
            return fromServerLogs;

        if (!string.IsNullOrWhiteSpace(zomboidDataPath) && Directory.Exists(zomboidDataPath))
        {
            string? fromDataLogs = ReadGameVersionFromLogs(Path.Combine(zomboidDataPath, "logs"), installFreshness);
            if (!string.IsNullOrWhiteSpace(fromDataLogs))
                return fromDataLogs;
        }

        return null;
    }

    private static DateTime? GetInstallFreshnessUtc(string serverPath)
    {
        string jarPath = Path.Combine(serverPath, "java", "projectzomboid.jar");
        if (File.Exists(jarPath))
            return File.GetLastWriteTimeUtc(jarPath);

        string? manifestPath = GetManifestPath(serverPath);
        if (manifestPath != null && File.Exists(manifestPath))
            return File.GetLastWriteTimeUtc(manifestPath);

        return null;
    }

    private static string? ReadGameVersionFromLogs(string logsDir, DateTime? minLogTimeUtc)
    {
        if (!Directory.Exists(logsDir))
            return null;

        try
        {
            IEnumerable<string> logs = Directory
                .EnumerateFiles(logsDir, "*DebugLog-server.txt", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc);

            foreach (string logPath in logs)
            {
                if (minLogTimeUtc.HasValue && File.GetLastWriteTimeUtc(logPath) < minLogTimeUtc.Value)
                    continue;

                foreach (string line in File.ReadLines(logPath))
                {
                    Match match = ServerGeneralVersionRegex.Match(line);
                    if (match.Success)
                        return match.Groups[1].Value;
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static string? GetManifestPath(string serverPath) =>
        string.IsNullOrWhiteSpace(serverPath)
            ? null
            : Path.Combine(serverPath, "steamapps", $"appmanifest_{DedicatedServerAppId}.acf");

    public static async Task<SteamCmdUpdateCheckResult> CheckUpdateNeededAsync(
        string serverPath,
        string steamCmdPath,
        string? configBranch = null,
        string? zomboidDataPath = null,
        string? uiLanguage = null,
        Action<string>? consoleLine = null)
    {
        if (string.IsNullOrWhiteSpace(serverPath) || !Directory.Exists(serverPath))
            return MakeResult(true, false, "public", null, null, null, uiLanguage);

        if (!File.Exists(steamCmdPath))
            return MakeResult(true, false, "public", null, null, null, uiLanguage);

        string branch = GetEffectiveBranch(configBranch);
        string? localVersion = ReadInstalledGameVersion(serverPath, zomboidDataPath);
        string? localBuild = ReadLocalBuildId(serverPath);

        if (string.IsNullOrWhiteSpace(localBuild))
            return MakeResult(true, true, branch, null, null, localVersion, uiLanguage);

        (int _, string steamCheckOutput) = await RunSteamCmdCaptureAsync(
            steamCmdPath,
            BuildCheckArguments(serverPath, branch),
            consoleLine);

        bool steamSaysUpToDate = ParseSteamSaysUpToDate(steamCheckOutput);
        bool steamWillDownload = ParseSteamWillDownload(steamCheckOutput);

        if (steamSaysUpToDate && !steamWillDownload)
            return MakeResult(false, true, branch, localBuild, localBuild, localVersion, uiLanguage);

        if (steamWillDownload || !steamSaysUpToDate)
            return MakeResult(true, true, branch, localBuild, null, localVersion, uiLanguage);

        (bool buildOk, string? remoteBuild, string? _, string _) = await FetchRemoteBuildInfoAsync(
            steamCmdPath,
            branch,
            consoleLine);

        if (!buildOk || string.IsNullOrWhiteSpace(remoteBuild))
            return MakeResult(true, false, branch, localBuild, remoteBuild, localVersion, uiLanguage);

        bool needsUpdate = !string.Equals(localBuild, remoteBuild, StringComparison.Ordinal);
        return MakeResult(needsUpdate, true, branch, localBuild, remoteBuild, localVersion, uiLanguage);
    }

    private static SteamCmdUpdateCheckResult MakeResult(
        bool needsUpdate,
        bool checkSucceeded,
        string branch,
        string? localBuild,
        string? remoteBuild,
        string? localVersion,
        string? uiLanguage)
    {
        var scratch = new SteamCmdUpdateCheckResult
        {
            NeedsUpdate = needsUpdate,
            CheckSucceeded = checkSucceeded,
            Branch = branch,
            LocalBuildId = localBuild,
            RemoteBuildId = remoteBuild,
            LocalGameVersion = localVersion,
            Message = ""
        };
        return new SteamCmdUpdateCheckResult
        {
            NeedsUpdate = needsUpdate,
            CheckSucceeded = checkSucceeded,
            Branch = branch,
            LocalBuildId = localBuild,
            RemoteBuildId = remoteBuild,
            LocalGameVersion = localVersion,
            Message = FormatCheckResultMessage(scratch, uiLanguage)
        };
    }

    private static bool ParseSteamSaysUpToDate(string output) =>
        !string.IsNullOrWhiteSpace(output)
        && output.Contains("already up to date", StringComparison.OrdinalIgnoreCase);

    private static bool ParseSteamWillDownload(string output) =>
        !string.IsNullOrWhiteSpace(output)
        && output.Contains("Update state", StringComparison.OrdinalIgnoreCase);

    public static string BuildUpdateArguments(string serverPath, string branch = "public")
    {
        string normalized = NormalizeBranch(branch);
        string betaArg = string.Equals(normalized, "public", StringComparison.OrdinalIgnoreCase)
            ? ""
            : " -beta " + normalized;
        return "+force_install_dir \"" + serverPath + "\" +login anonymous +app_update "
            + DedicatedServerAppId + betaArg + " validate +quit";
    }

    /// <summary>Lightweight SteamCMD check — app_update without validate.</summary>
    public static string BuildCheckArguments(string serverPath, string branch = "public")
    {
        string normalized = NormalizeBranch(branch);
        string betaArg = string.Equals(normalized, "public", StringComparison.OrdinalIgnoreCase)
            ? ""
            : " -beta " + normalized;
        return "+force_install_dir \"" + serverPath + "\" +login anonymous +app_update "
            + DedicatedServerAppId + betaArg + " +quit";
    }

    private static async Task<(bool Ok, string? BuildId, string? Description, string Output)> FetchRemoteBuildInfoAsync(
        string steamCmdPath,
        string branch,
        Action<string>? consoleLine)
    {
        string arguments = "+login anonymous +app_info_print " + DedicatedServerAppId + " +quit";
        (int exitCode, string output) = await RunSteamCmdCaptureAsync(steamCmdPath, arguments, consoleLine);

        if (string.IsNullOrWhiteSpace(output))
            return (false, null, null, output);

        string? remoteBuild = ParseBranchBuildId(output, branch);
        string? description = ParseBranchDescription(output, branch);
        if (string.IsNullOrWhiteSpace(remoteBuild))
            return (false, null, description, output);

        bool ok = exitCode == 0 || !string.IsNullOrWhiteSpace(remoteBuild);
        return (ok, remoteBuild, description, output);
    }

    private static string? ParseBranchBuildId(string output, string branch)
    {
        int branchesIdx = output.IndexOf("\"branches\"", StringComparison.OrdinalIgnoreCase);
        if (branchesIdx < 0)
            return null;

        string branchKey = "\"" + NormalizeBranch(branch) + "\"";
        int branchIdx = output.IndexOf(branchKey, branchesIdx, StringComparison.OrdinalIgnoreCase);
        if (branchIdx < 0)
            return null;

        int sliceLen = Math.Min(400, output.Length - branchIdx);
        string slice = output.Substring(branchIdx, sliceLen);
        Match match = BuildIdRegex.Match(slice);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ParseBranchDescription(string output, string branch)
    {
        int branchesIdx = output.IndexOf("\"branches\"", StringComparison.OrdinalIgnoreCase);
        if (branchesIdx < 0)
            return null;

        string branchKey = "\"" + NormalizeBranch(branch) + "\"";
        int branchIdx = output.IndexOf(branchKey, branchesIdx, StringComparison.OrdinalIgnoreCase);
        if (branchIdx < 0)
            return null;

        int sliceLen = Math.Min(500, output.Length - branchIdx);
        string slice = output.Substring(branchIdx, sliceLen);
        Match match = BranchDescriptionRegex.Match(slice);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static async Task<(int ExitCode, string CombinedOutput)> RunSteamCmdCaptureAsync(
        string steamCmdPath,
        string arguments,
        Action<string>? consoleLine = null,
        string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = steamCmdPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        string? steamDir = Path.GetDirectoryName(steamCmdPath);
        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
            startInfo.WorkingDirectory = workingDirectory;
        else if (!string.IsNullOrWhiteSpace(steamDir) && Directory.Exists(steamDir))
            startInfo.WorkingDirectory = steamDir;

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var outputLines = new List<string>();

        void HandleLine(string? line)
        {
            if (string.IsNullOrEmpty(line))
                return;
            outputLines.Add(line);
            consoleLine?.Invoke(line);
        }

        process.OutputDataReceived += (_, e) => HandleLine(e.Data);
        process.ErrorDataReceived += (_, e) => HandleLine(e.Data);

        if (!process.Start())
            return (-1, string.Empty);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        return (process.ExitCode, string.Join('\n', outputLines));
    }
}
