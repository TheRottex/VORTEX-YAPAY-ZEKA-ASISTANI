using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Vortex.Shared;

namespace Vortex.Desktop.Services;

public sealed record LocalChatMessagesReadResult(IReadOnlyList<ChatMessageDto> Messages, int SkippedUnreadableCount);

public sealed class LocalDatabaseService
{
    private readonly string _connectionString;
    private readonly byte[] _entropy = "VortexLocalEncryptionEntropyBytes"u8.ToArray();
    private readonly byte[] _localEncryptionKey;

    public LocalDatabaseService(string? dataDirectory = null)
    {
        var root = dataDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = AppContext.BaseDirectory;
        var dir = dataDirectory is null ? Path.Combine(root, "VortexAI") : root;
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "vortex_local.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        _localEncryptionKey = LoadOrCreateLocalEncryptionKey(dir);
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS LocalChatSessions (
                Id TEXT PRIMARY KEY,
                Title TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                IsArchived INTEGER NOT NULL DEFAULT 0,
                IsFavorite INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS LocalChatMessages (
                Id TEXT PRIMARY KEY,
                ChatSessionId TEXT NOT NULL,
                Role TEXT NOT NULL,
                EncryptedContent TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                ModelName TEXT NULL,
                FOREIGN KEY(ChatSessionId) REFERENCES LocalChatSessions(Id) ON DELETE CASCADE
            );
        ";
        command.ExecuteNonQuery();
    }

    private string EncryptText(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;
        try
        {
            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            byte[] encryptedBytes;
            if (OperatingSystem.IsWindows())
            {
                encryptedBytes = ProtectedData.Protect(plainBytes, _entropy, DataProtectionScope.CurrentUser);
            }
            else
            {
                encryptedBytes = EncryptAesGcm(plainBytes, _localEncryptionKey);
            }
            return Convert.ToBase64String(encryptedBytes);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Local offline message encryption failed.", ex);
        }
    }

    private string DecryptText(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return string.Empty;
        try
        {
            var cipherBytes = Convert.FromBase64String(cipherText);
            byte[] decryptedBytes;
            if (OperatingSystem.IsWindows())
            {
                decryptedBytes = ProtectedData.Unprotect(cipherBytes, _entropy, DataProtectionScope.CurrentUser);
            }
            else
            {
                decryptedBytes = DecryptAesGcm(cipherBytes, _localEncryptionKey);
            }
            return Encoding.UTF8.GetString(decryptedBytes);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Local offline message decryption failed.", ex);
        }
    }

    private static byte[] LoadOrCreateLocalEncryptionKey(string directory)
    {
        var path = Path.Combine(directory, "vortex_local.key");
        if (File.Exists(path))
        {
            var existing = Convert.FromBase64String(File.ReadAllText(path));
            if (existing.Length == 32) return existing;
            throw new InvalidOperationException("Local offline encryption key is invalid.");
        }

        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(path, Convert.ToBase64String(key));
        try { File.SetAttributes(path, FileAttributes.Hidden); } catch { }
        return key;
    }

    private static byte[] EncryptAesGcm(byte[] plaintext, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return nonce.Concat(tag).Concat(ciphertext).ToArray();
    }

    private static byte[] DecryptAesGcm(byte[] payload, byte[] key)
    {
        if (payload.Length < 28) throw new CryptographicException("Invalid local encrypted payload.");
        var plaintext = new byte[payload.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(payload.AsSpan(0, 12), payload.AsSpan(12, 16), payload.AsSpan(28), plaintext);
        return plaintext;
    }

    public async Task<List<ChatSessionDto>> ListSessionsAsync(CancellationToken ct)
    {
        var sessions = new List<ChatSessionDto>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Title, CreatedAt, UpdatedAt, IsArchived, IsFavorite FROM LocalChatSessions WHERE IsArchived = 0 ORDER BY UpdatedAt DESC";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            sessions.Add(new ChatSessionDto(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2)),
                DateTimeOffset.Parse(reader.GetString(3)),
                reader.GetInt32(4) == 1,
                reader.GetInt32(5) == 1
            ));
        }
        return sessions;
    }

    public async Task<List<ChatSessionDto>> ListArchivedSessionsAsync(CancellationToken ct)
    {
        var sessions = new List<ChatSessionDto>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Title, CreatedAt, UpdatedAt, IsArchived, IsFavorite FROM LocalChatSessions WHERE IsArchived = 1 ORDER BY UpdatedAt DESC";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            sessions.Add(new ChatSessionDto(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2)),
                DateTimeOffset.Parse(reader.GetString(3)),
                reader.GetInt32(4) == 1,
                reader.GetInt32(5) == 1
            ));
        }
        return sessions;
    }

    public async Task CreateSessionAsync(Guid id, string title, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO LocalChatSessions (Id, Title, CreatedAt, UpdatedAt) VALUES ($id, $title, $now, $now)";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task AppendMessageAsync(Guid sessionId, string role, string content, string? modelName, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var encrypted = EncryptText(content);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "INSERT INTO LocalChatMessages (Id, ChatSessionId, Role, EncryptedContent, CreatedAt, ModelName) VALUES ($id, $sessionId, $role, $content, $now, $modelName)";
            command.Parameters.AddWithValue("$id", id.ToString());
            command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            command.Parameters.AddWithValue("$role", role);
            command.Parameters.AddWithValue("$content", encrypted);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$modelName", (object?)modelName ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "UPDATE LocalChatSessions SET UpdatedAt = $now WHERE Id = $id";
            command.Parameters.AddWithValue("$id", sessionId.ToString());
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<List<ChatMessageDto>> GetMessagesAsync(Guid sessionId, CancellationToken ct)
        => (await GetReadableMessagesAsync(sessionId, ct)).Messages.ToList();

    public async Task<LocalChatMessagesReadResult> GetReadableMessagesAsync(Guid sessionId, CancellationToken ct)
    {
        var messages = new List<ChatMessageDto>();
        var skippedUnreadableCount = 0;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Role, EncryptedContent, CreatedAt, ModelName FROM LocalChatMessages WHERE ChatSessionId = $sessionId ORDER BY CreatedAt ASC";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            try
            {
                messages.Add(new ChatMessageDto(
                    Guid.Parse(reader.GetString(0)),
                    sessionId,
                    reader.GetString(1),
                    DecryptText(reader.GetString(2)),
                    DateTimeOffset.Parse(reader.GetString(3)),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    false,
                    null
                ));
            }
            catch (InvalidOperationException ex) when (ex.Message == "Local offline message decryption failed.")
            {
                skippedUnreadableCount++;
                DesktopLogService.Error($"Yerel sohbet geçmişinde okunamayan şifreli mesaj atlandı. sessionId={sessionId}", ex);
            }
        }
        return new LocalChatMessagesReadResult(messages, skippedUnreadableCount);
    }

    public async Task DeleteSessionAsync(Guid sessionId, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using (var deleteMessages = connection.CreateCommand())
        {
            deleteMessages.Transaction = (SqliteTransaction)transaction;
            deleteMessages.CommandText = "DELETE FROM LocalChatMessages WHERE ChatSessionId = $id";
            deleteMessages.Parameters.AddWithValue("$id", sessionId.ToString());
            await deleteMessages.ExecuteNonQueryAsync(ct);
        }

        await using (var deleteSession = connection.CreateCommand())
        {
            deleteSession.Transaction = (SqliteTransaction)transaction;
            deleteSession.CommandText = "DELETE FROM LocalChatSessions WHERE Id = $id";
            deleteSession.Parameters.AddWithValue("$id", sessionId.ToString());
            await deleteSession.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }
    public async Task ArchiveSessionAsync(Guid sessionId, bool archive, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE LocalChatSessions SET IsArchived = $archive WHERE Id = $id";
        command.Parameters.AddWithValue("$id", sessionId.ToString());
        command.Parameters.AddWithValue("$archive", archive ? 1 : 0);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task FavoriteSessionAsync(Guid sessionId, bool favorite, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE LocalChatSessions SET IsFavorite = $favorite WHERE Id = $id";
        command.Parameters.AddWithValue("$id", sessionId.ToString());
        command.Parameters.AddWithValue("$favorite", favorite ? 1 : 0);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task RenameSessionAsync(Guid sessionId, string title, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE LocalChatSessions SET Title = $title, UpdatedAt = $now WHERE Id = $id";
        command.Parameters.AddWithValue("$id", sessionId.ToString());
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task ClearAllSessionsAsync(CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using (var deleteMessages = connection.CreateCommand())
        {
            deleteMessages.Transaction = (SqliteTransaction)transaction;
            deleteMessages.CommandText = "DELETE FROM LocalChatMessages";
            await deleteMessages.ExecuteNonQueryAsync(ct);
        }

        await using (var deleteSessions = connection.CreateCommand())
        {
            deleteSessions.Transaction = (SqliteTransaction)transaction;
            deleteSessions.CommandText = "DELETE FROM LocalChatSessions";
            await deleteSessions.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }
}



