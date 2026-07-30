using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Vortex.Contracts;

namespace Vortex.Public.Tests;

public sealed class RateLimitingEndpointTests
{
    [Theory]
    [InlineData("/api/auth/register")]
    [InlineData("/api/auth/login")]
    [InlineData("/api/devices/register")]
    [InlineData("/api/device-jobs/claim")]
    [InlineData("/api/device-jobs/00000000-0000-0000-0000-000000000001/complete")]
    public async Task ProtectedEndpoints_ReturnGeneric429AfterTenRequests(string path)
    {
        using var factory = new PublicServerFactory();
        using var client = factory.CreateClient();

        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var response = await PostAsync(client, path, attempt);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        using var limited = await PostAsync(client, path, 10);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(string.IsNullOrEmpty(await limited.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task ActionQueue_AcceptsTenValidQueuesThenReturnsGeneric429WithoutAcceptingAnEleventh()
    {
        using var factory = new PublicServerFactory();
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client);
        var device = await RegisterDeviceAsync(client, auth.AccessToken);

        var acceptedJobIds = new HashSet<string>(StringComparer.Ordinal);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var response = await QueueAsync(client, auth.AccessToken, device.DeviceId, attempt);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var queued = await response.Content.ReadFromJsonAsync<DeviceJobQueuedResponse>();
            Assert.NotNull(queued);
            Assert.True(acceptedJobIds.Add(queued.JobId));
        }

        using var limited = await QueueAsync(client, auth.AccessToken, device.DeviceId, 10);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(string.IsNullOrEmpty(await limited.Content.ReadAsStringAsync()));
        Assert.Equal(10, acceptedJobIds.Count);

        foreach (var jobId in acceptedJobIds)
        {
            using var statusRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/device-jobs/{jobId}");
            statusRequest.Headers.Authorization = new("Bearer", auth.AccessToken);
            using var statusResponse = await client.SendAsync(statusRequest);
            statusResponse.EnsureSuccessStatusCode();
            var status = await statusResponse.Content.ReadFromJsonAsync<DeviceJobStatusResponse>();
            Assert.Equal(jobId, status!.JobId);
        }
    }

    [Fact]
    public async Task AuthAndDevicePolicies_AreIsolated_AndForwardedForCannotBypass()
    {
        using var factory = new PublicServerFactory();
        using var client = factory.CreateClient();

        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var response = await PostAsync(client, "/api/auth/login", attempt, $"198.51.100.{attempt}");
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        using var authLimited = await PostAsync(client, "/api/auth/login", 10, "203.0.113.10");
        Assert.Equal(HttpStatusCode.TooManyRequests, authLimited.StatusCode);

        using var deviceResponse = await PostAsync(client, "/api/device-jobs/claim", 0);
        Assert.Equal(HttpStatusCode.Unauthorized, deviceResponse.StatusCode);
    }

    private static async Task<AuthResponse> RegisterAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest($"queue-rate-{Guid.NewGuid():N}@example.test", "correct-horse-battery-staple", "Queue Rate", "Queue", "Rate"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<DeviceRegisterResponse> RegisterDeviceAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/devices/register") { Content = JsonContent.Create(new DeviceRegisterRequest("queue rate-limit device")) };
        request.Headers.Authorization = new("Bearer", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DeviceRegisterResponse>())!;
    }

    private static Task<HttpResponseMessage> QueueAsync(HttpClient client, string token, string deviceId, int attempt)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/local-agent/actions/queue")
        {
            Content = JsonContent.Create(new QueueLocalAgentToolRequest(Guid.Parse(deviceId), "jarvis_add_note", new Dictionary<string, string> { ["text"] = $"rate-limit-{attempt}" }))
        };
        request.Headers.Authorization = new("Bearer", token);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, int attempt, string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = path switch
            {
                "/api/auth/register" => JsonContent.Create(new RegisterRequest($"rate-{attempt}@example.test", "correct-horse-battery-staple", "Rate", "Test", "User")),
                "/api/auth/login" => JsonContent.Create(new LoginRequest("rate@example.test", "correct-horse-battery-staple")),
                "/api/devices/register" => JsonContent.Create(new DeviceRegisterRequest("rate-limit-test")),
                "/api/device-jobs/claim" => JsonContent.Create(new DeviceJobClaimRequest(Guid.NewGuid().ToString(), "invalid")),
                _ => JsonContent.Create(new DeviceJobCompleteRequest(Guid.NewGuid().ToString(), "invalid", false, "INVALID", "invalid", []))
            }
        };
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        return client.SendAsync(request);
    }

    private sealed class PublicServerFactory : WebApplicationFactory<Program>
    {
        private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), $"vortex-rate-limit-{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseSetting(WebHostDefaults.EnvironmentKey, "Testing")
            .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vortex:DataDirectory"] = dataDirectory,
                ["Jwt:Issuer"] = "test-issuer",
                ["Jwt:Audience"] = "test-audience",
                ["Jwt:SigningKey"] = new string('k', 32)
            }));

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
        }
    }
}
