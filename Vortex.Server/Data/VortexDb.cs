using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Vortex.Server.Public.Data;

public sealed class VortexDb(IConfiguration configuration, IWebHostEnvironment environment)
{
    private readonly string connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(
            configuration["Vortex:DataDirectory"] ?? Path.Combine(environment.ContentRootPath, "App_Data"),
            "vortex-public.db"),
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    }.ToString();

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(new SqliteConnectionStringBuilder(connectionString).DataSource)!;
        Directory.CreateDirectory(directory);
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;";
        await command.ExecuteNonQueryAsync(ct);
        return connection;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        foreach (var statement in Schema)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync(ct);
        }

        var freePlan = await ScalarStringAsync(connection, "SELECT Id FROM SubscriptionPlans WHERE Name = 'free'", ct);
        if (freePlan is null)
        {
            await ExecuteAsync(connection, "INSERT INTO SubscriptionPlans (Id, Name, DisplayName, StorageQuotaBytes) VALUES ($id, 'free', 'Ücretsiz Plan', 5368709120)", ct, ("$id", Guid.NewGuid().ToString()));
        }
    }

    public static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct) ?? 0);
    }

    public static async Task<string?> ScalarStringAsync(SqliteConnection connection, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    public static string HashSecret(string secret, string salt) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(salt + secret)));

    private static readonly string[] Schema =
    [
        "CREATE TABLE IF NOT EXISTS SubscriptionPlans (Id TEXT PRIMARY KEY, Name TEXT NOT NULL UNIQUE, DisplayName TEXT NOT NULL, StorageQuotaBytes INTEGER NOT NULL);",
        "CREATE TABLE IF NOT EXISTS Users (Id TEXT PRIMARY KEY, Email TEXT NOT NULL UNIQUE, DisplayName TEXT NOT NULL UNIQUE COLLATE NOCASE, PasswordHash TEXT NOT NULL, PasswordSalt TEXT NOT NULL, Role TEXT NOT NULL, PlanId TEXT NOT NULL, StorageUsedBytes INTEGER NOT NULL DEFAULT 0, FirstName TEXT NULL, LastName TEXT NULL, CreatedAt TEXT NOT NULL, FOREIGN KEY (PlanId) REFERENCES SubscriptionPlans(Id));",
        "CREATE TABLE IF NOT EXISTS LocalAgentDevices (Id TEXT PRIMARY KEY, UserId TEXT NOT NULL, DeviceName TEXT NOT NULL, TokenHash TEXT NOT NULL, TokenSalt TEXT NOT NULL, CreatedAt TEXT NOT NULL, LastSeenAt TEXT NULL, RevokedAt TEXT NULL, FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE);",
        "CREATE TABLE IF NOT EXISTS LocalAgentDeviceJobs (Id TEXT PRIMARY KEY, UserId TEXT NOT NULL, DeviceId TEXT NOT NULL, ToolName TEXT NOT NULL, ArgumentsJson TEXT NOT NULL, Status TEXT NOT NULL, CreatedAt TEXT NOT NULL, ClaimedAt TEXT NULL, CompletedAt TEXT NULL, ClaimedByDeviceId TEXT NULL, Success INTEGER NULL, ResultCode TEXT NULL, ResultMessage TEXT NULL, DryRun INTEGER NOT NULL, TimelineJson TEXT NULL, FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE, FOREIGN KEY (DeviceId) REFERENCES LocalAgentDevices(Id) ON DELETE CASCADE);",
        "CREATE INDEX IF NOT EXISTS IX_LocalAgentDeviceJobs_Device_Status ON LocalAgentDeviceJobs(DeviceId, Status, CreatedAt);",
        "CREATE TABLE IF NOT EXISTS HermesWorkerNonces (WorkerId TEXT NOT NULL, Nonce TEXT NOT NULL, ExpiresAt TEXT NOT NULL, PRIMARY KEY (WorkerId, Nonce));",
        "CREATE TABLE IF NOT EXISTS HermesWorkerHeartbeats (WorkerId TEXT PRIMARY KEY, LastSeenAt TEXT NOT NULL, HermesReady INTEGER NOT NULL, ModelReady INTEGER NOT NULL, StorageHealthy INTEGER NOT NULL, Message TEXT NULL);",
        "CREATE TABLE IF NOT EXISTS HermesWorkerJobs (Id TEXT PRIMARY KEY, UserId TEXT NOT NULL, AgentProfileId TEXT NOT NULL, ConversationId TEXT NULL, RequestId TEXT NOT NULL, WorkspaceId TEXT NOT NULL, HermesProfileName TEXT NOT NULL, Input TEXT NOT NULL, Priority INTEGER NOT NULL, Status TEXT NOT NULL, CreatedAt TEXT NOT NULL, ClaimedByWorkerId TEXT NULL, ClaimedAt TEXT NULL, LeaseExpiresAt TEXT NULL, AttemptCount INTEGER NOT NULL DEFAULT 0, MaxAttempts INTEGER NOT NULL DEFAULT 1, MaxRunSeconds INTEGER NOT NULL DEFAULT 60, FileAccessScope TEXT NOT NULL DEFAULT 'workspace', StorageQuotaBytes INTEGER NOT NULL DEFAULT 0, StorageUsedBytes INTEGER NOT NULL DEFAULT 0, IsSubAgentEnabled INTEGER NOT NULL DEFAULT 0, IsTerminalEnabled INTEGER NOT NULL DEFAULT 0, IsSystemCommandEnabled INTEGER NOT NULL DEFAULT 0, RuntimeMemoryLimitMb INTEGER NOT NULL DEFAULT 512, RuntimeIdleReleaseSeconds INTEGER NOT NULL DEFAULT 300, StoragePrefix TEXT NULL, CompletedAt TEXT NULL, Result TEXT NULL, ErrorCode TEXT NULL);",
        "CREATE INDEX IF NOT EXISTS IX_HermesWorkerJobs_Queue ON HermesWorkerJobs(Status, CreatedAt);"
    ];
}
