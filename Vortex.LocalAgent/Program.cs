using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vortex.Shared;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["LocalAgent:Url"] ?? "http://127.0.0.1:47891");
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Services.AddHttpClient();
builder.Services.AddHostedService<DeviceJobPollingService>();
var app = builder.Build();
var localSecret = builder.Configuration["LocalAgent:Secret"] ?? Environment.GetEnvironmentVariable("VORTEX_LOCAL_AGENT_SECRET");

var tools = new List<LocalToolDescriptor>
{
    new("read-selected-file", "Kullanıcının seçtiği güvenli metin dosyasını okur.", true, false, LocalToolRiskLevel.Low),
    new("open-program-request", "Program açma isteğini doğrular fakat otomatik çalıştırmaz.", true, true, LocalToolRiskLevel.High),
    new("speak-preview", "TTS entegrasyonu için güvenli önizleme yanıtı döndürür.", true, false, LocalToolRiskLevel.Low),
    new("pardus_set_theme", "Pardus masaüstü temasını güvenli allowlist komutlarıyla koyu moda alır.", true, false, LocalToolRiskLevel.Low),
    new("pardus_set_wallpaper", "Pardus XFCE masaüstü arka planını güvenli allowlist komutlarıyla siyah düz renge alır.", true, false, LocalToolRiskLevel.Low),
    new("jarvis_open_app", "Jarvis allowlistindeki uygulamayı açar.", true, true, LocalToolRiskLevel.Medium),
    new("jarvis_open_file", "Kullanıcı klasörlerindeki allowlistli dosyayı açar.", true, true, LocalToolRiskLevel.Medium),
    new("jarvis_create_folder", "Masaüstünde klasör oluşturur.", true, false, LocalToolRiskLevel.Low),
    new("jarvis_add_note", "Yerel not dosyasına metin ekler.", true, false, LocalToolRiskLevel.Low),
    new("jarvis_lock_screen", "Ekranı kilitler.", true, true, LocalToolRiskLevel.High),
    new("jarvis_write_document", "Masaüstünde metin belgesi oluşturur.", true, false, LocalToolRiskLevel.Low),
    new("run_cmd", "Kullanıcı onaylı serbest komut (shell yok; ArgumentList ile).", true, true, LocalToolRiskLevel.High)
};

app.MapGet("/health", (HttpRequest httpRequest) =>
    IsAuthorized(httpRequest, localSecret)
        ? Results.Ok(new LocalAgentHello("Vortex Local Agent", "0.1.0", RuntimeInformation.OSDescription, tools))
        : Results.Unauthorized());
app.MapGet("/api/pardus/desktop", (HttpRequest httpRequest) => IsAuthorized(httpRequest, localSecret) ? Results.Ok(new { desktopEnvironment = LocalAgentPardusTheme.DetectDesktopEnvironment().ToString() }) : Results.Unauthorized());
app.MapPost("/api/pardus/theme", async (HttpRequest httpRequest, CancellationToken ct) =>
{
    if (!IsAuthorized(httpRequest, localSecret)) return Results.Unauthorized();

    string body;
    using (var reader = new StreamReader(httpRequest.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false))
    {
        body = await reader.ReadToEndAsync(ct);
    }

    ModeSelection mode;
    bool dryRun;
    JsonDocument? document = null;
    try
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            mode = LocalAgentPardusTheme.ReadModeOrDefault((JsonElement?)null);
            dryRun = LocalAgentPardusTheme.ReadDryRunOrDefault(null);
        }
        else
        {
            document = JsonDocument.Parse(body);
            mode = LocalAgentPardusTheme.ReadModeOrDefault(document.RootElement);
            dryRun = LocalAgentPardusTheme.ReadDryRunOrDefault(document.RootElement);
        }
    }
    catch (JsonException ex)
    {
        app.Logger.LogWarning(ex, "Pardus theme request invalid JSON");
        return Results.BadRequest(PardusThemeExecutionResult.InvalidJson(ex.Message).ToResponsePayload(deviceId: null, deviceToken: null));
    }
    finally
    {
        document?.Dispose();
    }

    app.Logger.LogInformation("Pardus theme request stage=preflight mode={Mode} dryRun={DryRun}", mode.RawValue, dryRun);
    var result = await LocalAgentPardusTheme.HandleAsync(new PardusThemeExecutionOptions(mode.Value, dryRun, false, TimeSpan.FromSeconds(30)), app.Logger, ct);
    var payload = result.ToResponsePayload(deviceId: null, deviceToken: null);
    return result.Success
        ? Results.Ok(payload)
        : Results.BadRequest(payload);
});
app.MapGet("/api/audio/devices", () => Results.Ok(new[] { new AudioDeviceDto("default", "Varsayılan sistem cihazı", true, true) }));

app.MapPost("/api/tools/invoke", async (LocalToolRequest request, HttpRequest httpRequest, CancellationToken ct) =>
{
    if (!IsAuthorized(httpRequest, localSecret)) return Results.Unauthorized();
    if (request.ExpiresAt < DateTimeOffset.UtcNow) return Results.BadRequest(new LocalToolResponse(request.RequestId, false, "İstek süresi geçmiş."));
    var descriptor = tools.FirstOrDefault(t => t.Name == request.ToolName && t.IsEnabled);
    if (descriptor is null) return Results.BadRequest(new LocalToolResponse(request.RequestId, false, "Araç kayıtlı veya etkin değil."));
    var isDryRun = request.Arguments.TryGetValue("dryRun", out var dryRunValue) && bool.TryParse(dryRunValue, out var parsedDryRun) && parsedDryRun;
    if (descriptor.RequiresConfirmation && !request.UserConfirmed && !isDryRun) return Results.BadRequest(new LocalToolResponse(request.RequestId, false, "Bu işlem kullanıcı onayı gerektirir."));

    if (request.ToolName == "speak-preview")
    {
        var text = request.Arguments.TryGetValue("text", out var value) ? value : string.Empty;
        return Results.Ok(new LocalToolResponse(request.RequestId, true, "TTS önizleme hazırlandı.", text.Length > 300 ? text[..300] : text));
    }

    if (request.ToolName == "read-selected-file")
    {
        if (!request.Arguments.TryGetValue("path", out var path) || !TryResolveSafeSelectedFile(path, out var safePath) || !File.Exists(safePath)) return Results.BadRequest(new LocalToolResponse(request.RequestId, false, "Dosya bulunamadı."));
        var ext = Path.GetExtension(safePath).ToLowerInvariant();
        if (!SupportedFileTypes.Extensions.Contains(ext)) return Results.BadRequest(new LocalToolResponse(request.RequestId, false, "Desteklenmeyen dosya türü."));
        var info = new FileInfo(safePath);
        if (info.Length > 2 * 1024 * 1024) return Results.BadRequest(new LocalToolResponse(request.RequestId, false, "Dosya boyutu ilk sürüm sınırını aşıyor."));
        return Results.Ok(new LocalToolResponse(request.RequestId, true, "Dosya okundu.", await File.ReadAllTextAsync(safePath, ct)));
    }

    if (request.ToolName == "open-program-request")
    {
        return Results.Ok(new LocalToolResponse(request.RequestId, false, "Güvenlik nedeniyle ilk sürümde program otomatik çalıştırılmaz. Desktop açık kullanıcı onayıyla platform kabuğuna devredebilir."));
    }


    if (request.ToolName.StartsWith("jarvis_", StringComparison.Ordinal))
    {
        var result = request.ToolName switch
        {
            "jarvis_open_app" => await LocalAgentJarvisTools.OpenAppAsync(request.Arguments.GetValueOrDefault("appName", string.Empty), dryRun: isDryRun, ct),
            "jarvis_open_file" => await LocalAgentJarvisTools.OpenFileAsync(request.Arguments.GetValueOrDefault("path", string.Empty), dryRun: isDryRun, ct),
            "jarvis_create_folder" => await LocalAgentJarvisTools.CreateFolderAsync(request.Arguments.GetValueOrDefault("name", string.Empty), dryRun: isDryRun, ct),
            "jarvis_add_note" => await LocalAgentJarvisTools.AddNoteAsync(request.Arguments.GetValueOrDefault("text", string.Empty), dryRun: isDryRun, ct),
            "jarvis_lock_screen" => await LocalAgentJarvisTools.LockScreenAsync(dryRun: isDryRun, ct),
            "jarvis_write_document" => await LocalAgentJarvisTools.WriteDocumentAsync(request.Arguments.GetValueOrDefault("topic", string.Empty), dryRun: isDryRun, ct),
            _ => new PardusThemeExecutionResult("UNSUPPORTED_TOOL", false, "Jarvis tool is not allowlisted.", null, null, PardusDesktopEnvironment.Unknown, false, null, OperatingSystemInfo.Detect(), ["policy_rejected"])
        };
        var localResponse = new LocalToolResponse(request.RequestId, result.Success, result.Message, result.Output);
        return result.Success ? Results.Ok(localResponse) : Results.BadRequest(localResponse);
    }

    if (request.ToolName == "run_cmd")
    {
        var command = request.Arguments.GetValueOrDefault("command", string.Empty);
        var workingDirectory = request.Arguments.GetValueOrDefault("workingDirectory", string.Empty);
        var dryRun = request.Arguments.TryGetValue("dryRun", out var dryRunText) && bool.TryParse(dryRunText, out var parsedRunDryRun) && parsedRunDryRun;
        var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
        var runLogger = loggerFactory.CreateLogger("run_cmd_api");
        var result = await LocalAgentRunCmd.HandleAsync(command, workingDirectory, dryRun, TimeSpan.FromSeconds(30), runLogger, ct);
        var response = new LocalToolResponse(request.RequestId, result.Success, result.Message, result.DryRun ? $"Önizleme: {result.PlannedCommand}" : result.PlannedCommand is null ? result.Output : $"{result.PlannedCommand}\n{result.Output}");
        return result.Success ? Results.Ok(response) : Results.BadRequest(response);
    }

    return Results.BadRequest(new LocalToolResponse(request.RequestId, false, "Araç uygulanmadı."));
});

app.MapPost("/api/stt/transcribe", () => Results.Json(new { error = "stt_provider_unavailable", message = "Yerel STT motoru yapılandırılmadı." }, statusCode: StatusCodes.Status503ServiceUnavailable));
app.MapPost("/api/tts/speak", () => Results.Json(new TextToSpeechResponse(false, "Yerel TTS motoru yapılandırılmadı."), statusCode: StatusCodes.Status503ServiceUnavailable));

app.Run();

static bool IsAuthorized(HttpRequest request, string? secret)
{
    if (string.IsNullOrWhiteSpace(secret)) return false;
    var header = request.Headers.Authorization.ToString();
    return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && FixedTimeEquals(header[7..], secret);
}

static bool FixedTimeEquals(string value, string secret)
{
    var valueBytes = System.Text.Encoding.UTF8.GetBytes(value);
    var secretBytes = System.Text.Encoding.UTF8.GetBytes(secret);
    return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(valueBytes, secretBytes);
}

static bool TryResolveSafeSelectedFile(string path, out string safePath)
{
    safePath = string.Empty;
    if (string.IsNullOrWhiteSpace(path)) return false;
    var fullPath = Path.GetFullPath(path);
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    if (string.IsNullOrWhiteSpace(home)) return false;
    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    var allowedRoots = new[]
    {
        Path.Combine(home, "Desktop"),
        Path.Combine(home, "Documents"),
        Path.Combine(home, "Downloads")
    };
    if (!allowedRoots.Any(root => IsInside(root, fullPath, comparison))) return false;
    var info = new FileInfo(fullPath);
    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return false;
    safePath = fullPath;
    return true;
}

static bool IsInside(string root, string target, StringComparison comparison)
{
    var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var normalizedTarget = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    return normalizedTarget.Equals(normalizedRoot, comparison) || normalizedTarget.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
}

sealed class DeviceJobPollingService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private const int MaxExecutorWebSocketMessageBytes = 256 * 1024;
    private static readonly IReadOnlyDictionary<string, ExecutorPolicy> ExecutorPolicies = new Dictionary<string, ExecutorPolicy>(StringComparer.Ordinal)
    {
        ["pardus_set_theme"] = new("pardus_set_theme", true, false, LocalToolRiskLevel.Low, ["mode", "dryRun"]),
        ["pardus_set_wallpaper"] = new("pardus_set_wallpaper", true, false, LocalToolRiskLevel.Low, ["color", "dryRun"]),
        ["jarvis_open_app"] = new("jarvis_open_app", true, true, LocalToolRiskLevel.Medium, ["appName"]),
        ["jarvis_open_file"] = new("jarvis_open_file", true, true, LocalToolRiskLevel.Medium, ["path"]),
        ["jarvis_create_folder"] = new("jarvis_create_folder", true, false, LocalToolRiskLevel.Low, ["name"]),
        ["jarvis_add_note"] = new("jarvis_add_note", true, false, LocalToolRiskLevel.Low, ["text"]),
        ["jarvis_lock_screen"] = new("jarvis_lock_screen", true, true, LocalToolRiskLevel.High, []),
        ["jarvis_write_document"] = new("jarvis_write_document", true, false, LocalToolRiskLevel.Low, ["topic"]),
        ["run_cmd"] = new("run_cmd", true, true, LocalToolRiskLevel.High, ["command", "workingDirectory"])
    };
    private readonly ConcurrentDictionary<string, CancellationTokenSource> activeExecutorJobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim executorSendLock = new(1, 1);
    private readonly IHttpClientFactory httpClientFactory;
    private readonly ILogger<DeviceJobPollingService> logger;
    private readonly LocalAgentDeviceConfig config;

    public DeviceJobPollingService(IHttpClientFactory httpClientFactory, ILogger<DeviceJobPollingService> logger)
    {
        this.httpClientFactory = httpClientFactory;
        this.logger = logger;
        config = LocalAgentDeviceConfig.Load(logger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("LocalAgent serverUrl={ServerUrl} deviceName={DeviceName} credentialSource={CredentialSource}", config.ServerUrl ?? "(none)", config.DeviceName, config.CredentialSource);
        if (string.IsNullOrWhiteSpace(config.ServerUrl))
        {
            logger.LogInformation("SERVER_CONNECTED_MODE_DISABLED missing server URL");
            return;
        }

        var credentials = await ResolveCredentialsAsync(stoppingToken);
        if (credentials is null)
        {
            logger.LogInformation("SERVER_CONNECTED_MODE_DISABLED missing device credentials");
            return;
        }

        if (config.UseWebSocketExecutor && await RunWebSocketExecutorAsync(credentials, stoppingToken)) return;

        logger.LogInformation("Device job polling started intervalSeconds={IntervalSeconds}", config.PollingInterval.TotalSeconds);
        using var timer = new PeriodicTimer(config.PollingInterval);
        do
        {
            await PollOnceAsync(credentials, stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task<bool> RunWebSocketExecutorAsync(DeviceCredentials credentials, CancellationToken ct)
    {
        try
        {
            var endpoint = BuildExecutorWebSocketUri(credentials);
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("X-Vortex-Device-Token", credentials.DeviceToken);
            await socket.ConnectAsync(endpoint, ct);
            logger.LogInformation("Executor websocket connected endpoint={Endpoint}", endpoint.GetLeftPart(UriPartial.Path));

            await SendExecutorMessageAsync(socket, new ExecutorWireMessage("hello", JsonSerializer.SerializeToElement(new ExecutorHello(credentials.DeviceId, "0.1.0", RuntimeInformation.OSDescription, string.Join(',', ExecutorPolicies.Keys), DateTimeOffset.UtcNow), JsonOptions)), ct);
            var buffer = new byte[16 * 1024];
            var heartbeatLoop = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(config.ExecutorHeartbeatInterval);
                while (socket.State == WebSocketState.Open && await timer.WaitForNextTickAsync(ct))
                {
                    await SendExecutorMessageAsync(socket, new ExecutorWireMessage("heartbeat", JsonSerializer.SerializeToElement(new ExecutorHeartbeat(credentials.DeviceId, true, "ready", DateTimeOffset.UtcNow), JsonOptions)), ct);
                }
            }, ct);
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var payload = await ReceiveExecutorMessageAsync(socket, buffer, MaxExecutorWebSocketMessageBytes, ct);
                if (payload is null) break;
                await HandleExecutorMessageAsync(socket, credentials, payload, ct);
            }

            try { await heartbeatLoop; } catch { }

            return ct.IsCancellationRequested;
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException or OperationCanceledException)
        {
            if (!ct.IsCancellationRequested) logger.LogWarning(ex, "Executor websocket unavailable; falling back to polling");
            return false;
        }
    }

    private Task HandleExecutorMessageAsync(ClientWebSocket socket, DeviceCredentials credentials, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var wire = JsonSerializer.Deserialize<ExecutorWireMessage>(payload.Span, JsonOptions);
        if (wire is null) return Task.CompletedTask;

        if (string.Equals(wire.Type, "cancel", StringComparison.OrdinalIgnoreCase) && wire.Payload is { } cancelPayload)
        {
            var cancel = cancelPayload.Deserialize<ExecutorCancelJob>(JsonOptions);
            if (cancel is not null && activeExecutorJobs.TryGetValue(cancel.JobId, out var cancellation))
            {
                cancellation.Cancel();
                logger.LogInformation("Executor websocket cancellation requested jobId={JobId} reason={Reason}", cancel.JobId, cancel.Reason);
            }
            return Task.CompletedTask;
        }

        if (!string.Equals(wire.Type, "job", StringComparison.OrdinalIgnoreCase) || wire.Payload is null) return Task.CompletedTask;

        var executorJob = wire.Payload.Value.Deserialize<ExecutorDeviceJob>(JsonOptions);
        if (executorJob is null || string.IsNullOrWhiteSpace(executorJob.JobId)) return Task.CompletedTask;

        var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!activeExecutorJobs.TryAdd(executorJob.JobId, jobCancellation))
        {
            jobCancellation.Dispose();
            return Task.CompletedTask;
        }

        _ = RunExecutorJobAsync(socket, credentials, executorJob, jobCancellation);
        return Task.CompletedTask;
    }

    private async Task RunExecutorJobAsync(ClientWebSocket socket, DeviceCredentials credentials, ExecutorDeviceJob executorJob, CancellationTokenSource jobCancellation)
    {
        try
        {
            var job = new DeviceJobDto(executorJob.JobId, executorJob.ToolName, executorJob.Arguments);
            logger.LogInformation("Executor websocket job received jobId={JobId} tool={ToolName}", job.JobId, job.ToolName);
            var result = await DispatchJobAsync(job, jobCancellation.Token, async (stream, text) =>
            {
                var chunk = new ExecutorStreamChunk(job.JobId, stream, text, DateTimeOffset.UtcNow);
                await SendExecutorMessageAsync(socket, new ExecutorWireMessage("stream", JsonSerializer.SerializeToElement(chunk, JsonOptions)), CancellationToken.None);
            });
            result = result with { Timeline = result.Timeline.Append("report_sent").ToArray() };
            var resultMessage = new ExecutorWireMessage("result", JsonSerializer.SerializeToElement(new ExecutorDeviceJobResult(job.JobId, result.Success, result.Code, result.Message, result.DryRun, result.PlannedCommand, result.Output, result.TechnicalDetails, result.Timeline), JsonOptions));
            try
            {
                await SendExecutorMessageAsync(socket, resultMessage, CancellationToken.None);
                logger.LogInformation("Executor websocket result sent jobId={JobId}", job.JobId);
            }
            catch (WebSocketException ex)
            {
                logger.LogWarning(ex, "Executor websocket result failed; using HTTP completion fallback jobId={JobId}", job.JobId);
                var client = CreateClient();
                using var completeResponse = await client.PostAsJsonAsync($"/api/device-jobs/{Uri.EscapeDataString(job.JobId)}/complete", result.ToCompleteRequest(credentials), JsonOptions, CancellationToken.None);
                logger.LogInformation("Executor HTTP fallback complete jobId={JobId} statusCode={StatusCode}", job.JobId, (int)completeResponse.StatusCode);
            }
        }
        catch (OperationCanceledException) when (jobCancellation.IsCancellationRequested)
        {
            var cancelled = new ExecutorDeviceJobResult(executorJob.JobId, false, "CANCELLED", "Executor job was cancelled.", false, null, null, null, ["job_received", "cancelled"]);
            await SendExecutorMessageAsync(socket, new ExecutorWireMessage("result", JsonSerializer.SerializeToElement(cancelled, JsonOptions)), CancellationToken.None);
        }
        finally
        {
            activeExecutorJobs.TryRemove(executorJob.JobId, out _);
            jobCancellation.Dispose();
        }
    }


    private Uri BuildExecutorWebSocketUri(DeviceCredentials credentials)
    {
        var server = new Uri(config.ServerUrl!, UriKind.Absolute);
        var scheme = server.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        var builder = new UriBuilder(server)
        {
            Scheme = scheme,
            Path = "/api/ws/agent",
            Query = $"executorId={Uri.EscapeDataString(credentials.DeviceId)}&version=0.1.0"
        };
        return builder.Uri;
    }


    private static async Task<byte[]?> ReceiveExecutorMessageAsync(ClientWebSocket socket, byte[] buffer, int maxBytes, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        while (true)
        {
            var received = await socket.ReceiveAsync(buffer, ct);
            if (received.MessageType == WebSocketMessageType.Close) return null;
            if (received.MessageType != WebSocketMessageType.Text) continue;
            if (stream.Length + received.Count > maxBytes) throw new InvalidOperationException("Executor WebSocket message exceeded the maximum allowed size.");
            stream.Write(buffer, 0, received.Count);
            if (received.EndOfMessage) return stream.ToArray();
        }
    }

    private async Task SendExecutorMessageAsync<T>(ClientWebSocket socket, T message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        await executorSendLock.WaitAsync(ct);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            executorSendLock.Release();
        }
    }

    private async Task<DeviceCredentials?> ResolveCredentialsAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(config.DeviceId) && !string.IsNullOrWhiteSpace(config.DeviceToken)) return new(config.DeviceId, config.DeviceToken);
        if (!string.IsNullOrWhiteSpace(config.UserToken))
        {
            try
            {
                var client = CreateClient();
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/devices/register") { Content = JsonContent.Create(new { deviceName = config.DeviceName }, options: JsonOptions) };
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.UserToken);
                using var response = await client.SendAsync(request, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Device registration failed statusCode={StatusCode}", (int)response.StatusCode);
                    return null;
                }

                var registered = JsonSerializer.Deserialize<DeviceRegisterResponse>(body, JsonOptions);
                if (registered is null || string.IsNullOrWhiteSpace(registered.DeviceId) || string.IsNullOrWhiteSpace(registered.DeviceToken)) return null;
                SaveCredentials(new DeviceCredentials(registered.DeviceId, registered.DeviceToken));
                logger.LogInformation("Device credentials saved source=registration path={Path}", config.CredentialsPath);
                return new DeviceCredentials(registered.DeviceId, registered.DeviceToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Device registration failed");
            }
        }

        return null;
    }

    private async Task PollOnceAsync(DeviceCredentials credentials, CancellationToken ct)
    {
        try
        {
            var client = CreateClient();
            using var claimResponse = await client.PostAsJsonAsync("/api/device-jobs/claim", new DeviceJobClaimRequest(credentials.DeviceId, credentials.DeviceToken), JsonOptions, ct);
            var claimBody = await claimResponse.Content.ReadAsStringAsync(ct);
            if (!claimResponse.IsSuccessStatusCode)
            {
                logger.LogWarning("Device job claim failed statusCode={StatusCode}", (int)claimResponse.StatusCode);
                return;
            }

            var claim = JsonSerializer.Deserialize<DeviceJobClaimResponse>(claimBody, JsonOptions);
            if (claim?.Job is null || string.IsNullOrWhiteSpace(claim.Job.JobId)) return;
            logger.LogInformation("Device job claimed jobId={JobId} tool={ToolName}", claim.Job.JobId, claim.Job.ToolName);
            var result = await DispatchJobAsync(claim.Job, ct);
            logger.LogInformation("Device job lifecycle jobId={JobId} status={Status} message={Message}", claim.Job.JobId, result.Code, result.Message);
            result = result with { Timeline = result.Timeline.Append("report_sent").ToArray() };
            using var completeResponse = await client.PostAsJsonAsync($"/api/device-jobs/{Uri.EscapeDataString(claim.Job.JobId)}/complete", result.ToCompleteRequest(credentials), JsonOptions, ct);
            logger.LogInformation("Device job complete result jobId={JobId} statusCode={StatusCode} event=report_sent", claim.Job.JobId, (int)completeResponse.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Device job polling failed");
        }
    }

    private async Task<DeviceJobResult> DispatchJobAsync(DeviceJobDto job, CancellationToken ct, Func<string, string, Task>? chunkSink = null)
    {
        var policyResult = ValidateExecutorJobPolicy(job);
        if (policyResult is not null) return policyResult;

        if (string.Equals(job.ToolName, "jarvis_open_app", StringComparison.Ordinal))
        {
            var appName = LocalAgentJarvisTools.ReadArgument(job.Arguments, "appName") ?? string.Empty;
            var result = await LocalAgentJarvisTools.OpenAppAsync(appName, config.DryRun, ct);
            return DeviceJobResult.FromExecution(result with { Timeline = result.Timeline.Prepend("job_received").ToArray() });
        }

        if (string.Equals(job.ToolName, "jarvis_open_file", StringComparison.Ordinal))
        {
            var path = LocalAgentJarvisTools.ReadArgument(job.Arguments, "path") ?? string.Empty;
            var result = await LocalAgentJarvisTools.OpenFileAsync(path, config.DryRun, ct);
            return DeviceJobResult.FromExecution(result with { Timeline = result.Timeline.Prepend("job_received").ToArray() });
        }

        if (string.Equals(job.ToolName, "jarvis_create_folder", StringComparison.Ordinal))
        {
            var name = LocalAgentJarvisTools.ReadArgument(job.Arguments, "name") ?? string.Empty;
            var result = await LocalAgentJarvisTools.CreateFolderAsync(name, config.DryRun, ct);
            return DeviceJobResult.FromExecution(result with { Timeline = result.Timeline.Prepend("job_received").ToArray() });
        }

        if (string.Equals(job.ToolName, "jarvis_add_note", StringComparison.Ordinal))
        {
            var text = LocalAgentJarvisTools.ReadArgument(job.Arguments, "text") ?? string.Empty;
            var result = await LocalAgentJarvisTools.AddNoteAsync(text, config.DryRun, ct);
            return DeviceJobResult.FromExecution(result with { Timeline = result.Timeline.Prepend("job_received").ToArray() });
        }

        if (string.Equals(job.ToolName, "jarvis_lock_screen", StringComparison.Ordinal))
        {
            var result = await LocalAgentJarvisTools.LockScreenAsync(config.DryRun, ct);
            return DeviceJobResult.FromExecution(result with { Timeline = result.Timeline.Prepend("job_received").ToArray() });
        }

        if (string.Equals(job.ToolName, "jarvis_write_document", StringComparison.Ordinal))
        {
            var topic = LocalAgentJarvisTools.ReadArgument(job.Arguments, "topic") ?? string.Empty;
            var result = await LocalAgentJarvisTools.WriteDocumentAsync(topic, config.DryRun, ct);
            return DeviceJobResult.FromExecution(result with { Timeline = result.Timeline.Prepend("job_received").ToArray() });
        }
        if (string.Equals(job.ToolName, "pardus_set_theme", StringComparison.Ordinal))
        {
            var mode = LocalAgentPardusTheme.ReadModeOrDefault(job.Arguments);
            var dryRun = LocalAgentPardusTheme.ReadDryRunOrDefault(job.Arguments) || config.DryRun;
            logger.LogInformation("Device job event=job_received jobId={JobId} mode={Mode} dryRun={DryRun}", job.JobId, mode.RawValue, dryRun);
            var result = await LocalAgentPardusTheme.HandleAsync(new PardusThemeExecutionOptions(mode.Value, dryRun, config.RequireUserApproval, config.JobTimeout), logger, ct, chunkSink);
            logger.LogInformation("Device job result jobId={JobId} dryRun={DryRun} approvalRequired={ApprovalRequired} success={Success} code={Code} message={Message}", job.JobId, dryRun, config.RequireUserApproval, result.Success, result.Status, result.Message);
            return DeviceJobResult.FromExecution(result with { Timeline = result.Timeline.Prepend("job_received").ToArray() });
        }

        if (string.Equals(job.ToolName, "pardus_set_wallpaper", StringComparison.Ordinal))
        {
            var color = LocalAgentPardusWallpaper.ReadColorOrDefault(job.Arguments);
            var dryRun = LocalAgentPardusTheme.ReadDryRunOrDefault(job.Arguments);
            logger.LogInformation("Device job event=job_received jobId={JobId} color={Color} dryRun={DryRun}", job.JobId, color.RawValue, dryRun);
            var result = await LocalAgentPardusWallpaper.HandleAsync(new PardusWallpaperExecutionOptions(color.Value, dryRun, config.RequireUserApproval, config.JobTimeout), logger, ct, chunkSink);
            logger.LogInformation("Device job result jobId={JobId} dryRun={DryRun} approvalRequired={ApprovalRequired} success={Success} code={Code} message={Message}", job.JobId, dryRun, config.RequireUserApproval, result.Success, result.Status, result.Message);
            return DeviceJobResult.FromExecution(result with { Timeline = result.Timeline.Prepend("job_received").ToArray() });
        }

        if (string.Equals(job.ToolName, "run_cmd", StringComparison.Ordinal))
        {
            var command = LocalAgentJarvisTools.ReadArgument(job.Arguments, "command") ?? string.Empty;
            var workingDirectory = LocalAgentJarvisTools.ReadArgument(job.Arguments, "workingDirectory");
            logger.LogInformation("Device job event=job_received jobId={JobId} tool=run_cmd dryRun={DryRun}", job.JobId, config.DryRun);
            var result = await LocalAgentRunCmd.HandleAsync(command, workingDirectory, config.DryRun, config.JobTimeout, logger, ct, chunkSink);
            logger.LogInformation("Device job result jobId={JobId} tool=run_cmd success={Success} code={Code}", job.JobId, result.Success, result.Status);
            return DeviceJobResult.FromExecution(result with { Timeline = result.Timeline.Prepend("job_received").ToArray() });
        }

        return DeviceJobResult.FromExecution(new PardusThemeExecutionResult("UNSUPPORTED_TOOL", false, "Requested executor tool is not supported by the current allowlist.", null, null, PardusDesktopEnvironment.Unknown, false, null, OperatingSystemInfo.Detect(), ["job_received"]));
    }

    private DeviceJobResult? ValidateExecutorJobPolicy(DeviceJobDto job)
    {
        if (!ExecutorPolicies.TryGetValue(job.ToolName, out var policy) || !policy.Enabled)
        {
            return DeviceJobResult.FromExecution(new PardusThemeExecutionResult("UNSUPPORTED_TOOL", false, "Executor tool is not allowlisted.", null, null, PardusDesktopEnvironment.Unknown, false, null, OperatingSystemInfo.Detect(), ["job_received", "policy_rejected"]));
        }

        if (policy.RequiresApproval && !config.RequireUserApproval)
        {
            return DeviceJobResult.FromExecution(new PardusThemeExecutionResult("USER_APPROVAL_REQUIRED", false, "Executor policy requires explicit user approval.", null, null, PardusDesktopEnvironment.Unknown, false, null, OperatingSystemInfo.Detect(), ["job_received", "approval_required"]));
        }

        if (job.Arguments is { ValueKind: JsonValueKind.Object } arguments)
        {
            foreach (var property in arguments.EnumerateObject())
            {
                if (!policy.AllowedArguments.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                {
                    return DeviceJobResult.FromExecution(new PardusThemeExecutionResult("UNSUPPORTED_ARGUMENT", false, $"Executor argument is not allowlisted: {property.Name}", null, null, PardusDesktopEnvironment.Unknown, false, null, OperatingSystemInfo.Detect(), ["job_received", "policy_rejected"]));
                }
            }
        }

        return null;
    }

    private HttpClient CreateClient()
    {
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(config.ServerUrl!, UriKind.Absolute);
        client.Timeout = config.JobTimeout + TimeSpan.FromSeconds(10);
        return client;
    }

    private void SaveCredentials(DeviceCredentials credentials)
    {
        Directory.CreateDirectory(config.DataDir);
        File.WriteAllText(config.CredentialsPath, JsonSerializer.Serialize(credentials, JsonOptions));
    }
}

sealed record LocalAgentDeviceConfig(string? ServerUrl, string? UserToken, string? DeviceId, string? DeviceToken, string DeviceName, string DataDir, bool DryRun, bool RequireUserApproval, bool UseWebSocketExecutor, TimeSpan PollingInterval, TimeSpan ExecutorHeartbeatInterval, TimeSpan JobTimeout, string CredentialSource)
{
    public string CredentialsPath => Path.Combine(DataDir, "device-credentials.json");

    public static LocalAgentDeviceConfig Load(ILogger logger)
    {
        var dataDir = Environment.GetEnvironmentVariable("VORTEX_DEVICE_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dataDir)) dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "VortexAI", "LocalAgent");
        var deviceId = Environment.GetEnvironmentVariable("VORTEX_DEVICE_ID");
        var deviceToken = Environment.GetEnvironmentVariable("VORTEX_DEVICE_TOKEN");
        var source = !string.IsNullOrWhiteSpace(deviceId) && !string.IsNullOrWhiteSpace(deviceToken) ? "environment" : "none";
        var credentialPath = Path.Combine(dataDir, "device-credentials.json");
        if (source == "none" && File.Exists(credentialPath))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<DeviceCredentials>(File.ReadAllText(credentialPath), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (saved is not null && !string.IsNullOrWhiteSpace(saved.DeviceId) && !string.IsNullOrWhiteSpace(saved.DeviceToken))
                {
                    deviceId = saved.DeviceId;
                    deviceToken = saved.DeviceToken;
                    source = "device-credentials.json";
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Failed to load device credentials file");
            }
        }

        var deviceName = Environment.GetEnvironmentVariable("VORTEX_DEVICE_NAME");
        if (string.IsNullOrWhiteSpace(deviceName)) deviceName = Environment.MachineName;
        return new(
            NormalizeServerUrl(Environment.GetEnvironmentVariable("VORTEX_SERVER_URL")),
            Environment.GetEnvironmentVariable("VORTEX_USER_TOKEN"),
            deviceId,
            deviceToken,
            deviceName,
            dataDir,
            ReadBool("VORTEX_DRY_RUN"),
            ReadBool("VORTEX_REQUIRE_USER_APPROVAL"),
            ReadBool("VORTEX_EXECUTOR_WEBSOCKET"),
            TimeSpan.FromSeconds(ReadPositiveInt("VORTEX_POLLING_INTERVAL_SECONDS", 10)),
            TimeSpan.FromSeconds(ReadPositiveInt("VORTEX_EXECUTOR_HEARTBEAT_SECONDS", 20)),
            TimeSpan.FromSeconds(ReadPositiveInt("VORTEX_JOB_TIMEOUT_SECONDS", 60)),
            source);
    }

    private static string? NormalizeServerUrl(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimEnd('/');
    private static bool ReadBool(string name) => bool.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value;
    private static int ReadPositiveInt(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;
}

static class LocalAgentPardusTheme
{
    public static async Task<PardusThemeExecutionResult> HandleAsync(PardusThemeExecutionOptions options, ILogger logger, CancellationToken ct, Func<string, string, Task>? chunkSink = null)
    {
        var timeline = new List<string> { "preflight_started" };
        var osInfo = OperatingSystemInfo.Detect();
        if (options.Mode is not ("dark" or "light")) return NewResult("INVALID_MODE", false, "INVALID_MODE", options, null, null, PardusDesktopEnvironment.Unknown, "Mode must be dark or light.", timeline, osInfo);

        var desktop = DetectDesktopEnvironment();
        var command = BuildThemeCommand(desktop, options.Mode);
        var planned = command is null ? null : FormatCommand(command.Value);
        var preflight = CheckPreflight(desktop, command, osInfo);
        timeline.Add(preflight.Success ? "preflight_passed" : "preflight_failed");
        logger.LogInformation("Pardus theme event={Event} mode={Mode} desktop={Desktop} success={Success} details={Details} distroId={DistroId}", preflight.Success ? "preflight_passed" : "preflight_failed", options.Mode, desktop, preflight.Success, preflight.Details, osInfo.Id);
        if (!preflight.Success) return NewResult("PREFLIGHT_FAILED", false, "PREFLIGHT_FAILED", options, planned, null, desktop, preflight.Details, timeline, osInfo);

        if (options.DryRun)
        {
            timeline.Add("dry_run_completed");
            logger.LogInformation("Pardus theme event=dry_run_completed plannedCommand={PlannedCommand}", planned);
            return NewResult("DRY_RUN", true, "Dry-run accepted.", options, planned, null, desktop, "No command executed.", timeline, osInfo);
        }

        if (options.RequireUserApproval)
        {
            timeline.Add("approval_required");
            logger.LogInformation("Pardus theme event=approval_required plannedCommand={PlannedCommand}", planned);
            return NewResult("USER_APPROVAL_REQUIRED", false, "USER_APPROVAL_REQUIRED", options, planned, null, desktop, "User approval is required before execution.", timeline, osInfo);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(options.Timeout);
        timeline.Add("execution_started");
        logger.LogInformation("Pardus theme event=execution_started plannedCommand={PlannedCommand}", planned);
        var execute = await RunFixedCommand(command!.Value.FileName, command.Value.Arguments, timeoutCts.Token, chunkSink);
        timeline.Add(execute.ExitCode == 0 ? "execution_completed" : "execution_failed");
        if (execute.ExitCode != 0) return NewResult("EXECUTE_FAILED", false, "Theme command failed.", options, planned, execute.Output, desktop, execute.Output, timeline, osInfo);

        timeline.Add("verify_started");
        logger.LogInformation("Pardus theme event=verify_started mode={Mode} desktop={Desktop}", options.Mode, desktop);
        var verify = await VerifyThemeAsync(desktop, options.Mode, timeoutCts.Token);
        timeline.Add(verify.Code == "VERIFY_INCONCLUSIVE" ? "verify_inconclusive" : verify.Success ? "verify_passed" : "verify_failed");
        if (verify.Code == "VERIFY_INCONCLUSIVE") return NewResult("VERIFY_INCONCLUSIVE", false, "VERIFY_INCONCLUSIVE", options, planned, execute.Output, desktop, verify.Details, timeline, osInfo);
        if (!verify.Success) return NewResult("VERIFY_FAILED", false, "VERIFY_FAILED", options, planned, execute.Output, desktop, verify.Details, timeline, osInfo);
        return NewResult("OK", true, $"{options.Mode} theme applied.", options, planned, execute.Output, desktop, verify.Details, timeline, osInfo);
    }

    public static ModeSelection ReadModeOrDefault(object request)
    {
        var property = request.GetType().GetProperty("Mode");
        return NormalizeMode(property?.GetValue(request)?.ToString());
    }

    public static ModeSelection ReadModeOrDefault(JsonElement? arguments)
    {
        if (arguments is { ValueKind: JsonValueKind.Object } element && TryGetPropertyCaseInsensitive(element, "mode", out var mode)) return NormalizeMode(ReadJsonStringValue(mode));
        return NormalizeMode(null);
    }

    public static bool ReadDryRunOrDefault(JsonElement? arguments)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } element || !TryGetPropertyCaseInsensitive(element, "dryRun", out var dryRun)) return false;
        return dryRun.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(dryRun.GetString(), out var parsed) && parsed,
            JsonValueKind.Number => dryRun.TryGetInt32(out var parsed) && parsed != 0,
            _ => false
        };
    }

    public static PardusDesktopEnvironment DetectDesktopEnvironment()
    {
        var current = string.Join(' ', Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"), Environment.GetEnvironmentVariable("DESKTOP_SESSION"), Environment.GetEnvironmentVariable("GDMSESSION")).ToLowerInvariant();
        if (current.Contains("xfce")) return PardusDesktopEnvironment.Xfce;
        if (current.Contains("gnome")) return PardusDesktopEnvironment.Gnome;
        if (current.Contains("kde") || current.Contains("plasma")) return PardusDesktopEnvironment.Kde;
        return PardusDesktopEnvironment.Unknown;
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value)) return true;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? ReadJsonStringValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
        _ => null
    };

    private static ModeSelection NormalizeMode(string? value) => string.IsNullOrWhiteSpace(value) ? new("dark", "dark") : new(value.Trim().ToLowerInvariant(), value);

    private static FixedCommand? BuildThemeCommand(PardusDesktopEnvironment desktop, string mode) => (desktop, mode) switch
    {
        (PardusDesktopEnvironment.Xfce, "dark") => new("xfconf-query", ["-c", "xsettings", "-p", "/Net/ThemeName", "-s", "Adwaita-dark"]),
        (PardusDesktopEnvironment.Xfce, "light") => new("xfconf-query", ["-c", "xsettings", "-p", "/Net/ThemeName", "-s", "Adwaita"]),
        (PardusDesktopEnvironment.Gnome, "dark") => new("gsettings", ["set", "org.gnome.desktop.interface", "color-scheme", "prefer-dark"]),
        (PardusDesktopEnvironment.Gnome, "light") => new("gsettings", ["set", "org.gnome.desktop.interface", "color-scheme", "prefer-light"]),
        (PardusDesktopEnvironment.Kde, "dark") => new("lookandfeeltool", ["-a", "org.kde.breezedark.desktop"]),
        (PardusDesktopEnvironment.Kde, "light") => new("lookandfeeltool", ["-a", "org.kde.breeze.desktop"]),
        _ => null
    };

    private static FixedCommand? BuildVerifyCommand(PardusDesktopEnvironment desktop) => desktop switch
    {
        PardusDesktopEnvironment.Xfce => new("xfconf-query", ["-c", "xsettings", "-p", "/Net/ThemeName"]),
        PardusDesktopEnvironment.Gnome => new("gsettings", ["get", "org.gnome.desktop.interface", "color-scheme"]),
        _ => null
    };

    private static PreflightResult CheckPreflight(PardusDesktopEnvironment desktop, FixedCommand? command, OperatingSystemInfo osInfo)
    {
        var details = new List<string>();
        if (!OperatingSystem.IsLinux()) details.Add($"Unsupported OS: {RuntimeInformation.OSDescription}");
        if (OperatingSystem.IsLinux() && !osInfo.IsPardus) details.Add($"Non-Pardus Linux detected: {osInfo.PrettyName ?? osInfo.Id ?? RuntimeInformation.OSDescription}; continuing because the desktop command is allowlisted.");
        if (desktop == PardusDesktopEnvironment.Unknown) details.Add("Desktop environment was not detected from XDG_CURRENT_DESKTOP/DESKTOP_SESSION/GDMSESSION.");
        if (command is null) details.Add("No allowlisted command is available for the requested mode/desktop.");
        else if (!CommandExists(command.Value.FileName)) details.Add($"Required command not found: {command.Value.FileName}");
        var success = OperatingSystem.IsLinux() && desktop != PardusDesktopEnvironment.Unknown && command is not null && CommandExists(command.Value.FileName);
        return new(success, string.Join(" ", details));
    }

    private static bool CommandExists(string fileName)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return paths.Any(path => File.Exists(Path.Combine(path, fileName)));
    }

    private static async Task<VerifyResult> VerifyThemeAsync(PardusDesktopEnvironment desktop, string mode, CancellationToken ct)
    {
        var command = BuildVerifyCommand(desktop);
        if (command is null && desktop == PardusDesktopEnvironment.Kde) return new(false, "VERIFY_INCONCLUSIVE", "KDE verification is inconclusive: no read command is configured; execution success was not treated as verification success.");
        if (command is null) return new(false, "NO_VERIFY_COMMAND", "No verification command is configured.");
        if (!CommandExists(command.Value.FileName)) return new(false, "VERIFY_COMMAND_MISSING", $"Verify command not found: {command.Value.FileName}");
        var result = await RunFixedCommand(command.Value.FileName, command.Value.Arguments, ct);
        var expected = mode == "dark" ? "dark" : "light";
        var success = result.ExitCode == 0 && result.Output.Contains(expected, StringComparison.OrdinalIgnoreCase);
        return new(success, success ? "CONFIRMED" : "NOT_CONFIRMED", $"verifyExitCode={result.ExitCode}; output={result.Output}");
    }

    private static async Task<CommandResult> RunFixedCommand(string fileName, IReadOnlyList<string> arguments, CancellationToken ct, Func<string, string, Task>? chunkSink = null)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        try
        {
            process.Start();
            var stdoutTask = ReadStreamAsync(process.StandardOutput, "stdout", chunkSink, ct);
            var stderrTask = ReadStreamAsync(process.StandardError, "stderr", chunkSink, ct);
            await process.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new CommandResult(process.ExitCode, SanitizeOutput(stdout + stderr));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return new CommandResult(-1, SanitizeOutput(ex.Message));
        }
    }

    private static async Task<string> ReadStreamAsync(StreamReader reader, string stream, Func<string, string, Task>? chunkSink, CancellationToken ct)
    {
        var buffer = new StringBuilder();
        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            buffer.AppendLine(line);
            if (chunkSink is not null)
            {
                await chunkSink(stream, line);
            }
        }
        return buffer.ToString();
    }

    private static PardusThemeExecutionResult NewResult(string status, bool success, string message, PardusThemeExecutionOptions options, string? planned, string? output, PardusDesktopEnvironment desktop, string? details, IReadOnlyList<string> timeline, OperatingSystemInfo osInfo) =>
        new(status, success, message, planned, output, desktop, options.DryRun, BuildTechnicalDetails(details, osInfo), osInfo, timeline.ToArray());

    private static string BuildTechnicalDetails(string? details, OperatingSystemInfo osInfo)
    {
        var osRelease = string.Join("; ", new[]
        {
            $"osDescription={RuntimeInformation.OSDescription}",
            $"osReleaseId={osInfo.Id ?? "unknown"}",
            $"osReleasePrettyName={osInfo.PrettyName ?? "unknown"}",
            $"isPardus={osInfo.IsPardus}"
        });
        return string.IsNullOrWhiteSpace(details) ? osRelease : $"{details} {osRelease}";
    }

    private static string FormatCommand(FixedCommand command) => string.Join(' ', command.Arguments.Prepend(command.FileName));

    private static string SanitizeOutput(string value)
    {
        var sanitized = new string(value.Where(c => !char.IsControl(c) || c is '\r' or '\n' or '\t').ToArray()).Trim();
        return sanitized.Length <= 2048 ? sanitized : sanitized[..2048];
    }
}

static class LocalAgentPardusWallpaper
{
    private const string XfceBackdropProperty = "/backdrop/screen0/monitorVirtual-1/workspace0/last-image";

    public static async Task<PardusThemeExecutionResult> HandleAsync(PardusWallpaperExecutionOptions options, ILogger logger, CancellationToken ct, Func<string, string, Task>? chunkSink = null)
    {
        var timeline = new List<string> { "preflight_started" };
        var osInfo = OperatingSystemInfo.Detect();
        if (options.Color != "black") return NewResult("INVALID_COLOR", false, "INVALID_COLOR", options, null, null, PardusDesktopEnvironment.Unknown, "Color must be black.", timeline, osInfo);
        var desktop = LocalAgentPardusTheme.DetectDesktopEnvironment();
        var commands = BuildWallpaperCommands(desktop);
        var planned = commands.Count == 0 ? null : string.Join(" && ", commands.Select(FormatCommand));
        var preflight = CheckPreflight(desktop, osInfo);
        timeline.Add(preflight.Success ? "preflight_passed" : "preflight_failed");
        logger.LogInformation("Pardus wallpaper event={Event} color={Color} desktop={Desktop} success={Success} details={Details} distroId={DistroId}", preflight.Success ? "preflight_passed" : "preflight_failed", options.Color, desktop, preflight.Success, preflight.Details, osInfo.Id);
        if (!preflight.Success) return NewResult("PREFLIGHT_FAILED", false, "PREFLIGHT_FAILED", options, planned, null, desktop, preflight.Details, timeline, osInfo);
        if (options.DryRun)
        {
            timeline.Add("dry_run_completed");
            logger.LogInformation("Pardus wallpaper event=dry_run_completed plannedCommand={PlannedCommand}", planned);
            return NewResult("DRY_RUN", true, "Dry-run accepted.", options, planned, null, desktop, "No command executed.", timeline, osInfo);
        }
        if (options.RequireUserApproval)
        {
            timeline.Add("approval_required");
            return NewResult("USER_APPROVAL_REQUIRED", false, "USER_APPROVAL_REQUIRED", options, planned, null, desktop, "User approval is required before execution.", timeline, osInfo);
        }
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(options.Timeout);
        timeline.Add("execution_started");
        var output = new StringBuilder();
        foreach (var command in commands)
        {
            var execute = await RunFixedCommand(command.FileName, command.Arguments, timeoutCts.Token, chunkSink);
            output.AppendLine(execute.Output);
            if (execute.ExitCode != 0)
            {
                timeline.Add("execution_failed");
                return NewResult("EXECUTE_FAILED", false, "Wallpaper command failed.", options, planned, output.ToString(), desktop, execute.Output, timeline, osInfo);
            }
        }
        timeline.Add("execution_completed");
        return NewResult("OK", true, "Black solid wallpaper applied.", options, planned, output.ToString(), desktop, "XFCE backdrop color commands completed.", timeline, osInfo);
    }

    public static ColorSelection ReadColorOrDefault(JsonElement? arguments)
    {
        if (arguments is { ValueKind: JsonValueKind.Object } element && TryGetPropertyCaseInsensitive(element, "color", out var color)) return NormalizeColor(ReadJsonStringValue(color));
        return NormalizeColor(null);
    }

    private static List<FixedCommand> BuildWallpaperCommands(PardusDesktopEnvironment desktop) => desktop == PardusDesktopEnvironment.Xfce
        ?
        [
            new("xfconf-query", ["-c", "xfce4-desktop", "-p", XfceBackdropProperty, "-r"]),
            new("xfconf-query", ["-c", "xfce4-desktop", "-p", "/backdrop/screen0/monitorVirtual-1/workspace0/color-style", "-n", "-t", "int", "-s", "0"]),
            new("xfconf-query", ["-c", "xfce4-desktop", "-p", "/backdrop/screen0/monitorVirtual-1/workspace0/rgba1", "-n", "-t", "double", "-t", "double", "-t", "double", "-t", "double", "-s", "0", "-s", "0", "-s", "0", "-s", "1"])
        ]
        : [];

    private static PreflightResult CheckPreflight(PardusDesktopEnvironment desktop, OperatingSystemInfo osInfo)
    {
        var details = new List<string>();
        if (!OperatingSystem.IsLinux()) details.Add($"Unsupported OS: {RuntimeInformation.OSDescription}");
        if (OperatingSystem.IsLinux() && !osInfo.IsPardus) details.Add($"Non-Pardus Linux detected: {osInfo.PrettyName ?? osInfo.Id ?? RuntimeInformation.OSDescription}; continuing because the XFCE command is allowlisted.");
        if (desktop != PardusDesktopEnvironment.Xfce) details.Add("XFCE desktop environment was not detected from XDG_CURRENT_DESKTOP/DESKTOP_SESSION/GDMSESSION.");
        if (!CommandExists("xfconf-query")) details.Add("Required command not found: xfconf-query");
        return new(OperatingSystem.IsLinux() && desktop == PardusDesktopEnvironment.Xfce && CommandExists("xfconf-query"), string.Join(" ", details));
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value)) return true;
        foreach (var property in element.EnumerateObject()) if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }

    private static string? ReadJsonStringValue(JsonElement value) => value.ValueKind switch { JsonValueKind.String => value.GetString(), JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(), _ => null };
    private static ColorSelection NormalizeColor(string? value) => string.IsNullOrWhiteSpace(value) ? new("", "") : value.Trim().ToLowerInvariant() is "black" or "#000000" ? new("black", value) : new(value.Trim().ToLowerInvariant(), value);
    private static bool CommandExists(string fileName) => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Any(path => File.Exists(Path.Combine(path, fileName)));
    private static string FormatCommand(FixedCommand command) => string.Join(' ', command.Arguments.Prepend(command.FileName));
    private static PardusThemeExecutionResult NewResult(string status, bool success, string message, PardusWallpaperExecutionOptions options, string? planned, string? output, PardusDesktopEnvironment desktop, string? details, IReadOnlyList<string> timeline, OperatingSystemInfo osInfo) =>
        new(status, success, message, planned, output, desktop, options.DryRun, details, osInfo, timeline.ToArray());

    private static async Task<CommandResult> RunFixedCommand(string fileName, IReadOnlyList<string> arguments, CancellationToken ct, Func<string, string, Task>? chunkSink = null)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        try
        {
            process.Start();
            var stdoutTask = ReadStreamAsync(process.StandardOutput, "stdout", chunkSink, ct);
            var stderrTask = ReadStreamAsync(process.StandardError, "stderr", chunkSink, ct);
            await process.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new CommandResult(process.ExitCode, SanitizeOutput(stdout + stderr));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return new CommandResult(-1, SanitizeOutput(ex.Message));
        }
    }

    private static async Task<string> ReadStreamAsync(StreamReader reader, string stream, Func<string, string, Task>? chunkSink, CancellationToken ct)
    {
        var buffer = new StringBuilder();
        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            buffer.AppendLine(line);
            if (chunkSink is not null) await chunkSink(stream, line);
        }
        return buffer.ToString();
    }

    private static string SanitizeOutput(string value)
    {
        var sanitized = new string(value.Where(c => !char.IsControl(c) || c is '\r' or '\n' or '\t').ToArray()).Trim();
        return sanitized.Length <= 2048 ? sanitized : sanitized[..2048];
    }
}

readonly record struct FixedCommand(string FileName, string[] Arguments);
readonly record struct CommandResult(int ExitCode, string Output);
readonly record struct PreflightResult(bool Success, string Details);
readonly record struct VerifyResult(bool Success, string Code, string Details);
readonly record struct ModeSelection(string Value, string RawValue);
sealed record DeviceCredentials(string DeviceId, string DeviceToken);
sealed record DeviceRegisterResponse(string DeviceId, string DeviceToken);
sealed record DeviceJobClaimRequest(string DeviceId, string DeviceToken);
sealed record DeviceJobClaimResponse(DeviceJobDto? Job);
sealed record DeviceJobDto(string JobId, string ToolName, JsonElement? Arguments);
sealed record DeviceJobCompleteRequest(string DeviceId, string DeviceToken, bool Success, string Code, string Message, bool DryRun, string? PlannedCommand, string? Output, string? TechnicalDetails, string[] Timeline);
sealed record DeviceJobResult(bool Success, string Code, string Message, bool DryRun, string? PlannedCommand, string? Output, string? TechnicalDetails, string[] Timeline)
{
    public static DeviceJobResult FromExecution(PardusThemeExecutionResult result) =>
        new(result.Success, result.Status, result.Message, result.DryRun, result.PlannedCommand, result.Output, result.TechnicalDetails, result.Timeline);

    public DeviceJobCompleteRequest ToCompleteRequest(DeviceCredentials credentials) =>
        new(credentials.DeviceId, credentials.DeviceToken, Success, Code, Message, DryRun, PlannedCommand, Output, TechnicalDetails, Timeline);
}
sealed record PardusThemeExecutionOptions(string Mode, bool DryRun, bool RequireUserApproval, TimeSpan Timeout);
sealed record PardusWallpaperExecutionOptions(string Color, bool DryRun, bool RequireUserApproval, TimeSpan Timeout);
readonly record struct ColorSelection(string Value, string RawValue);
sealed record OperatingSystemInfo(string? Id, string? PrettyName, bool IsPardus)
{
    public static OperatingSystemInfo Detect()
    {
        if (!OperatingSystem.IsLinux()) return new(null, RuntimeInformation.OSDescription, false);
        const string osReleasePath = "/etc/os-release";
        try
        {
            if (!File.Exists(osReleasePath)) return new(null, RuntimeInformation.OSDescription, false);
            var values = File.ReadAllLines(osReleasePath)
                .Select(line => line.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1].Trim().Trim('"'), StringComparer.OrdinalIgnoreCase);
            values.TryGetValue("ID", out var id);
            values.TryGetValue("PRETTY_NAME", out var prettyName);
            values.TryGetValue("ID_LIKE", out var idLike);
            var joined = string.Join(' ', id, idLike, prettyName).ToLowerInvariant();
            return new(id, prettyName, joined.Contains("pardus"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(null, RuntimeInformation.OSDescription, false);
        }
    }
}
sealed record PardusThemeExecutionResult(string Status, bool Success, string Message, string? PlannedCommand, string? Output, PardusDesktopEnvironment Desktop, bool DryRun, string? TechnicalDetails, OperatingSystemInfo OperatingSystem, string[] Timeline)
{
    public static PardusThemeExecutionResult InvalidJson(string details) =>
        new("INVALID_JSON", false, "INVALID_JSON", null, null, PardusDesktopEnvironment.Unknown, false, details, OperatingSystemInfo.Detect(), ["invalid_json"]);

    public object ToResponsePayload(string? deviceId, string? deviceToken) => new
    {
        toolName = "pardus_set_theme",
        success = Success,
        code = Status,
        message = Message,
        dryRun = DryRun,
        plannedCommand = PlannedCommand,
        output = Output,
        desktop = Desktop,
        operatingSystem = OperatingSystem,
        technicalDetails = TechnicalDetails,
        timeline = Timeline,
        deviceId,
        deviceToken
    };
}




static class LocalAgentRunCmd
{
    private static readonly HashSet<string> BlockedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "cmd.exe", "powershell", "powershell.exe", "pwsh", "pwsh.exe",
        "bash", "bash.exe", "sh", "sh.exe", "zsh", "zsh.exe", "wscript", "wscript.exe",
        "cscript", "cscript.exe", "mshta", "mshta.exe", "rundll32", "rundll32.exe"
    };

    public static async Task<PardusThemeExecutionResult> HandleAsync(
        string commandText,
        string? workingDirectory,
        bool dryRun,
        TimeSpan timeout,
        ILogger logger,
        CancellationToken ct,
        Func<string, string, Task>? chunkSink = null)
    {
        var timeline = new List<string> { "preflight_started" };
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return Result("INVALID_COMMAND", false, "run_cmd command is empty.", null, null, dryRun, ["preflight_started", "policy_rejected"]);
        }

        if (commandText.IndexOfAny(['\r', '\n', '|', ';', '&', '>', '<', '`']) >= 0)
        {
            return Result("UNSAFE_COMMAND", false, "run_cmd rejects shell metacharacters and multi-line input.", null, null, dryRun, ["preflight_started", "policy_rejected"]);
        }

        if (!TryParseCommand(commandText, out var fileName, out var arguments))
        {
            return Result("INVALID_COMMAND", false, "run_cmd could not parse fileName and arguments.", null, null, dryRun, ["preflight_started", "policy_rejected"]);
        }

        var bareName = Path.GetFileName(fileName);
        if (BlockedFileNames.Contains(bareName))
        {
            return Result("BLOCKED_SHELL", false, $"run_cmd blocks shell interpreters: {bareName}", null, null, dryRun, ["preflight_started", "policy_rejected"]);
        }

        string? safeWorkingDirectory = null;
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            try
            {
                safeWorkingDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(workingDirectory));
                if (!Directory.Exists(safeWorkingDirectory))
                {
                    return Result("INVALID_WORKING_DIRECTORY", false, "run_cmd workingDirectory does not exist.", null, null, dryRun, ["preflight_started", "policy_rejected"]);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Result("INVALID_WORKING_DIRECTORY", false, "run_cmd workingDirectory is invalid.", null, null, dryRun, ["preflight_started", "policy_rejected"]);
            }
        }

        var planned = fileName + (arguments.Count == 0 ? string.Empty : " " + string.Join(' ', arguments));
        timeline.Add("preflight_passed");
        if (dryRun)
        {
            timeline.Add("dry_run_completed");
            return Result("DRY_RUN", true, "Dry-run: run_cmd not executed.", planned, null, true, timeline.ToArray());
        }

        timeline.Add("execution_started");
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = fileName;
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            if (!string.IsNullOrWhiteSpace(safeWorkingDirectory)) process.StartInfo.WorkingDirectory = safeWorkingDirectory;

            process.Start();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var combined = Sanitize(stdout + (string.IsNullOrWhiteSpace(stderr) ? string.Empty : Environment.NewLine + stderr));
            if (chunkSink is not null && !string.IsNullOrWhiteSpace(stdout)) await chunkSink("stdout", Sanitize(stdout));
            if (chunkSink is not null && !string.IsNullOrWhiteSpace(stderr)) await chunkSink("stderr", Sanitize(stderr));
            timeline.Add("execution_completed");
            return Result(process.ExitCode == 0 ? "OK" : "COMMAND_FAILED", process.ExitCode == 0, $"exitCode={process.ExitCode}", planned, combined, false, timeline.ToArray());
        }
        catch (OperationCanceledException)
        {
            timeline.Add("execution_timeout");
            logger.LogWarning("run_cmd timed out or was cancelled: {Command}", planned);
            return Result("TIMEOUT", false, "run_cmd timed out or was cancelled.", planned, null, false, timeline.ToArray());
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            timeline.Add("execution_failed");
            logger.LogWarning(ex, "run_cmd failed to start: {Command}", planned);
            return Result("START_FAILED", false, "run_cmd failed to start process.", planned, Sanitize(ex.Message), false, timeline.ToArray());
        }
    }

    private static bool TryParseCommand(string commandText, out string fileName, out List<string> arguments)
    {
        fileName = string.Empty;
        arguments = [];
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < commandText.Length; i++)
        {
            var ch = commandText[i];
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (inQuotes) return false;
        if (current.Length > 0) tokens.Add(current.ToString());
        if (tokens.Count == 0) return false;
        fileName = tokens[0];
        if (tokens.Count > 1) arguments.AddRange(tokens.Skip(1));
        return !string.IsNullOrWhiteSpace(fileName);
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var trimmed = value.Length > 2048 ? value[..2048] : value;
        return new string(trimmed.Where(c => !char.IsControl(c) || c is '\r' or '\n' or '\t').ToArray());
    }

    private static PardusThemeExecutionResult Result(string status, bool success, string message, string? planned, string? output, bool dryRun, string[] timeline) =>
        new(status, success, message, planned, output, PardusDesktopEnvironment.Unknown, dryRun, "run_cmd_executor", OperatingSystemInfo.Detect(), timeline);
}

static class LocalAgentJarvisTools
{
    private static readonly IReadOnlyDictionary<string, FixedCommand> AppCommands = new Dictionary<string, FixedCommand>(StringComparer.OrdinalIgnoreCase)
    {
        ["notepad"] = OperatingSystem.IsWindows() ? WindowsSystemApp("notepad.exe") : new("xdg-open", [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)]),
        ["hesap makinesi"] = OperatingSystem.IsWindows() ? WindowsSystemApp("calc.exe") : new("gnome-calculator", []),
        ["calculator"] = OperatingSystem.IsWindows() ? WindowsSystemApp("calc.exe") : new("gnome-calculator", []),
        ["paint"] = OperatingSystem.IsWindows() ? WindowsSystemApp("mspaint.exe") : new("xdg-open", [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)]),
        ["mspaint"] = OperatingSystem.IsWindows() ? WindowsSystemApp("mspaint.exe") : new("xdg-open", [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)])
    };

    private static FixedCommand WindowsSystemApp(string fileName)
    {
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return new FixedCommand(Path.Combine(systemDirectory, fileName), []);
    }

    public static string ReadArgument(JsonElement? arguments, string name)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(name, out var property)) return string.Empty;
        return property.ValueKind == JsonValueKind.String ? property.GetString() ?? string.Empty : string.Empty;
    }
    public static Task<PardusThemeExecutionResult> OpenAppAsync(string appName, bool dryRun, CancellationToken ct)
    {
        if (!AppCommands.TryGetValue((appName ?? string.Empty).Trim(), out var command)) return Task.FromResult(Result("UNSUPPORTED_APP", false, "App is not allowlisted.", null, null, dryRun, ["jarvis_app_rejected"]));
        var planned = command.FileName + " " + string.Join(' ', command.Arguments);
        if (dryRun) return Task.FromResult(Result("DRY_RUN", true, "Dry-run: command not executed.", planned, null, true, ["jarvis_app_opened", "dry_run"]));

        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo(command.FileName) { UseShellExecute = false, CreateNoWindow = true } };
            foreach (var arg in command.Arguments) process.StartInfo.ArgumentList.Add(arg);
            if (!process.Start()) return Task.FromResult(Result("START_FAILED", false, "Application process could not start.", planned, null, false, ["jarvis_app_start_failed"]));
            return Task.FromResult(Result("STARTED", true, "Application launch request accepted.", planned, null, false, ["jarvis_app_started"]));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return Task.FromResult(Result("START_FAILED", false, "Application process could not start.", planned, ex.Message.Length <= 2048 ? ex.Message : ex.Message[..2048], false, ["jarvis_app_start_failed"]));
        }
    }

    public static async Task<PardusThemeExecutionResult> OpenFileAsync(string path, bool dryRun, CancellationToken ct)
    {
        if (!TryResolveSafeUserPath(path, requireExisting: true, out var safePath)) return Result("UNSAFE_PATH", false, "File path is not inside an allowed user folder or does not exist.", null, null, dryRun, ["jarvis_file_rejected"]);
        var command = OperatingSystem.IsWindows() ? new FixedCommand("explorer.exe", [safePath]) : new FixedCommand("xdg-open", [safePath]);
        return await RunJarvisCommandAsync(command, dryRun, "jarvis_file_opened", ct);
    }

    public static Task<PardusThemeExecutionResult> CreateFolderAsync(string name, bool dryRun, CancellationToken ct)
    {
        var cleanName = SanitizeName(name);
        if (string.IsNullOrWhiteSpace(cleanName)) return Task.FromResult(Result("INVALID_NAME", false, "Folder name is invalid.", null, null, dryRun, ["jarvis_folder_rejected"]));
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop)) desktop = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var target = Path.Combine(desktop, cleanName);
        if (!TryResolveSafeUserPath(target, requireExisting: false, out var safeTarget)) return Task.FromResult(Result("UNSAFE_PATH", false, "Folder target is unsafe.", null, null, dryRun, ["jarvis_folder_rejected"]));
        if (!dryRun) Directory.CreateDirectory(safeTarget);
        return Task.FromResult(Result("OK", true, "Folder created.", $"mkdir {safeTarget}", safeTarget, dryRun, ["jarvis_folder_created"]));
    }

    public static async Task<PardusThemeExecutionResult> AddNoteAsync(string text, bool dryRun, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return Result("INVALID_NOTE", false, "Note text is empty.", null, null, dryRun, ["jarvis_note_rejected"]);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop)) desktop = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var target = Path.Combine(desktop, "vortex-notlar.txt");
        if (!dryRun) await File.AppendAllTextAsync(target, DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm") + " - " + text.Trim() + Environment.NewLine, ct);
        return Result("OK", true, "Note stored.", $"append {target}", null, dryRun, ["jarvis_note_added"]);
    }

    public static async Task<PardusThemeExecutionResult> LockScreenAsync(bool dryRun, CancellationToken ct)
    {
        var command = OperatingSystem.IsWindows() ? new FixedCommand("rundll32.exe", ["user32.dll,LockWorkStation"]) : new FixedCommand("loginctl", ["lock-session"]);
        return await RunJarvisCommandAsync(command, dryRun, "jarvis_screen_locked", ct);
    }

    public static async Task<PardusThemeExecutionResult> WriteDocumentAsync(string topic, bool dryRun, CancellationToken ct)
    {
        var title = SanitizeName(string.IsNullOrWhiteSpace(topic) ? "vortex-belge" : topic);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop)) desktop = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var target = Path.Combine(desktop, title + ".txt");
        if (!TryResolveSafeUserPath(target, requireExisting: false, out var safeTarget)) return Result("UNSAFE_PATH", false, "Document target is unsafe.", null, null, dryRun, ["jarvis_document_rejected"]);
        if (!dryRun) await File.WriteAllTextAsync(safeTarget, $"# {topic}{Environment.NewLine}{Environment.NewLine}Vortex LocalAgent tarafından oluşturuldu.{Environment.NewLine}", ct);
        return Result("OK", true, "Document written.", $"write {safeTarget}", safeTarget, dryRun, ["jarvis_document_written"]);
    }

    private static async Task<PardusThemeExecutionResult> RunJarvisCommandAsync(FixedCommand command, bool dryRun, string timelineEvent, CancellationToken ct)
    {
        var planned = command.FileName + " " + string.Join(' ', command.Arguments);
        if (dryRun) return Result("DRY_RUN", true, "Dry-run: command not executed.", planned, null, true, [timelineEvent, "dry_run"]);
        using var process = new Process { StartInfo = new ProcessStartInfo(command.FileName) { UseShellExecute = false, CreateNoWindow = true } };
        foreach (var arg in command.Arguments) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        await process.WaitForExitAsync(ct);
        return Result(process.ExitCode == 0 ? "OK" : "COMMAND_FAILED", process.ExitCode == 0, $"exitCode={process.ExitCode}", planned, null, dryRun, [timelineEvent]);
    }

    private static bool TryResolveSafeUserPath(string? path, bool requireExisting, out string safePath)
    {
        safePath = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) return false;
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        if (requireExisting && !File.Exists(full) && !Directory.Exists(full)) return false;
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(Path.GetFullPath).ToArray();
        if (!roots.Any(root => full.Equals(root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return false;
        safePath = full;
        return true;
    }

    private static string SanitizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value.Trim().Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray()).Trim();
    }

    private static PardusThemeExecutionResult Result(string status, bool success, string message, string? planned, string? output, bool dryRun, string[] timeline) =>
        new(status, success, message, planned, output, PardusDesktopEnvironment.Unknown, dryRun, "jarvis_v3_localagent_bridge", OperatingSystemInfo.Detect(), timeline);
}

