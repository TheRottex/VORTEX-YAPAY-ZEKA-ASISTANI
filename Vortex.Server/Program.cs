using System.Security.Claims;
using System.Threading.RateLimiting;
using Vortex.Contracts;
using Vortex.Server.Public.Data;
using Vortex.Server.Public.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<VortexDb>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<DeviceJobService>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    foreach (var policyName in new[] { "register", "login", "device-register", "device-claim", "device-complete" })
    {
        options.AddPolicy(policyName, context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    }
    options.AddPolicy("action-queue", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
});

var app = builder.Build();
app.UseRateLimiter();
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<VortexDb>().InitializeAsync();
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapPost("/api/auth/register", async (RegisterRequest request, AuthService auth, CancellationToken ct) =>
{
    try { return Results.Ok(await auth.RegisterAsync(request, ct)); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireRateLimiting("register");

app.MapPost("/api/auth/login", async (LoginRequest request, AuthService auth, CancellationToken ct) =>
{
    try { return Results.Ok(await auth.LoginAsync(request, ct)); }
    catch (InvalidCredentialsException) { return Results.Unauthorized(); }
}).RequireRateLimiting("login");

app.MapGet("/api/me", async (HttpRequest request, TokenService tokens, AuthService auth, CancellationToken ct) =>
{
    var userId = GetAuthenticatedUserId(request, tokens);
    if (userId is null) return Results.Unauthorized();
    var profile = await auth.GetProfileAsync(userId.Value, ct);
    return profile is null ? Results.Unauthorized() : Results.Ok(profile);
});

app.MapPost("/api/devices/register", async (HttpRequest request, DeviceRegisterRequest registration, TokenService tokens, DeviceJobService jobs, CancellationToken ct) =>
{
    var userId = GetAuthenticatedUserId(request, tokens);
    return userId is null ? Results.Unauthorized() : Results.Ok(await jobs.RegisterAsync(userId.Value, registration, ct));
}).RequireRateLimiting("device-register");

app.MapGet("/api/devices", async (HttpRequest request, TokenService tokens, DeviceJobService jobs, CancellationToken ct) =>
{
    var userId = GetAuthenticatedUserId(request, tokens);
    return userId is null ? Results.Unauthorized() : Results.Ok(await jobs.ListForOwnerAsync(userId.Value, ct));
});

app.MapPost("/api/local-agent/actions/plan", (HttpRequest request, LocalAgentActionPlanRequest actionRequest, TokenService tokens) =>
{
    return GetAuthenticatedUserId(request, tokens) is null ? Results.Unauthorized() : Results.Ok(DeviceJobService.Plan(actionRequest.ToolName));
});

app.MapPost("/api/local-agent/actions/queue", async (HttpRequest request, QueueLocalAgentToolRequest queueRequest, TokenService tokens, DeviceJobService jobs, CancellationToken ct) =>
{
    var userId = GetAuthenticatedUserId(request, tokens);
    if (userId is null) return Results.Unauthorized();
    var queued = await jobs.QueueAsync(userId.Value, queueRequest, ct);
    return queued is null ? Results.BadRequest(new { error = "Invalid action, device, arguments, or confirmation." }) : Results.Ok(queued);
}).RequireRateLimiting("action-queue");

app.MapPost("/api/device-jobs/claim", async (DeviceJobClaimRequest claimRequest, DeviceJobService jobs, CancellationToken ct) =>
{
    var claim = await jobs.ClaimAsync(claimRequest, ct);
    return claim is null ? Results.Unauthorized() : Results.Ok(claim);
}).RequireRateLimiting("device-claim");

app.MapPost("/api/device-jobs/{jobId:guid}/complete", async (Guid jobId, DeviceJobCompleteRequest completion, DeviceJobService jobs, CancellationToken ct) =>
{
    var result = await jobs.CompleteAsync(jobId, completion, ct);
    return result.Outcome switch
    {
        DeviceJobCompletionOutcome.Unauthorized => Results.Unauthorized(),
        DeviceJobCompletionOutcome.NotFound => Results.NotFound(),
        DeviceJobCompletionOutcome.Conflict => Results.Conflict(),
        DeviceJobCompletionOutcome.Completed => Results.Ok(result.Status),
        _ => Results.Conflict()
    };
}).RequireRateLimiting("device-complete");

app.MapGet("/api/device-jobs/{jobId:guid}", async (Guid jobId, HttpRequest request, TokenService tokens, DeviceJobService jobs, CancellationToken ct) =>
{
    var userId = GetAuthenticatedUserId(request, tokens);
    if (userId is null) return Results.Unauthorized();
    var status = await jobs.GetForOwnerAsync(userId.Value, jobId, ct);
    return status is null ? Results.NotFound() : Results.Ok(status);
});

app.Run();

static Guid? GetAuthenticatedUserId(HttpRequest request, TokenService tokens)
{
    var authorization = request.Headers.Authorization.ToString();
    if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
    var principal = tokens.ValidateToken(authorization[7..].Trim());
    return Guid.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ? userId : null;
}

public sealed record LocalAgentActionPlanRequest(string ToolName);
public partial class Program { }
