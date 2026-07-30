using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vortex.Contracts;
using Vortex.Server.Public.Data;

namespace Vortex.Public.Tests;

public sealed class DeviceJobCompletionEndpointTests : IClassFixture<DeviceJobCompletionEndpointTests.PublicServerFactory>
{
    private readonly PublicServerFactory factory;

    public DeviceJobCompletionEndpointTests(PublicServerFactory factory) => this.factory = factory;

    [Fact]
    public async Task Complete_MapsCredentialJobAndStateOutcomesAndPreservesFirstCompletion()
    {
        using var client = factory.CreateClient();
        var owner = await RegisterAsync(client, "owner");
        var otherOwner = await RegisterAsync(client, "other");
        var device = await RegisterDeviceAsync(client, owner.AccessToken);
        var otherDevice = await RegisterDeviceAsync(client, otherOwner.AccessToken);
        var completion = new DeviceJobCompleteRequest(device.DeviceId, device.DeviceToken, true, "FIRST", "first completion", ["completed"]);

        var malformed = await client.PostAsJsonAsync($"/api/device-jobs/{Guid.NewGuid()}/complete", completion with { DeviceId = "not-a-guid" });
        Assert.Equal(HttpStatusCode.Unauthorized, malformed.StatusCode);

        var unknownCredential = await client.PostAsJsonAsync($"/api/device-jobs/{Guid.NewGuid()}/complete", completion with { DeviceToken = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, unknownCredential.StatusCode);

        var unknownJob = await client.PostAsJsonAsync($"/api/device-jobs/{Guid.NewGuid()}/complete", completion);
        Assert.Equal(HttpStatusCode.NotFound, unknownJob.StatusCode);

        var pendingJob = await QueueAsync(client, owner.AccessToken, device.DeviceId, dryRun: true);
        var pending = await client.PostAsJsonAsync($"/api/device-jobs/{pendingJob}/complete", completion);
        Assert.Equal(HttpStatusCode.NotFound, pending.StatusCode);

        var claimedJob = pendingJob;
        var claim = await client.PostAsJsonAsync("/api/device-jobs/claim", new DeviceJobClaimRequest(device.DeviceId, device.DeviceToken));
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        var claimed = await claim.Content.ReadFromJsonAsync<DeviceJobClaimResponse>();
        Assert.Equal(claimedJob, claimed!.Job!.JobId);

        var wrongDevice = await client.PostAsJsonAsync($"/api/device-jobs/{claimedJob}/complete", completion with { DeviceId = otherDevice.DeviceId, DeviceToken = otherDevice.DeviceToken });
        Assert.Equal(HttpStatusCode.NotFound, wrongDevice.StatusCode);

        var completed = await client.PostAsJsonAsync($"/api/device-jobs/{claimedJob}/complete", completion);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var firstResult = await completed.Content.ReadFromJsonAsync<DeviceJobStatusResponse>();
        Assert.Equal("completed", firstResult!.Status);
        Assert.Equal("FIRST", firstResult.Code);
        Assert.True(firstResult.Success);
        Assert.True(firstResult.DryRun);

        var retry = await client.PostAsJsonAsync($"/api/device-jobs/{claimedJob}/complete", completion with { Success = false, Code = "SECOND", Message = "overwrite attempt", Timeline = ["overwritten"] });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var retryResult = await retry.Content.ReadFromJsonAsync<DeviceJobStatusResponse>();
        Assert.Equal(firstResult.JobId, retryResult!.JobId);
        Assert.Equal(firstResult.Status, retryResult.Status);
        Assert.Equal(firstResult.Code, retryResult.Code);
        Assert.Equal(firstResult.Message, retryResult.Message);
        Assert.Equal(firstResult.Success, retryResult.Success);
        Assert.Equal(firstResult.DryRun, retryResult.DryRun);
        Assert.Equal(firstResult.Timeline, retryResult.Timeline);

        var conflictingJob = await QueueAsync(client, owner.AccessToken, device.DeviceId);
        var conflictClaim = await client.PostAsJsonAsync("/api/device-jobs/claim", new DeviceJobClaimRequest(device.DeviceId, device.DeviceToken));
        Assert.Equal(HttpStatusCode.OK, conflictClaim.StatusCode);
        await SetStatusAsync(conflictingJob, "failed");
        var conflict = await client.PostAsJsonAsync($"/api/device-jobs/{conflictingJob}/complete", completion);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    private static async Task<AuthResponse> RegisterAsync(HttpClient client, string prefix)
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest($"{prefix}-{Guid.NewGuid():N}@example.test", "correct-horse-battery-staple", prefix, "Test", "User"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<DeviceRegisterResponse> RegisterDeviceAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/devices/register") { Content = JsonContent.Create(new DeviceRegisterRequest("test device")) };
        request.Headers.Authorization = new("Bearer", token);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DeviceRegisterResponse>())!;
    }

    private static async Task<string> QueueAsync(HttpClient client, string token, string deviceId, bool dryRun = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local-agent/actions/queue")
        {
            Content = JsonContent.Create(new QueueLocalAgentToolRequest(Guid.Parse(deviceId), "jarvis_add_note", new Dictionary<string, string> { ["text"] = "test" }, DryRun: dryRun))
        };
        request.Headers.Authorization = new("Bearer", token);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DeviceJobQueuedResponse>())!.JobId;
    }

    private async Task SetStatusAsync(string jobId, string status)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VortexDb>();
        await using var connection = await db.OpenAsync();
        await VortexDb.ExecuteAsync(connection, "UPDATE LocalAgentDeviceJobs SET Status = $status WHERE Id = $id", CancellationToken.None, ("$status", status), ("$id", jobId));
    }

    public sealed class PublicServerFactory : WebApplicationFactory<Program>
    {
        private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), $"vortex-public-{Guid.NewGuid():N}");

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
