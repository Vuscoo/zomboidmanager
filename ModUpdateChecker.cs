using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZomboidManager;

/// <summary>
/// Detects Steam Workshop mod updates via GetPublishedFileDetails (no API key).
/// State is stored under LocalAppData\ZomboidManager\mod_update_state.json.
/// </summary>
public static class ModUpdateChecker
{
    private const string ApiUrl =
        "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";

    private const int BatchSize = 50;

    private static readonly string StateDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZomboidManager");

    private static readonly string StatePath = Path.Combine(StateDirectory, "mod_update_state.json");

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static readonly SemaphoreSlim CheckGate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string StateFilePath => StatePath;

    /// <summary>
    /// Fetches current Workshop timestamps, returns display names of mods newer than stored state,
    /// then updates the stored timestamps. On API failure returns an empty list (caller should omit section).
    /// </summary>
    public static async Task<IReadOnlyList<string>> CheckForUpdatedModsAsync(
        IEnumerable<string> workshopIds,
        Action<string>? log = null,
        Func<string, string?, string>? formatLabel = null)
    {
        List<string> ids = workshopIds
            .Select(id => (id ?? string.Empty).Trim())
            .Where(id => !string.IsNullOrWhiteSpace(id) && id.All(char.IsDigit))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (ids.Count == 0)
            return Array.Empty<string>();

        await CheckGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await CheckForUpdatedModsCoreAsync(ids, log, formatLabel).ConfigureAwait(false);
        }
        finally
        {
            CheckGate.Release();
        }
    }

    private static async Task<IReadOnlyList<string>> CheckForUpdatedModsCoreAsync(
        List<string> ids,
        Action<string>? log,
        Func<string, string?, string>? formatLabel)
    {
        Dictionary<string, long> previous = LoadState();
        List<WorkshopFileDetails> details;
        try
        {
            details = await FetchDetailsAsync(ids);
        }
        catch (Exception ex)
        {
            log?.Invoke("Mod update check skipped (Steam API): " + ex.Message);
            return Array.Empty<string>();
        }

        if (details.Count == 0)
        {
            log?.Invoke("Mod update check skipped: Steam returned no file details.");
            return Array.Empty<string>();
        }

        var updatedNames = new List<string>();
        var nextState = new Dictionary<string, long>(previous, StringComparer.Ordinal);

        foreach (WorkshopFileDetails detail in details)
        {
            if (string.IsNullOrWhiteSpace(detail.PublishedFileId))
                continue;

            string id = detail.PublishedFileId.Trim();
            if (previous.TryGetValue(id, out long oldUpdated) && detail.TimeUpdated > oldUpdated)
            {
                string steamTitle = string.IsNullOrWhiteSpace(detail.Title) ? id : detail.Title.Trim();
                string label = formatLabel?.Invoke(id, steamTitle) ?? steamTitle;
                if (string.IsNullOrWhiteSpace(label))
                    label = steamTitle;
                updatedNames.Add(label);
            }

            nextState[id] = detail.TimeUpdated;
        }

        // Drop IDs that are no longer configured so the file stays in sync with the server.ini list.
        foreach (string stale in nextState.Keys.Except(ids, StringComparer.Ordinal).ToList())
            nextState.Remove(stale);

        try
        {
            SaveState(nextState);
        }
        catch (Exception ex)
        {
            log?.Invoke("Mod update state save failed: " + ex.Message);
        }

        return updatedNames;
    }

    public static async Task RunSelfTestAsync()
    {
        string outPath = Path.Combine(StateDirectory, "mod_update_selftest.json");
        Directory.CreateDirectory(StateDirectory);

        // Known live Project Zomboid Workshop item used only for API/path verification.
        const string sampleId = "2875848298"; // Common Sense

        try
        {
            List<WorkshopFileDetails> details = await FetchDetailsAsync(new[] { sampleId });
            WorkshopFileDetails? sample = details.FirstOrDefault(d => d.PublishedFileId == sampleId);
            if (sample is null || sample.TimeUpdated <= 0)
            {
                File.WriteAllText(outPath, JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = "Steam API did not return a usable sample mod."
                }, JsonOptions));
                Console.WriteLine(File.ReadAllText(outPath));
                return;
            }

            // Simulate a prior older timestamp so the next check reports an update.
            SaveState(new Dictionary<string, long>(StringComparer.Ordinal)
            {
                [sampleId] = sample.TimeUpdated - 1
            });

            IReadOnlyList<string> first = await CheckForUpdatedModsAsync(
                new[] { sampleId },
                msg => Debug.WriteLine(msg));

            IReadOnlyList<string> second = await CheckForUpdatedModsAsync(
                new[] { sampleId },
                msg => Debug.WriteLine(msg));

            Dictionary<string, long> stored = LoadState();
            long storedTime = stored.TryGetValue(sampleId, out long saved) ? saved : 0;
            bool ok = first.Count == 1
                      && second.Count == 0
                      && storedTime == sample.TimeUpdated;

            File.WriteAllText(outPath, JsonSerializer.Serialize(new
            {
                ok,
                sampleId,
                title = sample.Title,
                timeUpdated = sample.TimeUpdated,
                firstCheck = first,
                secondCheck = second,
                storedTimeUpdated = storedTime
            }, JsonOptions));

            Console.WriteLine(File.ReadAllText(outPath));
        }
        catch (Exception ex)
        {
            File.WriteAllText(outPath, JsonSerializer.Serialize(new { ok = false, error = ex.Message }, JsonOptions));
            Console.WriteLine(File.ReadAllText(outPath));
        }
    }

    private static async Task<List<WorkshopFileDetails>> FetchDetailsAsync(IReadOnlyList<string> ids)
    {
        var results = new List<WorkshopFileDetails>();

        for (int offset = 0; offset < ids.Count; offset += BatchSize)
        {
            List<string> batch = ids.Skip(offset).Take(BatchSize).ToList();
            var form = new List<KeyValuePair<string, string>>
            {
                new("itemcount", batch.Count.ToString())
            };
            for (int i = 0; i < batch.Count; i++)
                form.Add(new($"publishedfileids[{i}]", batch[i]));

            using var content = new FormUrlEncodedContent(form);
            using HttpResponseMessage response = await Http.PostAsync(ApiUrl, content);
            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {body}");

            SteamApiRoot? root = JsonSerializer.Deserialize<SteamApiRoot>(body, JsonOptions);
            IEnumerable<WorkshopFileDetails> batchDetails =
                root?.Response?.PublishedFileDetails ?? Enumerable.Empty<WorkshopFileDetails>();

            foreach (WorkshopFileDetails detail in batchDetails)
            {
                // Steam result == 1 means OK for that file.
                if (detail.Result != 1)
                    continue;
                if (string.IsNullOrWhiteSpace(detail.PublishedFileId))
                    continue;
                results.Add(detail);
            }
        }

        return results;
    }

    private static Dictionary<string, long> LoadState()
    {
        try
        {
            if (!File.Exists(StatePath))
                return new Dictionary<string, long>(StringComparer.Ordinal);

            string json = File.ReadAllText(StatePath);
            Dictionary<string, long>? data = JsonSerializer.Deserialize<Dictionary<string, long>>(json, JsonOptions);
            return data is null
                ? new Dictionary<string, long>(StringComparer.Ordinal)
                : new Dictionary<string, long>(data, StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }
    }

    private static void SaveState(Dictionary<string, long> state)
    {
        Directory.CreateDirectory(StateDirectory);
        string json = JsonSerializer.Serialize(state, JsonOptions);
        File.WriteAllText(StatePath, json);
    }

    private sealed class SteamApiRoot
    {
        public SteamApiResponse? Response { get; set; }
    }

    private sealed class SteamApiResponse
    {
        [JsonPropertyName("publishedfiledetails")]
        public List<WorkshopFileDetails>? PublishedFileDetails { get; set; }
    }

    private sealed class WorkshopFileDetails
    {
        [JsonPropertyName("publishedfileid")]
        public string? PublishedFileId { get; set; }

        public int Result { get; set; }

        public string? Title { get; set; }

        [JsonPropertyName("time_updated")]
        public long TimeUpdated { get; set; }
    }
}
