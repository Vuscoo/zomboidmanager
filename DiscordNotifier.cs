using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ZomboidManager;

public static class DiscordNotifier
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<(bool success, string message)> SendAsync(string? webhookUrl, string content)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
            return (false, "Discord webhook URL is not configured.");

        if (string.IsNullOrWhiteSpace(content))
            return (false, "Message is empty.");

        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !uri.Host.Contains("discord", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Webhook URL looks invalid (expected a Discord webhook HTTPS URL).");
        }

        try
        {
            // Discord limit ~2000 chars
            string text = content.Length > 1900 ? content[..1900] + "…" : content;
            string json = JsonSerializer.Serialize(new { content = text }, JsonOptions);
            using var body = new StringContent(json, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await Http.PostAsync(uri, body);
            if (!response.IsSuccessStatusCode)
            {
                string err = await response.Content.ReadAsStringAsync();
                return (false, $"Discord error {(int)response.StatusCode}: {err}");
            }

            return (true, "Message sent.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
