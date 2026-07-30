using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Vortex.Shared;

namespace Vortex.Desktop.Services;

public sealed record ExchangeResult(HttpStatusCode StatusCode, AuthResponse? AuthResponse);
public sealed record LoginResult(AuthResponse? Auth, HttpStatusCode? StatusCode, string Reason, string? CorrelationId);
public sealed record UserProfileFetchResult(bool Ok, UserProfileDto? User, HttpStatusCode? StatusCode, string Reason);
public sealed record ListChatsResult(bool Ok, IReadOnlyList<ChatSessionDto>? Chats, HttpStatusCode? StatusCode, string Reason);
public sealed record CreateChatResult(bool Ok, ChatSessionDto? Chat, HttpStatusCode? StatusCode, string Reason);
public sealed record AgentStatusFetchResult(bool Ok, AgentStatusDto? Status, HttpStatusCode? StatusCode, string Reason);
public sealed record VoiceTranscribeResult(bool Ok, string? Text, HttpStatusCode? StatusCode, string Reason);
public sealed record AgentJobFetchResult(bool Ok, AgentJobStatusDto? Job, HttpStatusCode? StatusCode, string Reason);
public sealed record LocalAgentPlanResult(bool Ok, LocalAgentToolPlan? Plan, HttpStatusCode? StatusCode, string Reason);
public sealed record LocalAgentQueueResult(bool Ok, LocalAgentQueuedResponse? Queued, LocalAgentToolPlan? Plan, HttpStatusCode? StatusCode, string Reason);
public sealed record LocalAgentDevicesFetchResult(bool Ok, IReadOnlyList<LocalAgentDeviceDto>? Devices, HttpStatusCode? StatusCode, string Reason);
public sealed record LocalAgentJobStatusDto(string JobId, string Status, string ToolName, string? Code, string? Message, bool? Success, bool DryRun, string[] Timeline);
public sealed record LocalAgentJobFetchResult(bool Ok, LocalAgentJobStatusDto? Job, HttpStatusCode? StatusCode, string Reason);
public sealed record DirectChatCreditStatusResult(DirectChatCreditStatusDto? Status, string Reason);
public sealed record LocalAgentQueuedResponse(string JobId, string Status, string Action, bool DryRun);
public sealed record ScheduledTasksFetchResult(bool Ok, IReadOnlyList<AgentTaskDto>? Tasks, HttpStatusCode? StatusCode, string Reason);
public sealed record ScheduledTaskCreateResult(bool Ok, AgentTaskDto? Task, HttpStatusCode? StatusCode, string Reason);
public sealed record AgentChatSubmitResult(AgentChatResponse? ImmediateResponse, AgentJobStatusDto? QueuedJob)
{
    public bool IsImmediate => ImmediateResponse is not null;
    public bool IsQueued => QueuedJob is not null;
}

public sealed class AgentChatRequestException(HttpStatusCode statusCode, string? body) : Exception(BuildMessage(statusCode, body))
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    private static string BuildMessage(HttpStatusCode statusCode, string? body)
    {
        if (statusCode == HttpStatusCode.TooManyRequests) return MapQuotaFailure(body);
        if (statusCode == HttpStatusCode.Unauthorized) return "Oturum süresi dolmuş, yeniden giriş yapın.";
        if (statusCode == HttpStatusCode.ServiceUnavailable) return "Sunucu şu anda kullanılamıyor.";
        if (statusCode == HttpStatusCode.BadGateway) return MapHermesFailure(body);
        return "Agent isteği sunucu tarafından reddedildi.";
    }

    private static string MapQuotaFailure(string? body)
    {
        var errorCode = TryExtractServerErrorCode(body);
        return errorCode switch
        {
            "quota_exceeded_daily" => "Günlük istek limitiniz doldu.",
            "quota_exceeded_storage" => "Konuşma geçmişi depolama kotanız doldu.",
            _ when TryExtractServerMessage(body) == "Konuşma geçmişi depolama kotanız doldu." => "Konuşma geçmişi depolama kotanız doldu.",
            _ => "İstek kotanız doldu."
        };
    }

    private static string? TryExtractServerErrorCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var errorProp) && errorProp.ValueKind == JsonValueKind.String
                ? errorProp.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string MapHermesFailure(string? body)
    {
        const string prefix = "Hermes işi tamamlanamadı: ";
        var detail = TryExtractServerMessage(body);
        if (detail is null || !detail.StartsWith(prefix, StringComparison.Ordinal)) return "Hermes işi tamamlanamadı.";

        return detail[prefix.Length..] switch
        {
            "TimedOut" => "Hermes işi zaman aşımına uğradı.",
            "StorageQuotaExceeded" => "Hermes çalışma alanı depolama sınırına ulaştı.",
            "StorageUnavailable" => "Hermes çalışma alanı şu anda kullanılamıyor.",
            "StdoutLimitExceeded" or "StderrLimitExceeded" => "Hermes işi çıktı sınırını aştı.",
            "HermesProcessFailed" or "DockerHermesProcessFailed" => "Hermes işlemi başarısız oldu.",
            "WorkerNotReady" or "HermesUnavailable" or "DockerUnavailable" => "Hermes çalışanı şu anda hazır değil.",
            "HermesToolProposalUnsupported" => "Hermes tarafından önerilen eylem desteklenmiyor.",
            _ => "Hermes işi tamamlanamadı."
        };
    }

    private static string? TryExtractServerMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var messageProp) && messageProp.ValueKind == JsonValueKind.String)
            {
                return messageProp.GetString();
            }
            if (doc.RootElement.TryGetProperty("detail", out var detailProp) && detailProp.ValueKind == JsonValueKind.String)
            {
                return detailProp.GetString();
            }
        }
        catch (JsonException)
        {
            // Not JSON (e.g. Results.Problem plain text) — fall through.
        }
        return null;
    }

}

public sealed class BackendClient(HttpClient httpClient, TokenStorageService tokenStorage, DesktopSettingsService settingsService)
{
    private string? _token;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task SetTokenAsync(string token, CancellationToken cancellationToken)
    {
        _token = token;
        try
        {
            var settings = await settingsService.LoadAsync(cancellationToken);
            if (settings.RememberMe)
            {
                await tokenStorage.SaveAsync(token, cancellationToken);
            }
            else
            {
                tokenStorage.Clear();
            }
        }
        catch (Exception ex)
        {
            DesktopLogService.Error("Desktop session token could not be persisted.", ex);
        }
    }

    public Task<DesktopSettings> GetSettingsAsync(CancellationToken cancellationToken) => settingsService.LoadAsync(cancellationToken);

    public async Task SetRememberMeAsync(bool remember, CancellationToken cancellationToken)
    {
        var settings = await settingsService.LoadAsync(cancellationToken);
        await settingsService.SaveAsync(settings with { RememberMe = remember }, cancellationToken);
        if (remember && !string.IsNullOrWhiteSpace(_token))
        {
            await tokenStorage.SaveAsync(_token, cancellationToken);
        }
        else if (!remember)
        {
            tokenStorage.Clear();
        }
    }

    public async Task<bool> TryLoadStoredTokenAsync(CancellationToken cancellationToken)
    {
        _token = await tokenStorage.LoadAsync(cancellationToken);
        return !string.IsNullOrWhiteSpace(_token);
    }

    public void Logout()
    {
        _token = null;
        tokenStorage.Clear();
    }

    public async Task<AuthResponse?> LoginAsync(string email, string password, CancellationToken cancellationToken)
        => (await LoginDetailedAsync(email, password, cancellationToken)).Auth;

    public async Task<LoginResult> LoginDetailedAsync(string email, string password, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password), JsonOptions, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(null, response.StatusCode, "invalid_credentials", null);
            if ((int)response.StatusCode >= 500)
            {
                var correlationId = await TryReadCorrelationIdAsync(response, cancellationToken);
                return new(null, response.StatusCode, "server_error", correlationId);
            }
            if (!response.IsSuccessStatusCode) return new(null, response.StatusCode, "invalid_response", null);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var auth = await JsonSerializer.DeserializeAsync<AuthResponse>(stream, JsonOptions, cancellationToken);
            if (auth is null) return new(null, response.StatusCode, "invalid_response", null);
            await SetTokenAsync(auth.AccessToken, cancellationToken);
            return new(auth, response.StatusCode, "ok", null);
        }
        catch (OperationCanceledException) { return new(null, null, "cancelled", null); }
        catch (HttpRequestException) { return new(null, null, "transport_error", null); }
        catch (JsonException) { return new(null, HttpStatusCode.OK, "invalid_response", null); }
    }

    private static async Task<string?> TryReadCorrelationIdAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("correlationId", out var value) || value.ValueKind != JsonValueKind.String) return null;
            var correlationId = value.GetString();
            return Guid.TryParseExact(correlationId, "D", out _) ? correlationId : null;
        }
        catch (JsonException) { return null; }
    }

    public async Task<AuthResponse?> RegisterAsync(string email, string password, string displayName, string? firstName, string? lastName, string? birthDate, string? phoneNumber, CancellationToken cancellationToken)
    {
        var auth = await PostRequiredAsync<RegisterRequest, AuthResponse>("/api/auth/register", new RegisterRequest(email, password, displayName, firstName, lastName, birthDate, phoneNumber), cancellationToken);
        if (auth is not null) await SetTokenAsync(auth.AccessToken, cancellationToken);
        return auth;
    }

    public async Task<UserProfileDto?> GetMeAsync(CancellationToken cancellationToken)
        => (await GetMeDetailedAsync(cancellationToken)).User;

    public async Task<UserProfileFetchResult> GetMeDetailedAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_token)) return new(false, null, null, "not_authenticated");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(false, null, response.StatusCode, "not_authenticated");
            if (response.StatusCode == HttpStatusCode.Forbidden) return new(false, null, response.StatusCode, "forbidden");
            if (response.StatusCode == HttpStatusCode.NotFound) return new(false, null, response.StatusCode, "not_found");
            if (!response.IsSuccessStatusCode) return new(false, null, response.StatusCode, "http_error");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var user = await JsonSerializer.DeserializeAsync<UserProfileDto>(stream, JsonOptions, cancellationToken);
            return user is null
                ? new(false, null, response.StatusCode, "invalid_response")
                : new(true, user, response.StatusCode, "ok");
        }
        catch (OperationCanceledException) { return new(false, null, null, "cancelled"); }
        catch (HttpRequestException) { return new(false, null, null, "transport_error"); }
        catch (JsonException) { return new(false, null, HttpStatusCode.OK, "invalid_response"); }
    }


    public async Task<AgentStatusDto?> GetAgentStatusAsync(CancellationToken cancellationToken)
        => (await GetAgentStatusDetailedAsync(cancellationToken)).Status;

    public async Task<AgentStatusFetchResult> GetAgentStatusDetailedAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_token)) return new(false, null, null, "not_authenticated");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/agent/status");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(false, null, response.StatusCode, "not_authenticated");
            if (response.StatusCode == HttpStatusCode.Forbidden) return new(false, null, response.StatusCode, "forbidden");
            if (!response.IsSuccessStatusCode) return new(false, null, response.StatusCode, "http_error");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var status = await JsonSerializer.DeserializeAsync<AgentStatusDto>(stream, JsonOptions, cancellationToken);
            return status is null
                ? new(false, null, response.StatusCode, "invalid_response")
                : new(true, status, response.StatusCode, "ok");
        }
        catch (OperationCanceledException) { return new(false, null, null, "cancelled"); }
        catch (HttpRequestException) { return new(false, null, null, "transport_error"); }
        catch (JsonException) { return new(false, null, HttpStatusCode.OK, "invalid_response"); }
    }

    private static string GetAgentStatusFailureMessage(string reason) => reason switch
    {
        "not_authenticated" => "Hermes durumu için oturum gerekli.",
        "forbidden" => "Hermes durumuna erişim izni yok.",
        "transport_error" => "Hermes durum sunucusuna bağlanılamadı.",
        "cancelled" => "Hermes durum alımı iptal edildi.",
        "invalid_response" => "Hermes durum cevabı geçersiz.",
        _ => "Hermes durumu alınamadı."
    };

    public static string GetAgentStatusFailureMessageForUi(string reason) => GetAgentStatusFailureMessage(reason);



    public async Task<IReadOnlyList<ChatSessionDto>?> ListChatsAsync(string? query, bool archived, CancellationToken cancellationToken)
        => (await ListChatsDetailedAsync(query, archived, cancellationToken)).Chats;

    public async Task<ListChatsResult> ListChatsDetailedAsync(string? query, bool archived, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_token)) return new(false, null, null, "not_authenticated");
        var path = "/api/chats?archived=" + (archived ? "true" : "false");
        if (!string.IsNullOrWhiteSpace(query)) path += $"&q={Uri.EscapeDataString(query)}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(false, null, response.StatusCode, "not_authenticated");
            if (response.StatusCode == HttpStatusCode.Forbidden) return new(false, null, response.StatusCode, "forbidden");
            if (!response.IsSuccessStatusCode) return new(false, null, response.StatusCode, "http_error");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var chats = await JsonSerializer.DeserializeAsync<List<ChatSessionDto>>(stream, JsonOptions, cancellationToken);
            return chats is null
                ? new(false, null, response.StatusCode, "invalid_response")
                : new(true, chats, response.StatusCode, "ok");
        }
        catch (OperationCanceledException) { return new(false, null, null, "cancelled"); }
        catch (HttpRequestException) { return new(false, null, null, "transport_error"); }
        catch (JsonException) { return new(false, null, HttpStatusCode.OK, "invalid_response"); }
    }

    private static string GetChatListFailureMessage(string reason) => reason switch
    {
        "not_authenticated" => "Oturum geçersiz. Lütfen tekrar giriş yapın.",
        "forbidden" => "Sohbetlerinize erişim izniniz yok.",
        "transport_error" => "Sunucuya bağlanılamadı.",
        "cancelled" => "Sohbet listesi yükleme iptal edildi.",
        "invalid_response" => "Sunucu geçersiz bir sohbet listesi döndürdü.",
        _ => "Sohbet listesi yüklenemedi."
    };

    public static string GetChatListFailureMessageForUi(string reason) => GetChatListFailureMessage(reason);

    public async Task<IReadOnlyList<LocalAgentDeviceDto>?> ListLocalAgentDevicesAsync(CancellationToken cancellationToken)
        => (await ListLocalAgentDevicesDetailedAsync(cancellationToken)).Devices;

    public async Task<LocalAgentDevicesFetchResult> ListLocalAgentDevicesDetailedAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_token)) return new(false, null, null, "not_authenticated");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/devices");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(false, null, response.StatusCode, "not_authenticated");
            if (response.StatusCode == HttpStatusCode.Forbidden) return new(false, null, response.StatusCode, "forbidden");
            if (!response.IsSuccessStatusCode) return new(false, null, response.StatusCode, "http_error");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var devices = await JsonSerializer.DeserializeAsync<List<LocalAgentDeviceDto>>(stream, JsonOptions, cancellationToken);
            return devices is null
                ? new(false, null, response.StatusCode, "invalid_response")
                : new(true, devices, response.StatusCode, "ok");
        }
        catch (OperationCanceledException) { return new(false, null, null, "cancelled"); }
        catch (HttpRequestException) { return new(false, null, null, "transport_error"); }
        catch (JsonException) { return new(false, null, HttpStatusCode.OK, "invalid_response"); }
    }

    public async Task<CreateChatResult> CreateChatAsync(string? title, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_token)) return new(false, null, null, "not_authenticated");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chats");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Content = new StringContent(JsonSerializer.Serialize(new CreateChatRequest(title), JsonOptions), Encoding.UTF8, "application/json");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(false, null, response.StatusCode, "not_authenticated");
            if (response.StatusCode == HttpStatusCode.Forbidden) return new(false, null, response.StatusCode, "forbidden");
            if (!response.IsSuccessStatusCode) return new(false, null, response.StatusCode, "http_error");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var chat = await JsonSerializer.DeserializeAsync<ChatSessionDto>(stream, JsonOptions, cancellationToken);
            return chat is null
                ? new(false, null, response.StatusCode, "invalid_response")
                : new(true, chat, response.StatusCode, "ok");
        }
        catch (OperationCanceledException) { return new(false, null, null, "cancelled"); }
        catch (HttpRequestException) { return new(false, null, null, "transport_error"); }
        catch (JsonException) { return new(false, null, HttpStatusCode.OK, "invalid_response"); }
    }

    private static string GetCreateChatFailureMessage(string reason) => reason switch
    {
        "not_authenticated" => "Oturum geçersiz. Lütfen tekrar giriş yapın.",
        "forbidden" => "Yeni sohbet oluşturma izniniz yok.",
        "transport_error" => "Sunucuya bağlanılamadı.",
        "cancelled" => "Yeni sohbet oluşturma iptal edildi.",
        "invalid_response" => "Sunucu geçersiz bir sohbet cevabı döndürdü.",
        _ => "Yeni sohbet oluşturulamadı."
    };

    public static string GetCreateChatFailureMessageForUi(string reason) => GetCreateChatFailureMessage(reason);

    public async Task<ScheduledTasksFetchResult> ListScheduledTasksAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_token)) return new(false, null, null, "not_authenticated");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/agent/tasks");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(false, null, response.StatusCode, "not_authenticated");
            if (response.StatusCode == HttpStatusCode.Forbidden) return new(false, null, response.StatusCode, "forbidden");
            if (!response.IsSuccessStatusCode) return new(false, null, response.StatusCode, "http_error");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var tasks = await JsonSerializer.DeserializeAsync<List<AgentTaskDto>>(stream, JsonOptions, cancellationToken);
            return tasks is null
                ? new(false, null, response.StatusCode, "invalid_response")
                : new(true, tasks, response.StatusCode, "ok");
        }
        catch (OperationCanceledException) { return new(false, null, null, "cancelled"); }
        catch (HttpRequestException) { return new(false, null, null, "transport_error"); }
        catch (JsonException) { return new(false, null, HttpStatusCode.OK, "invalid_response"); }
    }

    public async Task<ScheduledTaskCreateResult> CreateScheduledTaskAsync(CreateAgentTaskRequest taskRequest, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_token)) return new(false, null, null, "not_authenticated");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/agent/tasks");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Content = new StringContent(JsonSerializer.Serialize(taskRequest, JsonOptions), Encoding.UTF8, "application/json");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(false, null, response.StatusCode, "not_authenticated");
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return new(false, null, response.StatusCode, "scheduled_task_limit_exceeded");
            if (response.StatusCode == HttpStatusCode.BadRequest) return new(false, null, response.StatusCode, "invalid_request");
            if (!response.IsSuccessStatusCode) return new(false, null, response.StatusCode, "http_error");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var task = await JsonSerializer.DeserializeAsync<AgentTaskDto>(stream, JsonOptions, cancellationToken);
            return task is null
                ? new(false, null, response.StatusCode, "invalid_response")
                : new(true, task, response.StatusCode, "ok");
        }
        catch (OperationCanceledException) { return new(false, null, null, "cancelled"); }
        catch (HttpRequestException) { return new(false, null, null, "transport_error"); }
        catch (JsonException) { return new(false, null, HttpStatusCode.OK, "invalid_response"); }
    }

    public async Task<string> DeleteScheduledTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_token)) return "not_authenticated";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/agent/tasks/{taskId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return "not_authenticated";
            if (response.StatusCode == HttpStatusCode.NotFound) return "not_found";
            if (response.StatusCode == HttpStatusCode.NoContent || response.IsSuccessStatusCode) return "ok";
            return "http_error";
        }
        catch (OperationCanceledException) { return "cancelled"; }
        catch (HttpRequestException) { return "transport_error"; }
    }

    public async Task<ChatActiveStateDto?> GetActiveChatStateAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_token)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/chats/active");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<ChatActiveStateDto>(stream, JsonOptions, cancellationToken);
    }

    public async Task<bool> SetActiveChatStateAsync(Guid? chatId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_token)) return false;
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/chats/active");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Content = new StringContent(JsonSerializer.Serialize(new UpdateChatActiveStateRequest(chatId), JsonOptions), Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> RenameChatAsync(Guid chatId, string title, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_token)) return false;
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/chats/{chatId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Content = new StringContent(JsonSerializer.Serialize(new RenameChatRequest(title), JsonOptions), Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> ArchiveChatAsync(Guid chatId, bool archive, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_token)) return false;
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/chats/{chatId}/archive");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Content = new StringContent(JsonSerializer.Serialize(new ArchiveChatRequest(archive), JsonOptions), Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> FavoriteChatAsync(Guid chatId, bool favorite, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_token)) return false;
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/chats/{chatId}/favorite");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Content = new StringContent(JsonSerializer.Serialize(new FavoriteChatRequest(favorite), JsonOptions), Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> DeleteChatAsync(Guid chatId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_token)) return false;
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/chats/{chatId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<IReadOnlyList<ChatMessageDto>?> GetChatMessagesAsync(Guid chatId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_token)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/chats/{chatId}/messages");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<List<ChatMessageDto>>(stream, JsonOptions, cancellationToken);
    }

    public async Task<AgentChatSubmitResult?> SendAgentChatAsync(string message, Guid? conversationId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_token)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/agent/chat");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Content = new StringContent(JsonSerializer.Serialize(new AgentChatRequest(message, ConversationId: conversationId), JsonOptions), Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new AgentChatRequestException(response.StatusCode, body);
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var immediate = await JsonSerializer.DeserializeAsync<AgentChatResponse>(stream, JsonOptions, cancellationToken);
                return immediate is null ? null : new AgentChatSubmitResult(immediate, null);
            }
            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                var queued = await JsonSerializer.DeserializeAsync<AgentJobStatusDto>(stream, JsonOptions, cancellationToken);
                return queued is null ? null : new AgentChatSubmitResult(null, queued);
            }
        }
        catch (JsonException)
        {
            // A successful status with an invalid body is not a usable submission.
        }

        return null;
    }

    public async Task<ServerTextToSpeechResponse> TextToSpeechAsync(ServerTextToSpeechRequest requestPayload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_token))
        {
            return new ServerTextToSpeechResponse(false, "not_authenticated");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/agent/text-to-speech");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Content = new StringContent(JsonSerializer.Serialize(requestPayload, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            try
            {
                await using var errorStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var error = await JsonSerializer.DeserializeAsync<ServerTextToSpeechResponse>(errorStream, JsonOptions, cancellationToken);
                if (error is { Succeeded: false } && !string.IsNullOrWhiteSpace(error.Message)) return error;
            }
            catch (JsonException)
            {
                // Status mapping below preserves the existing fail-closed contract for non-JSON errors.
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return new ServerTextToSpeechResponse(false, "quota_exceeded_daily");
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                return new ServerTextToSpeechResponse(false, "provider_missing");
            if (response.StatusCode == HttpStatusCode.BadGateway)
                return new ServerTextToSpeechResponse(false, "provider_failed");
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new ServerTextToSpeechResponse(false, "not_authenticated");
            return new ServerTextToSpeechResponse(false, "http_error");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<ServerTextToSpeechResponse>(stream, JsonOptions, cancellationToken) ?? new ServerTextToSpeechResponse(false, "decode_failed");
    }

    public async IAsyncEnumerable<AgentJobFetchResult> StreamAgentJobEventsAsync(Guid jobId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_token))
        {
            yield return new AgentJobFetchResult(false, null, null, "not_authenticated");
            yield break;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/agent/jobs/{jobId}/events");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        HttpResponseMessage? response = null;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch
        {
        }

        if (response is null)
        {
            yield return new AgentJobFetchResult(false, null, null, "transport_error");
            yield break;
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                yield return new AgentJobFetchResult(false, null, response.StatusCode, "not_authenticated");
                yield break;
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                yield return new AgentJobFetchResult(false, null, response.StatusCode, "not_found");
                yield break;
            }
            if (!response.IsSuccessStatusCode)
            {
                yield return new AgentJobFetchResult(false, null, response.StatusCode, "http_error");
                yield break;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null) yield break;
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

                AgentJobStatusDto? job = null;
                try
                {
                    job = JsonSerializer.Deserialize<AgentJobStatusDto>(line[6..], JsonOptions);
                }
                catch (JsonException)
                {
                }

                if (job is null)
                {
                    yield return new AgentJobFetchResult(false, null, response.StatusCode, "http_error");
                    yield break;
                }
                yield return new AgentJobFetchResult(true, job, response.StatusCode, "ok");
            }
        }
    }

    public async Task<AgentJobFetchResult> GetAgentJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_token))
            return new AgentJobFetchResult(false, null, null, "not_authenticated");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/agent/jobs/{jobId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch
        {
            return new AgentJobFetchResult(false, null, null, "transport_error");
        }

        try
        {
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var job = await JsonSerializer.DeserializeAsync<AgentJobStatusDto>(stream, JsonOptions, cancellationToken);
                if (job is null)
                    return new AgentJobFetchResult(false, null, response.StatusCode, "http_error");
                return new AgentJobFetchResult(true, job, response.StatusCode, "ok");
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new AgentJobFetchResult(false, null, HttpStatusCode.Unauthorized, "not_authenticated");
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new AgentJobFetchResult(false, null, HttpStatusCode.NotFound, "not_found");
            return new AgentJobFetchResult(false, null, response.StatusCode, "http_error");
        }
        finally
        {
            response.Dispose();
        }
    }

    public async Task<LocalAgentJobFetchResult> GetLocalAgentJobStatusAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_token)) return new(false, null, null, "not_authenticated");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/pardus/actions/{jobId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(false, null, response.StatusCode, "not_authenticated");
            if (response.StatusCode == HttpStatusCode.NotFound) return new(false, null, response.StatusCode, "not_found");
            if (!response.IsSuccessStatusCode) return new(false, null, response.StatusCode, "http_error");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var job = await JsonSerializer.DeserializeAsync<LocalAgentJobStatusDto>(stream, JsonOptions, cancellationToken);
            return job is null
                ? new(false, null, response.StatusCode, "invalid_response")
                : new(true, job, response.StatusCode, "ok");
        }
        catch (OperationCanceledException) { return new(false, null, null, "cancelled"); }
        catch (HttpRequestException) { return new(false, null, null, "transport_error"); }
        catch (JsonException) { return new(false, null, HttpStatusCode.OK, "invalid_response"); }
    }

    public async Task<LocalAgentPlanResult> PlanLocalAgentActionAsync(
        string toolName,
        Dictionary<string, string>? arguments = null,
        string? fallbackCommand = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_token))
            return new LocalAgentPlanResult(false, null, null, "not_authenticated");

        var payload = new QueueLocalAgentToolRequest(Guid.Empty, toolName, arguments, UserConfirmed: false, DryRun: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local-agent/actions/plan");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch
        {
            return new LocalAgentPlanResult(false, null, null, "transport_error");
        }

        try
        {
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    return new LocalAgentPlanResult(false, null, HttpStatusCode.Unauthorized, "not_authenticated");
                return new LocalAgentPlanResult(false, null, response.StatusCode, "http_error");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var plan = await JsonSerializer.DeserializeAsync<LocalAgentToolPlan>(stream, JsonOptions, cancellationToken);
            if (plan is null)
                return new LocalAgentPlanResult(false, null, response.StatusCode, "http_error");

            if (!plan.UsesPreparedTool && string.IsNullOrWhiteSpace(plan.FallbackCommand) && !string.IsNullOrWhiteSpace(fallbackCommand))
            {
                plan = plan with { FallbackCommand = fallbackCommand };
            }

            return new LocalAgentPlanResult(true, plan, response.StatusCode, "ok");
        }
        finally
        {
            response.Dispose();
        }
    }

    public async Task<LocalAgentQueueResult> QueueLocalAgentActionAsync(
        Guid deviceId,
        string toolName,
        Dictionary<string, string>? arguments = null,
        bool userConfirmed = false,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_token))
            return new LocalAgentQueueResult(false, null, null, null, "not_authenticated");

        var payload = new QueueLocalAgentToolRequest(deviceId, toolName, arguments, userConfirmed, dryRun);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local-agent/actions/queue");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch
        {
            return new LocalAgentQueueResult(false, null, null, null, "transport_error");
        }

        try
        {
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var queued = await JsonSerializer.DeserializeAsync<LocalAgentQueuedResponse>(stream, JsonOptions, cancellationToken);
                if (queued is null)
                    return new LocalAgentQueueResult(false, null, null, response.StatusCode, "http_error");
                return new LocalAgentQueueResult(true, queued, null, response.StatusCode, "ok");
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new LocalAgentQueueResult(false, null, null, HttpStatusCode.Unauthorized, "not_authenticated");

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                LocalAgentToolPlan? plan = null;
                try
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                    if (doc.RootElement.TryGetProperty("plan", out var planElement))
                    {
                        plan = planElement.Deserialize<LocalAgentToolPlan>(JsonOptions);
                    }
                }
                catch
                {
                    // Best-effort plan parse; still surface user_approval_required.
                }

                return new LocalAgentQueueResult(false, null, plan, HttpStatusCode.Conflict, "user_approval_required");
            }

            if (response.StatusCode == HttpStatusCode.BadRequest)
                return new LocalAgentQueueResult(false, null, null, HttpStatusCode.BadRequest, "invalid_request");

            return new LocalAgentQueueResult(false, null, null, response.StatusCode, "http_error");
        }
        finally
        {
            response.Dispose();
        }
    }

    public async Task<VoiceTranscribeResult> TranscribeAsync(byte[] wavBytes, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_token))
        {
            return new VoiceTranscribeResult(false, null, null, "not_authenticated");
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/agent/transcribe");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        var content = new ByteArrayContent(wavBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        request.Content = content;

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch
        {
            return new VoiceTranscribeResult(false, null, null, "transport_error");
        }

        try
        {
            if (response.StatusCode == HttpStatusCode.OK)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var result = await JsonSerializer.DeserializeAsync<TranscriptionResponse>(stream, JsonOptions, cancellationToken);
                if (result is null || string.IsNullOrWhiteSpace(result.Text))
                {
                    return new VoiceTranscribeResult(false, null, HttpStatusCode.OK, "http_error");
                }
                return new VoiceTranscribeResult(true, result.Text, HttpStatusCode.OK, "ok");
            }

            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                return new VoiceTranscribeResult(false, null, HttpStatusCode.ServiceUnavailable, "provider_missing");
            }
            if (response.StatusCode == HttpStatusCode.BadGateway)
            {
                return new VoiceTranscribeResult(false, null, HttpStatusCode.BadGateway, "provider_failed");
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new VoiceTranscribeResult(false, null, HttpStatusCode.Unauthorized, "not_authenticated");
            }
            if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
            {
                return new VoiceTranscribeResult(false, null, HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return new VoiceTranscribeResult(false, null, HttpStatusCode.TooManyRequests, "quota_exceeded_daily");
            }
            return new VoiceTranscribeResult(false, null, response.StatusCode, "http_error");
        }
        finally
        {
            response.Dispose();
        }
    }

    public async Task<StartDesktopAuthResponse?> StartDesktopAuthAsync(StartDesktopAuthRequest payload, CancellationToken cancellationToken)
        => await PostAsync<StartDesktopAuthRequest, StartDesktopAuthResponse>("/api/desktop-auth/sessions", payload, cancellationToken);

    public async Task<ExchangeResult> ExchangeDesktopCodeDetailedAsync(
    ExchangeDesktopCodeRequest payload,
    CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(
            "/api/desktop-auth/token",
            new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions),
                Encoding.UTF8,
                "application/json"),
            cancellationToken);

        if (!response.IsSuccessStatusCode) return new ExchangeResult(response.StatusCode, null);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            throw new InvalidOperationException(
                "Desktop token endpoint başarılı durum kodu döndürdü fakat cevap gövdesi boş.");
        }

        var auth = JsonSerializer.Deserialize<AuthResponse>(responseBody, JsonOptions)
            ?? throw new InvalidOperationException(
                "Desktop token cevabı AuthResponse nesnesine dönüştürülemedi.");

        return new ExchangeResult(response.StatusCode, auth);
    }

    public async Task<AuthResponse?> ExchangeDesktopCodeAsync(ExchangeDesktopCodeRequest payload, CancellationToken cancellationToken)
        => (await ExchangeDesktopCodeDetailedAsync(payload, cancellationToken)).AuthResponse;

    public async IAsyncEnumerable<ChatCompletionChunk> StreamChatAsync(string message, Guid? chatSessionId, string? idempotencyKey, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_token)) yield break;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Content = new StringContent(JsonSerializer.Serialize(new ChatCompletionRequest(chatSessionId, message, null, null, null, true, idempotencyKey), JsonOptions), Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new AgentChatRequestException(response.StatusCode, body);
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var chunk = JsonSerializer.Deserialize<ChatCompletionChunk>(line[5..].Trim(), JsonOptions);
            if (chunk is not null) yield return chunk;
        }
    }

    public async Task<DirectChatCreditStatusResult> GetDirectChatCreditStatusAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_token)) return new(null, "not_authenticated");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/chat/credits");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(null, "not_authenticated");
            if (!response.IsSuccessStatusCode) return new(null, "http_error");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var status = await JsonSerializer.DeserializeAsync<DirectChatCreditStatusDto>(stream, JsonOptions, cancellationToken);
            return status is null ? new(null, "invalid_response") : new(status, "ok");
        }
        catch (OperationCanceledException) { return new(null, "cancelled"); }
        catch (HttpRequestException) { return new(null, "transport_error"); }
    }

    public async Task<DirectChatCreditStatusDto?> GetDirectChatCreditsAsync(CancellationToken cancellationToken)
        => (await GetDirectChatCreditStatusAsync(cancellationToken)).Status;

    private async Task<TResponse?> PostAsync<TRequest, TResponse>(string path, TRequest payload, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(path, new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"), cancellationToken);
        if (!response.IsSuccessStatusCode) return default;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<TResponse>(stream, JsonOptions, cancellationToken);
    }

    private async Task<TResponse?> PostRequiredAsync<TRequest, TResponse>(string path, TRequest payload, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(path, new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"), cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<TResponse>(stream, JsonOptions, cancellationToken);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new AgentChatRequestException(response.StatusCode, body);
    }
}

