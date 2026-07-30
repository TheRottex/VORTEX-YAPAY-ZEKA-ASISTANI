using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Vortex.Contracts;
using Vortex.Server.Public.Data;
using Vortex.Server.Public.Services;

namespace Vortex.Public.Tests;

public sealed class TokenServiceTests
{
    [Theory]
    [InlineData("HS512", "JWT", true)]
    [InlineData("HS256", "JWS", true)]
    [InlineData("HS256", "JWT", false)]
    public void ValidateToken_RejectsInvalidHeaderOrMissingRequiredClaim(string algorithm, string type, bool includeSubject)
    {
        var tokens = CreateTokenService();
        var payload = new Dictionary<string, object>
        {
            ["iss"] = "test-issuer",
            ["aud"] = "test-audience",
            ["exp"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()
        };
        if (includeSubject) payload["sub"] = Guid.NewGuid().ToString();

        var token = CreateToken(algorithm, type, payload);

        Assert.Null(tokens.ValidateToken(token));
    }

    private static TokenService CreateTokenService() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Jwt:Issuer"] = "test-issuer",
        ["Jwt:Audience"] = "test-audience",
        ["Jwt:SigningKey"] = new string('k', 32)
    }).Build());

    private static string CreateToken(string algorithm, string type, Dictionary<string, object> payload)
    {
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = algorithm, typ = type }));
        var body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        var unsigned = $"{header}.{body}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(new string('k', 32)));
        return $"{unsigned}.{Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(unsigned)))}";
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class ServerSecretProtectorTests
{
    [Fact]
    public void ProtectAndUnprotect_RoundTripsSecret() => Assert.Equal("secret", ServerSecretProtector.Unprotect(ServerSecretProtector.Protect("secret", "key"), "key"));

    [Fact]
    public void Protect_EmptySecretReturnsEmpty() => Assert.Equal(string.Empty, ServerSecretProtector.Protect("", "key"));

    [Theory]
    [InlineData("vortex-secret:v1:not-base64")]
    [InlineData("vortex-secret:v1:AA==")]
    public void Unprotect_MalformedPayloadThrows(string value) => Assert.ThrowsAny<Exception>(() => ServerSecretProtector.Unprotect(value, "key"));

    [Fact]
    public void Unprotect_WrongKeyThrows() => Assert.ThrowsAny<CryptographicException>(() => ServerSecretProtector.Unprotect(ServerSecretProtector.Protect("secret", "key"), "wrong-key"));

    [Fact]
    public void Protect_MissingKeyThrows() => Assert.Throws<InvalidOperationException>(() => ServerSecretProtector.Protect("secret", ""));
}

public sealed class DeviceJobPolicyTests
{
    [Fact]
    public async Task QueueAsync_RequiresConfirmationForConfirmationTool()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Vortex:DataDirectory"] = root }).Build();
            var environment = new TestEnvironment(root);
            var db = new VortexDb(configuration, environment);
            await db.InitializeAsync();
            var jobs = new DeviceJobService(db);

            var result = await jobs.QueueAsync(Guid.NewGuid(), new QueueLocalAgentToolRequest(Guid.NewGuid(), "open-program-request", UserConfirmed: false), CancellationToken.None);

            Assert.Null(result);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Plan_UnknownToolIsNotPreparedAndRequiresConfirmation()
    {
        var plan = DeviceJobService.Plan("unknown-tool");

        Assert.False(plan.UsesPreparedTool);
        Assert.True(plan.RequiresUserConfirmation);
    }

    private sealed class TestEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Vortex.Public.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string WebRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
