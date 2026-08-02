namespace Vortex.Contracts;

public static class VortexRoles
{
    public const string User = "User";
    public const string Support = "Support";
    public const string Administrator = "Administrator";
    public const string Owner = "Owner";
}

public static class SupportedFileTypes
{
    public static readonly string[] Extensions =
    [
        ".cs", ".axaml", ".xaml", ".json", ".md", ".txt", ".xml", ".yaml", ".yml",
        ".js", ".ts", ".html", ".css", ".py", ".cpp", ".h", ".ps1", ".bat"
    ];
}

public enum LocalToolRiskLevel { Low, Medium, High, Critical }

public sealed record RegisterRequest(
    string Email,
    string Password,
    string DisplayName,
    string? FirstName = null,
    string? LastName = null);

public sealed record LoginRequest(string Email, string Password);

public sealed record UserProfileDto(
    Guid Id,
    string Email,
    string DisplayName,
    string Role,
    string PlanName,
    long StorageQuotaBytes,
    long StorageUsedBytes,
    string? FirstName = null,
    string? LastName = null);

public sealed record AuthResponse(string AccessToken, DateTimeOffset ExpiresAt, UserProfileDto User);

public sealed record LocalAgentDeviceDto(Guid Id, string DeviceName, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt);

public sealed record LocalToolDescriptor(
    string Name,
    string Description,
    bool IsEnabled,
    bool RequiresConfirmation,
    LocalToolRiskLevel RiskLevel);

public sealed record QueueLocalAgentToolRequest(
    Guid DeviceId,
    string ToolName,
    Dictionary<string, string>? Arguments = null,
    bool UserConfirmed = false,
    bool DryRun = false);

public sealed record LocalAgentToolPlan(
    string ToolName,
    bool UsesPreparedTool,
    bool RequiresUserConfirmation,
    string? FallbackCommand = null);

public sealed record DeviceRegisterRequest(string? DeviceName = null);
public sealed record DeviceRegisterResponse(string DeviceId, string DeviceToken);
public sealed record DeviceJobQueuedResponse(string JobId, string Status, string Action, bool DryRun);
public sealed record DeviceJobClaimRequest(string DeviceId, string DeviceToken);
public sealed record DeviceJobDto(string JobId, string ToolName, Dictionary<string, string> Arguments);
public sealed record DeviceJobClaimResponse(DeviceJobDto? Job);
public sealed record DeviceJobCompleteRequest(
    string DeviceId,
    string DeviceToken,
    bool Success,
    string Code,
    string Message,
    string[]? Timeline);
public sealed record DeviceJobStatusResponse(
    string JobId,
    string Status,
    string ToolName,
    string? Code,
    string? Message,
    bool? Success,
    bool DryRun,
    string[] Timeline);

public enum AgentJobPriority { Low = 0, Normal = 10, High = 20 }
public enum WorkerReadinessState { Connected, HermesReady, ModelReady, StorageDegraded, NotConfigured }

public sealed record WorkerHeartbeatRequest(bool HermesReady, bool ModelReady, bool StorageHealthy, string? Message = null);
public sealed record WorkerReadinessDto(string WorkerId, bool Authenticated, bool HermesReady, bool ModelReady, bool StorageHealthy, WorkerReadinessState State, DateTimeOffset ServerTime, string? Message = null);
public sealed record WorkerClaimRequest(int MaxJobs = 1, int LeaseSeconds = 60);
public sealed record WorkerJobLeaseDto(Guid JobId, Guid UserId, Guid AgentProfileId, Guid? ConversationId, string RequestId, string WorkspaceId, string HermesProfileName, string Input, AgentJobPriority Priority, DateTimeOffset LeaseExpiresAt, int AttemptCount, int MaxAttempts, int MaxRunSeconds, string FileAccessScope, long StorageQuotaBytes = 0, long StorageUsedBytes = 0, bool IsSubAgentEnabled = false, bool IsTerminalEnabled = false, bool IsSystemCommandEnabled = false, int RuntimeMemoryLimitMb = 512, int RuntimeIdleReleaseSeconds = 300, string? StoragePrefix = null);
public sealed record WorkerCompleteJobRequest(bool Succeeded, string? Result, string? ErrorCode = null, bool Retryable = false, int InputTokens = 0, int OutputTokens = 0, long? StorageUsedBytes = null);
public sealed record HermesActionProposal(string Kind, string ToolName, IReadOnlyDictionary<string, string> Arguments, string Status, string? Summary = null);
public sealed record HermesToolCall(string ToolName, Dictionary<string, string> Arguments);
public sealed record HermesWorkerPairRequest(string PairingCode, string? DisplayName = null);
public sealed record HermesWorkerPairResponse(string WorkerId, string WorkerToken);
public sealed record HermesWorkerStatusDto(string WorkerId, string DisplayName, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, bool HermesReady, bool ModelReady, bool StorageHealthy, bool Revoked);

public static class SigningCanonical
{
    public static string Create(string method, string pathAndQuery, string timestamp, string nonce, string bodySha256)
        => string.Join('\n', method.ToUpperInvariant(), pathAndQuery, timestamp, nonce, bodySha256);

    public static string Hash(byte[] body) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)).ToLowerInvariant();

    public static string Sign(string canonical, string secret)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

public static class PathSafety
{
    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) return false;
        return !path.Replace('\\', '/').Split('/').Any(part => part is ".." or "" || part.Contains('\0'));
    }
}
