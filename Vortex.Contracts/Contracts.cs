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
