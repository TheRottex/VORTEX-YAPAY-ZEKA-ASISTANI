using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vortex.Contracts;
using Vortex.Server.Public.Data;

namespace Vortex.Server.Public.Services;

public enum DeviceJobCompletionOutcome { Unauthorized, NotFound, Conflict, Completed }

public sealed record DeviceJobCompletionResult(DeviceJobCompletionOutcome Outcome, DeviceJobStatusResponse? Status)
{
    public static readonly DeviceJobCompletionResult Unauthorized = new(DeviceJobCompletionOutcome.Unauthorized, null);
    public static readonly DeviceJobCompletionResult NotFound = new(DeviceJobCompletionOutcome.NotFound, null);
    public static readonly DeviceJobCompletionResult Conflict = new(DeviceJobCompletionOutcome.Conflict, null);
}

public sealed class DeviceJobService(VortexDb db)
{
    private const int MaxTextLength = 2048;
    private static readonly IReadOnlyDictionary<string, (bool RequiresConfirmation, string[] RequiredArguments)> Tools = new Dictionary<string, (bool, string[])>(StringComparer.Ordinal)
    {
        ["read-selected-file"] = (false, ["path"]),
        ["speak-preview"] = (false, ["text"]),
        ["open-program-request"] = (true, []),
        ["jarvis_create_folder"] = (false, ["name"]),
        ["jarvis_add_note"] = (false, ["text"]),
        ["jarvis_write_document"] = (false, ["topic"])
    };

    public async Task<DeviceRegisterResponse> RegisterAsync(Guid userId, DeviceRegisterRequest request, CancellationToken ct)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var id = Guid.NewGuid();
        var name = Sanitize(request.DeviceName, 120) ?? "Vortex LocalAgent";
        await using var connection = await db.OpenAsync(ct);
        await VortexDb.ExecuteAsync(connection, "INSERT INTO LocalAgentDevices (Id, UserId, DeviceName, TokenHash, TokenSalt, CreatedAt) VALUES ($id, $userId, $name, $hash, $salt, $createdAt)", ct,
            ("$id", id.ToString()), ("$userId", userId.ToString()), ("$name", name), ("$hash", VortexDb.HashSecret(token, salt)), ("$salt", salt), ("$createdAt", DateTimeOffset.UtcNow.ToString("O")));
        return new DeviceRegisterResponse(id.ToString(), token);
    }

    public async Task<IReadOnlyList<LocalAgentDeviceDto>> ListForOwnerAsync(Guid userId, CancellationToken ct)
    {
        var devices = new List<LocalAgentDeviceDto>();
        await using var connection = await db.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, DeviceName, CreatedAt, LastSeenAt FROM LocalAgentDevices WHERE UserId = $userId AND RevokedAt IS NULL ORDER BY CreatedAt DESC";
        command.Parameters.AddWithValue("$userId", userId.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) devices.Add(new LocalAgentDeviceDto(Guid.Parse(reader.GetString(0)), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2)), reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3))));
        return devices;
    }

    public static LocalAgentToolPlan Plan(string toolName)
    {
        if (Tools.TryGetValue(toolName, out var policy)) return new LocalAgentToolPlan(toolName, true, policy.RequiresConfirmation);
        return new LocalAgentToolPlan(toolName, false, true);
    }

    public async Task<DeviceJobQueuedResponse?> QueueAsync(Guid userId, QueueLocalAgentToolRequest request, CancellationToken ct)
    {
        if (!Tools.TryGetValue(request.ToolName, out var policy) || request.DeviceId == Guid.Empty || (policy.RequiresConfirmation && !request.UserConfirmed)) return null;
        var arguments = request.Arguments ?? [];
        if (!policy.RequiredArguments.All(key => arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) || arguments.Any(pair => pair.Key.Length > 64 || pair.Value.Length > 4096)) return null;
        await using var connection = await db.OpenAsync(ct);
        if (!await DeviceBelongsToOwnerAsync(connection, userId, request.DeviceId, ct)) return null;
        var id = Guid.NewGuid();
        await VortexDb.ExecuteAsync(connection, "INSERT INTO LocalAgentDeviceJobs (Id, UserId, DeviceId, ToolName, ArgumentsJson, Status, CreatedAt, DryRun) VALUES ($id, $userId, $deviceId, $toolName, $arguments, 'pending', $createdAt, $dryRun)", ct,
            ("$id", id.ToString()), ("$userId", userId.ToString()), ("$deviceId", request.DeviceId.ToString()), ("$toolName", request.ToolName), ("$arguments", JsonSerializer.Serialize(arguments)), ("$createdAt", DateTimeOffset.UtcNow.ToString("O")), ("$dryRun", request.DryRun ? 1 : 0));
        return new DeviceJobQueuedResponse(id.ToString(), "queued", request.ToolName, request.DryRun);
    }

    public async Task<DeviceJobClaimResponse?> ClaimAsync(DeviceJobClaimRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(request.DeviceId, out var deviceId)) return null;
        await using var connection = await db.OpenAsync(ct);
        if (!await VerifyTokenAsync(connection, deviceId, request.DeviceToken, ct)) return null;
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var select = connection.CreateCommand();
        select.Transaction = (SqliteTransaction)transaction;
        select.CommandText = "SELECT Id, ToolName, ArgumentsJson FROM LocalAgentDeviceJobs WHERE DeviceId = $deviceId AND Status = 'pending' ORDER BY CreatedAt LIMIT 1";
        select.Parameters.AddWithValue("$deviceId", deviceId.ToString());
        string id;
        string toolName;
        Dictionary<string, string> arguments;
        await using (var reader = await select.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) { await transaction.CommitAsync(ct); return new DeviceJobClaimResponse(null); }
            id = reader.GetString(0);
            toolName = reader.GetString(1);
            arguments = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(2)) ?? [];
        }
        await using var update = connection.CreateCommand();
        update.Transaction = (SqliteTransaction)transaction;
        update.CommandText = "UPDATE LocalAgentDeviceJobs SET Status = 'claimed', ClaimedAt = $now, ClaimedByDeviceId = $deviceId WHERE Id = $id AND Status = 'pending'";
        update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$deviceId", deviceId.ToString());
        update.Parameters.AddWithValue("$id", id);
        if (await update.ExecuteNonQueryAsync(ct) != 1) { await transaction.RollbackAsync(ct); return new DeviceJobClaimResponse(null); }
        await transaction.CommitAsync(ct);
        return new DeviceJobClaimResponse(new DeviceJobDto(id, toolName, arguments));
    }

    public async Task<DeviceJobCompletionResult> CompleteAsync(Guid jobId, DeviceJobCompleteRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(request.DeviceId, out var deviceId)) return DeviceJobCompletionResult.Unauthorized;
        await using var connection = await db.OpenAsync(ct);
        if (!await VerifyTokenAsync(connection, deviceId, request.DeviceToken, ct)) return DeviceJobCompletionResult.Unauthorized;

        var existing = await GetByDeviceAsync(connection, jobId, deviceId, ct);
        if (existing is null || existing.Status == "pending") return DeviceJobCompletionResult.NotFound;
        if (existing.Status == "completed") return new DeviceJobCompletionResult(DeviceJobCompletionOutcome.Completed, existing);
        if (existing.Status != "claimed") return DeviceJobCompletionResult.Conflict;

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE LocalAgentDeviceJobs SET Status = 'completed', CompletedAt = $now, Success = $success, ResultCode = $code, ResultMessage = $message, TimelineJson = $timeline WHERE Id = $id AND DeviceId = $deviceId AND ClaimedByDeviceId = $deviceId AND Status = 'claimed'";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$success", request.Success ? 1 : 0);
        command.Parameters.AddWithValue("$code", (object?)Sanitize(request.Code, 80) ?? DBNull.Value);
        command.Parameters.AddWithValue("$message", (object?)Sanitize(request.Message, MaxTextLength) ?? DBNull.Value);
        command.Parameters.AddWithValue("$timeline", JsonSerializer.Serialize((request.Timeline ?? []).Select(item => Sanitize(item, 120)).Where(item => item is not null)));
        command.Parameters.AddWithValue("$id", jobId.ToString());
        command.Parameters.AddWithValue("$deviceId", deviceId.ToString());
        if (await command.ExecuteNonQueryAsync(ct) != 1) return DeviceJobCompletionResult.Conflict;
        return new DeviceJobCompletionResult(DeviceJobCompletionOutcome.Completed, await GetByDeviceAsync(connection, jobId, deviceId, ct));
    }

    public async Task<DeviceJobStatusResponse?> GetForOwnerAsync(Guid userId, Guid jobId, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        return await GetAsync(connection, "Id = $id AND UserId = $userId", ct, ("$id", jobId.ToString()), ("$userId", userId.ToString()));
    }

    private static async Task<bool> DeviceBelongsToOwnerAsync(SqliteConnection connection, Guid userId, Guid deviceId, CancellationToken ct) => await VortexDb.ScalarLongAsync(connection, "SELECT COUNT(*) FROM LocalAgentDevices WHERE Id = $deviceId AND UserId = $userId AND RevokedAt IS NULL", ct, ("$deviceId", deviceId.ToString()), ("$userId", userId.ToString())) == 1;

    private static async Task<bool> VerifyTokenAsync(SqliteConnection connection, Guid deviceId, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT TokenHash, TokenSalt FROM LocalAgentDevices WHERE Id = $id AND RevokedAt IS NULL";
        command.Parameters.AddWithValue("$id", deviceId.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return false;
        var actual = VortexDb.HashSecret(token, reader.GetString(1));
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(reader.GetString(0)));
    }

    private static Task<DeviceJobStatusResponse?> GetByDeviceAsync(SqliteConnection connection, Guid id, Guid deviceId, CancellationToken ct) => GetAsync(connection, "Id = $id AND DeviceId = $deviceId", ct, ("$id", id.ToString()), ("$deviceId", deviceId.ToString()));

    private static async Task<DeviceJobStatusResponse?> GetAsync(SqliteConnection connection, string where, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT Id, Status, ToolName, ResultCode, ResultMessage, Success, DryRun, TimelineJson FROM LocalAgentDeviceJobs WHERE {where}";
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new DeviceJobStatusResponse(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetInt32(5) == 1, reader.GetInt32(6) == 1, reader.IsDBNull(7) ? [] : JsonSerializer.Deserialize<string[]>(reader.GetString(7)) ?? []) : null;
    }

    private static string? Sanitize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var result = new string(value.Where(c => !char.IsControl(c) || c is '\r' or '\n' or '\t').ToArray()).Trim();
        return result.Length <= maxLength ? result : result[..maxLength];
    }
}
