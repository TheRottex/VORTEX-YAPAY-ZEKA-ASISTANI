using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vortex.Contracts;
using Vortex.Server.Public.Data;

namespace Vortex.Server.Public.Services;

public sealed class HermesWorkerService(VortexDb db, IConfiguration configuration)
{
    private const int MaxNonceLength = 128;
    private const int MaxMessageLength = 512;
    private const int MaxResultLength = 65536;
    private static readonly TimeSpan TimestampSkew = TimeSpan.FromMinutes(5);

    public async Task<string?> AuthenticateAsync(HttpRequest request, byte[] body, CancellationToken ct)
    {
        var workerId = request.Headers["X-Vortex-Worker-Id"].ToString();
        var timestamp = request.Headers["X-Vortex-Timestamp"].ToString();
        var nonce = request.Headers["X-Vortex-Nonce"].ToString();
        var signature = request.Headers["X-Vortex-Signature"].ToString();
        var allowedWorkerId = configuration["Worker:AllowedWorkerId"];
        var serviceToken = configuration["Worker:ServiceToken"];

        if (string.IsNullOrWhiteSpace(allowedWorkerId) || string.IsNullOrWhiteSpace(serviceToken)
            || !string.Equals(workerId, allowedWorkerId, StringComparison.Ordinal)
            || nonce.Length is 0 or > MaxNonceLength || signature.Length is 0 or > 128
            || !DateTimeOffset.TryParse(timestamp, out var parsed) || (DateTimeOffset.UtcNow - parsed).Duration() > TimestampSkew)
        {
            return null;
        }

        var target = "/" + request.Path.Value!.TrimStart('/') + request.QueryString;
        var canonical = SigningCanonical.Create(request.Method, target, timestamp, nonce, SigningCanonical.Hash(body));
        var expected = SigningCanonical.Sign(canonical, serviceToken);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signature))) return null;

        await using var connection = await db.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO HermesWorkerNonces (WorkerId, Nonce, ExpiresAt) VALUES ($workerId, $nonce, $expiresAt)";
        command.Parameters.AddWithValue("$workerId", workerId);
        command.Parameters.AddWithValue("$nonce", nonce);
        command.Parameters.AddWithValue("$expiresAt", DateTimeOffset.UtcNow.Add(TimestampSkew).ToString("O"));
        if (await command.ExecuteNonQueryAsync(ct) != 1) return null;

        await VortexDb.ExecuteAsync(connection, "DELETE FROM HermesWorkerNonces WHERE ExpiresAt <= $now", ct, ("$now", DateTimeOffset.UtcNow.ToString("O")));
        return workerId;
    }

    public async Task<WorkerReadinessDto> HeartbeatAsync(string workerId, WorkerHeartbeatRequest request, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        await VortexDb.ExecuteAsync(connection, "INSERT INTO HermesWorkerHeartbeats (WorkerId, LastSeenAt, HermesReady, ModelReady, StorageHealthy, Message) VALUES ($workerId,$now,$hermes,$model,$storage,$message) ON CONFLICT(WorkerId) DO UPDATE SET LastSeenAt=$now, HermesReady=$hermes, ModelReady=$model, StorageHealthy=$storage, Message=$message", ct,
            ("$workerId", workerId), ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$hermes", request.HermesReady ? 1 : 0), ("$model", request.ModelReady ? 1 : 0), ("$storage", request.StorageHealthy ? 1 : 0), ("$message", Sanitize(request.Message, MaxMessageLength)));
        var state = request.HermesReady && request.ModelReady && request.StorageHealthy ? WorkerReadinessState.Connected : WorkerReadinessState.NotConfigured;
        return new WorkerReadinessDto(workerId, true, request.HermesReady, request.ModelReady, request.StorageHealthy, state, DateTimeOffset.UtcNow);
    }

    public async Task<WorkerJobLeaseDto?> ClaimAsync(string workerId, WorkerClaimRequest request, CancellationToken ct)
    {
        var leaseSeconds = Math.Clamp(request.LeaseSeconds, 15, 300);
        var now = DateTimeOffset.UtcNow;
        var leaseExpires = now.AddSeconds(leaseSeconds);
        await using var connection = await db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await VortexDb.ExecuteAsync(connection, "UPDATE HermesWorkerJobs SET Status='Queued', ClaimedByWorkerId=NULL, ClaimedAt=NULL, LeaseExpiresAt=NULL WHERE Status IN ('Claimed','Running') AND LeaseExpiresAt <= $now AND AttemptCount < MaxAttempts", ct, ("$now", now.ToString("O")));
        await using var select = connection.CreateCommand();
        select.Transaction = (SqliteTransaction)transaction;
        select.CommandText = "SELECT Id, UserId, AgentProfileId, ConversationId, RequestId, WorkspaceId, HermesProfileName, Input, Priority, AttemptCount, MaxAttempts, MaxRunSeconds, FileAccessScope, StorageQuotaBytes, StorageUsedBytes, IsSubAgentEnabled, IsTerminalEnabled, IsSystemCommandEnabled, RuntimeMemoryLimitMb, RuntimeIdleReleaseSeconds, StoragePrefix FROM HermesWorkerJobs WHERE Status='Queued' AND AttemptCount < MaxAttempts ORDER BY CreatedAt LIMIT 1";
        await using var reader = await select.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) { await transaction.CommitAsync(ct); return null; }
        var jobId = Guid.Parse(reader.GetString(0));
        var job = new WorkerJobLeaseDto(jobId, Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)), reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), (AgentJobPriority)reader.GetInt32(8), leaseExpires, reader.GetInt32(9) + 1, reader.GetInt32(10), reader.GetInt32(11), reader.GetString(12), reader.GetInt64(13), reader.GetInt64(14), reader.GetInt32(15) == 1, reader.GetInt32(16) == 1, reader.GetInt32(17) == 1, reader.GetInt32(18), reader.GetInt32(19), reader.IsDBNull(20) ? null : reader.GetString(20));
        await reader.DisposeAsync();
        await using var update = connection.CreateCommand();
        update.Transaction = (SqliteTransaction)transaction;
        update.CommandText = "UPDATE HermesWorkerJobs SET Status='Claimed', ClaimedByWorkerId=$worker, ClaimedAt=$now, LeaseExpiresAt=$lease, AttemptCount=AttemptCount+1 WHERE Id=$id AND Status='Queued'";
        update.Parameters.AddWithValue("$worker", workerId); update.Parameters.AddWithValue("$now", now.ToString("O")); update.Parameters.AddWithValue("$lease", leaseExpires.ToString("O")); update.Parameters.AddWithValue("$id", jobId.ToString());
        if (await update.ExecuteNonQueryAsync(ct) != 1) { await transaction.RollbackAsync(ct); return null; }
        await transaction.CommitAsync(ct);
        return job;
    }

    public async Task<bool> RenewLeaseAsync(string workerId, Guid jobId, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE HermesWorkerJobs SET Status='Running', LeaseExpiresAt=$lease WHERE Id=$id AND ClaimedByWorkerId=$worker AND Status IN ('Claimed','Running') AND LeaseExpiresAt > $now";
        command.Parameters.AddWithValue("$lease", DateTimeOffset.UtcNow.AddSeconds(60).ToString("O")); command.Parameters.AddWithValue("$id", jobId.ToString()); command.Parameters.AddWithValue("$worker", workerId); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<WorkerCompletionOutcome> CompleteAsync(string workerId, Guid jobId, WorkerCompleteJobRequest request, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Status, ClaimedByWorkerId FROM HermesWorkerJobs WHERE Id=$id"; command.Parameters.AddWithValue("$id", jobId.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return WorkerCompletionOutcome.NotFound;
        var status = reader.GetString(0); var claimedBy = reader.IsDBNull(1) ? null : reader.GetString(1);
        if (status is "Completed" or "Failed") return string.Equals(claimedBy, workerId, StringComparison.Ordinal) ? WorkerCompletionOutcome.Completed : WorkerCompletionOutcome.Conflict;
        if (!string.Equals(claimedBy, workerId, StringComparison.Ordinal)) return WorkerCompletionOutcome.Conflict;
        await reader.DisposeAsync();
        await VortexDb.ExecuteAsync(connection, "UPDATE HermesWorkerJobs SET Status=$status, CompletedAt=$now, Result=$result, ErrorCode=$error WHERE Id=$id AND ClaimedByWorkerId=$worker AND Status IN ('Claimed','Running')", ct,
            ("$status", request.Succeeded ? "Completed" : "Failed"), ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$result", Sanitize(request.Result, MaxResultLength)), ("$error", Sanitize(request.ErrorCode, 128)), ("$id", jobId.ToString()), ("$worker", workerId));
        return WorkerCompletionOutcome.Completed;
    }

    private static string? Sanitize(string? value, int maxLength) => string.IsNullOrWhiteSpace(value) ? null : value.Length <= maxLength ? value : value[..maxLength];
}

public enum WorkerCompletionOutcome { NotFound, Conflict, Completed }
