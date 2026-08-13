using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZomboidManager;

/// <summary>
/// One-click portable update: downloads the new EXE from GitHub Releases and swaps it in place.
/// UI lives embedded in the EXE (extracted to LocalAppData), so no ZIP is involved for users.
/// </summary>
internal static class AppUpdater
{
    public const string GitHubOwner = "Vuscoo";
    public const string GitHubRepo = "zomboidmanager";
    public const string ReleasesPageUrl = $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases";
    public const string LatestReleaseApiUrl =
        $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";

    private static readonly HttpClient ApiHttp = CreateApiHttpClient();
    private static readonly HttpClient DownloadHttp = CreateDownloadHttpClient();

    public sealed record CheckResult(
        bool UpdateAvailable,
        string? LatestTag,
        string? DownloadUrl,
        string? AssetName,
        string Message);

    public static async Task<CheckResult> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        using HttpResponseMessage response = await ApiHttp.GetAsync(LatestReleaseApiUrl, ct);
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new CheckResult(false, null, null, null,
                    "No GitHub release found yet. Publish a release with ZomboidManager.exe first.");
            }

            return new CheckResult(false, null, null, null,
                $"Update check failed ({(int)response.StatusCode}): {Truncate(body, 180)}");
        }

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        JsonElement root = document.RootElement;

        string tagName = root.TryGetProperty("tag_name", out JsonElement tagEl)
            ? tagEl.GetString() ?? string.Empty
            : string.Empty;
        string latest = NormalizeVersion(tagName);
        string current = NormalizeVersion(currentVersion);

        if (!TryParseVersion(latest, out Version latestVer) || !TryParseVersion(current, out Version currentVer))
        {
            return new CheckResult(false, tagName, null, null,
                $"Could not compare versions (current={currentVersion}, latest={tagName}).");
        }

        if (latestVer <= currentVer)
        {
            return new CheckResult(false, tagName, null, null, "You already have the latest version.");
        }

        if (!TryPickExeAsset(root, out string assetName, out string downloadUrl))
        {
            return new CheckResult(true, tagName, null, null,
                $"Update {tagName} is available, but ZomboidManager.exe was not found on the release.");
        }

        return new CheckResult(true, tagName, downloadUrl, assetName, $"Update available: {tagName}");
    }

    public static async Task DownloadAsync(
        string downloadUrl,
        string destinationFile,
        Action<int>? progress = null,
        CancellationToken ct = default)
    {
        using HttpResponseMessage response = await DownloadHttp.GetAsync(
            downloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        long readTotal;

        // Scope the write stream so it is fully closed before we re-open for verification
        // (and before the detached updater copies the file).
        await using (Stream input = await response.Content.ReadAsStreamAsync(ct))
        await using (FileStream output = new(
            destinationFile,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true))
        {
            byte[] buffer = new byte[81920];
            readTotal = 0;
            int lastPercent = -1;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                readTotal += read;
                if (total is > 0)
                {
                    int percent = (int)Math.Clamp(readTotal * 100 / total.Value, 0, 100);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress?.Invoke(percent);
                    }
                }
            }

            await output.FlushAsync(ct);
        }

        progress?.Invoke(100);

        if (readTotal < 1024 * 1024)
            throw new InvalidOperationException("Downloaded update file is too small to be a valid application build.");

        await using (FileStream verify = new(destinationFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Span<byte> magic = stackalloc byte[2];
            int magicRead = verify.Read(magic);
            if (magicRead < 2 || magic[0] != (byte)'M' || magic[1] != (byte)'Z')
                throw new InvalidOperationException("Downloaded update file is not a valid Windows executable.");
        }
    }

    /// <summary>
    /// Starts a detached updater that survives this process exiting, then replaces the EXE and relaunches.
    /// </summary>
    public static void ApplyExeAndRestart(string downloadedExePath, string targetExePath, int processId)
    {
        string logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZomboidManager",
            "update-error.log");

        string scriptPath = Path.Combine(
            Path.GetTempPath(),
            $"zomboidmanager-update-{Guid.NewGuid():N}.cmd");

        // cmd.exe is more reliable than PowerShell here: must outlive the WinForms process.
        string script = $"""
@echo off
setlocal EnableExtensions
set "LOG={logPath}"
set "SRC={downloadedExePath}"
set "DST={targetExePath}"
set "PID={processId}"

call :log started pid=%PID%
call :log src=%SRC%
call :log dst=%DST%

:wait
tasklist /FI "PID eq %PID%" 2>nul | findstr /I /C:"%PID%" >nul
if not errorlevel 1 (
  timeout /t 1 /nobreak >nul
  goto wait
)

timeout /t 1 /nobreak >nul

set /a tries=0
:copy
set /a tries+=1
copy /Y "%SRC%" "%DST%" >nul
if not errorlevel 1 goto launch
if %tries% GEQ 60 (
  call :log copy_failed_after_retries
  goto cleanup
)
timeout /t 1 /nobreak >nul
goto copy

:launch
call :log copy_ok
start "" "%DST%"
if errorlevel 1 call :log start_failed
goto cleanup

:cleanup
del /F /Q "%SRC%" >nul 2>nul
del /F /Q "%~f0" >nul 2>nul
exit /b 0

:log
>>"%LOG%" echo [%date% %time%] %*
exit /b 0
""";

        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(scriptPath, script, Encoding.ASCII);

        // "cmd /c start" creates a fully detached process that is not killed when we exit.
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c start \"ZomboidManagerUpdate\" /min \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath()
        });
    }

    public static string ResolveRunningExePath()
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
            return Path.GetFullPath(processPath);

        string executablePath = Application.ExecutablePath;
        if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
            return Path.GetFullPath(executablePath);

        return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ZomboidManager.exe"));
    }

    private static bool TryPickExeAsset(JsonElement root, out string assetName, out string downloadUrl)
    {
        assetName = string.Empty;
        downloadUrl = string.Empty;
        if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
            return false;

        List<(string Name, string Url)> exes = new();
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string name = asset.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
            string url = asset.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                continue;
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;
            if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                continue;
            exes.Add((name, url));
        }

        if (exes.Count == 0)
            return false;

        (string Name, string Url) pick = exes.FirstOrDefault(z =>
            z.Name.Equals("ZomboidManager.exe", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(pick.Name))
        {
            pick = exes.FirstOrDefault(z =>
                z.Name.Contains("ZomboidManager", StringComparison.OrdinalIgnoreCase));
        }
        if (string.IsNullOrEmpty(pick.Name))
            pick = exes[0];

        assetName = pick.Name;
        downloadUrl = pick.Url;
        return true;
    }

    public static string NormalizeVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return "0.0.0";
        string v = version.Trim();
        if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            v = v[1..];
        int plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];
        int dash = v.IndexOf('-');
        if (dash >= 0) v = v[..dash];
        return v;
    }

    private static bool TryParseVersion(string version, out Version result)
    {
        result = new Version(0, 0, 0);
        string[] parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return false;
        try
        {
            int major = int.Parse(parts[0], CultureInfo.InvariantCulture);
            int minor = int.Parse(parts[1], CultureInfo.InvariantCulture);
            int build = parts.Length > 2 ? int.Parse(parts[2], CultureInfo.InvariantCulture) : 0;
            result = new Version(major, minor, build);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static HttpClient CreateApiHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ZomboidManager-Updater");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static HttpClient CreateDownloadHttpClient()
    {
        // Important: do NOT send application/vnd.github+json here — that can break binary asset downloads.
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ZomboidManager-Updater");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return client;
    }

    private static string Truncate(string text, int max)
    {
        text = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }
}
