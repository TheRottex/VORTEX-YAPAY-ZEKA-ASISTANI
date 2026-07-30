using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Vortex.Shared;

namespace Vortex.Desktop.Services;

public sealed record LocalAgentHealthResult(bool Ok, string Reason, string? Detail = null, HttpStatusCode? StatusCode = null);

public sealed record LocalAgentInvokeResult(bool Ok, string Reason, string? Message = null, string? Output = null, HttpStatusCode? StatusCode = null);

/// <summary>
/// Direct Desktop → LocalAgent HTTP client (no public Server, no Hermes).
/// Uses per-call base URL/secret from desktop settings.
/// </summary>
public sealed class LocalAgentClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public LocalAgentClient()
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, ownsHttpClient: true)
    {
    }

    public LocalAgentClient(HttpClient httpClient)
        : this(httpClient, ownsHttpClient: false)
    {
    }

    private LocalAgentClient(HttpClient httpClient, bool ownsHttpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
    }

    public static string NormalizeBaseUrl(string? baseUrl)
    {
        var url = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        return string.IsNullOrWhiteSpace(url) ? string.Empty : url;
    }

    public async Task<LocalAgentHealthResult> HealthAsync(string? baseUrl, string? secret, CancellationToken cancellationToken)
    {
        var normalized = NormalizeBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new LocalAgentHealthResult(false, "not_configured", "LocalAgent URL boş.");
        }
        if (string.IsNullOrWhiteSpace(secret))
        {
            return new LocalAgentHealthResult(false, "not_configured", "LocalAgent secret boş.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Combine(normalized, "/health"));
            ApplyBearerIfPresent(request, secret);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new LocalAgentHealthResult(false, "not_authenticated", "LocalAgent secret doğrulanamadı.", response.StatusCode);
            }

            if (response.IsSuccessStatusCode)
            {
                string? detail = null;
                try
                {
                    var hello = await response.Content.ReadFromJsonAsync<LocalAgentHello>(JsonOptions, cancellationToken).ConfigureAwait(false);
                    if (hello is not null)
                    {
                        detail = $"{hello.AgentName} {hello.Version} ({hello.Platform})";
                    }
                }
                catch
                {
                    // Body parse is best-effort; 200 alone is enough for health.
                }

                return new LocalAgentHealthResult(true, "ok", detail, response.StatusCode);
            }

            return new LocalAgentHealthResult(false, "http_error", $"HTTP {(int)response.StatusCode}", response.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return new LocalAgentHealthResult(false, "transport_error", ex.Message);
        }
    }

    public async Task<LocalAgentInvokeResult> InvokeToolAsync(
        string? baseUrl,
        string? secret,
        string toolName,
        Dictionary<string, string>? arguments,
        bool userConfirmed,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new LocalAgentInvokeResult(false, "not_configured", "LocalAgent URL boş.");
        }

        if (string.IsNullOrWhiteSpace(secret))
        {
            return new LocalAgentInvokeResult(false, "not_configured", "LocalAgent secret boş.");
        }

        if (string.IsNullOrWhiteSpace(toolName))
        {
            return new LocalAgentInvokeResult(false, "invalid_request", "ToolName boş.");
        }

        var requestArguments = arguments is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(arguments, StringComparer.OrdinalIgnoreCase);
        if (dryRun) requestArguments["dryRun"] = "true";

        var body = new LocalToolRequest(
            Guid.NewGuid().ToString("N"),
            toolName.Trim(),
            requestArguments,
            DateTimeOffset.UtcNow.AddMinutes(2),
            string.Empty,
            userConfirmed);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Combine(normalized, "/api/tools/invoke"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret.Trim());
            request.Content = JsonContent.Create(body, options: JsonOptions);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new LocalAgentInvokeResult(false, "not_authenticated", "LocalAgent yetkilendirme başarısız.", StatusCode: response.StatusCode);
            }

            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                var bad = await TryReadToolResponseAsync(response, cancellationToken).ConfigureAwait(false);
                return new LocalAgentInvokeResult(
                    false,
                    "invalid_request",
                    bad?.Message ?? "LocalAgent isteği reddetti.",
                    bad?.Output,
                    response.StatusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new LocalAgentInvokeResult(false, "http_error", $"HTTP {(int)response.StatusCode}", StatusCode: response.StatusCode);
            }

            var parsed = await TryReadToolResponseAsync(response, cancellationToken).ConfigureAwait(false);
            if (parsed is null)
            {
                return new LocalAgentInvokeResult(false, "http_error", "LocalAgent yanıtı okunamadı.", StatusCode: response.StatusCode);
            }

            if (!parsed.Succeeded)
            {
                return new LocalAgentInvokeResult(false, "invalid_request", parsed.Message, parsed.Output, response.StatusCode);
            }

            return new LocalAgentInvokeResult(true, "ok", parsed.Message, parsed.Output, response.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return new LocalAgentInvokeResult(false, "transport_error", ex.Message);
        }
    }

    private static void ApplyBearerIfPresent(HttpRequestMessage request, string? secret)
    {
        if (!string.IsNullOrWhiteSpace(secret))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret.Trim());
        }
    }

    private static string Combine(string baseUrl, string path)
    {
        if (string.IsNullOrEmpty(path)) return baseUrl;
        return path.StartsWith('/') ? baseUrl + path : baseUrl + "/" + path;
    }

    private static async Task<LocalToolResponse?> TryReadToolResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<LocalToolResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }
}
