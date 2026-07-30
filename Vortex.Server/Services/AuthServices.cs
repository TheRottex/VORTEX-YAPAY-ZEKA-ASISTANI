using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vortex.Contracts;
using Vortex.Server.Public.Data;

namespace Vortex.Server.Public.Services;

public sealed class InvalidCredentialsException : Exception;

public sealed class TokenService(IConfiguration configuration)
{
    private readonly string issuer = configuration["Jwt:Issuer"] ?? "Vortex.Server.Public";
    private readonly string audience = configuration["Jwt:Audience"] ?? "Vortex.Public.Clients";
    private readonly byte[] key = Encoding.UTF8.GetBytes(configuration["Jwt:SigningKey"]
        ?? throw new InvalidOperationException("Jwt:SigningKey must be configured."));

    public string CreateToken(UserProfileDto user, DateTimeOffset expiresAt)
    {
        if (key.Length < 32) throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes.");
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iss = issuer, aud = audience, sub = user.Id, email = user.Email, name = user.DisplayName, role = user.Role, exp = expiresAt.ToUnixTimeSeconds() }));
        var unsigned = $"{header}.{payload}";
        using var hmac = new HMACSHA256(key);
        return $"{unsigned}.{Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(unsigned)))}";
    }

    public ClaimsPrincipal? ValidateToken(string? token)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(token)) return null;
            var parts = token.Split('.');
            if (parts.Length != 3) return null;

            var header = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Base64UrlDecode(parts[0]));
            if (header is null
                || !header.TryGetValue("alg", out var algorithm) || algorithm.ValueKind != JsonValueKind.String || algorithm.GetString() != "HS256"
                || !header.TryGetValue("typ", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "JWT") return null;

            using var hmac = new HMACSHA256(key);
            var expected = Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}")));
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(parts[2]))) return null;

            var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Base64UrlDecode(parts[1]));
            if (payload is null
                || !payload.TryGetValue("exp", out var exp) || exp.ValueKind != JsonValueKind.Number || !exp.TryGetInt64(out var expiration)
                || !payload.TryGetValue("iss", out var tokenIssuer) || tokenIssuer.ValueKind != JsonValueKind.String || tokenIssuer.GetString() != issuer
                || !payload.TryGetValue("aud", out var tokenAudience) || tokenAudience.ValueKind != JsonValueKind.String || tokenAudience.GetString() != audience
                || !payload.TryGetValue("sub", out var subject) || subject.ValueKind != JsonValueKind.String || !Guid.TryParse(subject.GetString(), out var id)
                || DateTimeOffset.FromUnixTimeSeconds(expiration) <= DateTimeOffset.UtcNow) return null;

            return new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, id.ToString()),
                new Claim(ClaimTypes.Email, payload.TryGetValue("email", out var email) && email.ValueKind == JsonValueKind.String ? email.GetString() ?? string.Empty : string.Empty),
                new Claim(ClaimTypes.Name, payload.TryGetValue("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? string.Empty : string.Empty),
                new Claim(ClaimTypes.Role, payload.TryGetValue("role", out var role) && role.ValueKind == JsonValueKind.String ? role.GetString() ?? VortexRoles.User : VortexRoles.User)
            ], "VortexJwt"));
        }
        catch
        {
            return null;
        }
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Base64UrlDecode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
}

public sealed class AuthService(VortexDb db, TokenService tokens)
{
    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        if (request.Password.Length < 8 || string.IsNullOrWhiteSpace(request.DisplayName) || string.IsNullOrWhiteSpace(request.FirstName) || string.IsNullOrWhiteSpace(request.LastName) || !IsValidEmail(request.Email))
        {
            throw new InvalidOperationException("Geçerli e-posta, ad, soyad, görünen ad ve en az 8 karakter parola gereklidir.");
        }

        await using var connection = await db.OpenAsync(ct);
        var planId = await VortexDb.ScalarStringAsync(connection, "SELECT Id FROM SubscriptionPlans WHERE Name = 'free'", ct) ?? throw new InvalidOperationException("Varsayılan plan bulunamadı.");
        var id = Guid.NewGuid();
        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var email = request.Email.Trim().ToLowerInvariant();
        var displayName = request.DisplayName.Trim();
        var hash = HashPassword(request.Password, salt);
        try
        {
            await VortexDb.ExecuteAsync(connection, "INSERT INTO Users (Id, Email, DisplayName, PasswordHash, PasswordSalt, Role, PlanId, FirstName, LastName, CreatedAt) VALUES ($id, $email, $displayName, $hash, $salt, $role, $planId, $firstName, $lastName, $createdAt)", ct,
                ("$id", id.ToString()), ("$email", email), ("$displayName", displayName), ("$hash", hash), ("$salt", salt), ("$role", VortexRoles.User), ("$planId", planId), ("$firstName", request.FirstName.Trim()), ("$lastName", request.LastName.Trim()), ("$createdAt", DateTimeOffset.UtcNow.ToString("O")));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException("Bu e-posta adresi veya görünen ad zaten kullanılıyor.");
        }

        return CreateAuthResponse(await GetProfileAsync(id, ct) ?? throw new InvalidOperationException("Kullanıcı oluşturulamadı."));
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, PasswordHash, PasswordSalt FROM Users WHERE Email = $email";
        command.Parameters.AddWithValue("$email", request.Email.Trim().ToLowerInvariant());
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidCredentialsException();
        var id = Guid.Parse(reader.GetString(0));
        if (!VerifyPassword(request.Password, reader.GetString(2), reader.GetString(1))) throw new InvalidCredentialsException();
        return CreateAuthResponse(await GetProfileAsync(id, ct) ?? throw new InvalidCredentialsException());
    }

    public async Task<UserProfileDto?> GetProfileAsync(Guid userId, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT u.Id, u.Email, u.DisplayName, u.Role, p.DisplayName, p.StorageQuotaBytes, u.StorageUsedBytes, u.FirstName, u.LastName FROM Users u JOIN SubscriptionPlans p ON p.Id = u.PlanId WHERE u.Id = $id";
        command.Parameters.AddWithValue("$id", userId.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new UserProfileDto(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8))
            : null;
    }

    private AuthResponse CreateAuthResponse(UserProfileDto profile)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddHours(12);
        return new AuthResponse(tokens.CreateToken(profile, expiresAt), expiresAt, profile);
    }

    private static bool IsValidEmail(string email)
    {
        try { return new MailAddress(email).Address.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase); }
        catch (FormatException) { return false; }
    }

    private static string HashPassword(string password, string salt) => "pbkdf2-sha256:210000:" + Convert.ToHexString(Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromHexString(salt), 210_000, HashAlgorithmName.SHA256, 32));
    private static bool VerifyPassword(string password, string salt, string expected) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(HashPassword(password, salt)), Encoding.UTF8.GetBytes(expected));
}
