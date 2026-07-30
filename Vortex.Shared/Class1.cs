using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vortex.Shared;

public static class VortexRoles
{
    public const string User = "User";
    public const string Support = "Support";
    public const string Administrator = "Administrator";
    public const string Owner = "Owner";
    public static readonly string[] All = [User, Support, Administrator, Owner];
}

public static class VortexFeatures
{
    public const string Chat = "chat";
    public const string PremiumChat = "premium-chat";
    public const string FileContext = "file-context";
    public const string ProjectContext = "project-context";
    public const string VoiceInput = "voice-input";
    public const string TextToSpeech = "text-to-speech";
    public const string LocalTools = "local-tools";
    public const string HermesAgent = "hermes-agent";
}

public static class SupportedFileTypes
{
    public static readonly string[] Extensions =
    [
        ".cs", ".axaml", ".xaml", ".json", ".md", ".txt", ".xml", ".yaml", ".yml",
        ".js", ".ts", ".html", ".css", ".py", ".cpp", ".h", ".ps1", ".bat"
    ];
}

public enum ChatRole { System, User, Assistant, Tool }
public enum LocalToolRiskLevel { Low, Medium, High, Critical }
public enum HermesProfileStatus { Provisioning, Ready, ProvisioningFailed, Disabled }
public enum AgentExecutionStatus { Started, Succeeded, Failed, LimitRejected, TimedOut, Cancelled }
public enum AgentJobStatus { Pending, Queued, Claimed, Running, Completed, Failed, Cancelled, TimedOut, Retrying, WorkerUnavailable }
public enum AgentJobPriority { Low = 0, Normal = 10, High = 20 }
public enum WorkerReadinessState { Connected, HermesReady, ModelReady, StorageDegraded, NotConfigured }
public enum PardusThemeMode { Dark }
public enum PardusDesktopEnvironment { Unknown, Xfce, Gnome, Kde }

public sealed record PardusSetThemeRequest(PardusThemeMode Mode, bool DryRun = false);
public sealed record PardusToolResponse(string ToolName, bool Succeeded, string Message, string? Output = null, PardusDesktopEnvironment DesktopEnvironment = PardusDesktopEnvironment.Unknown, bool ConfirmationRequired = false, LocalToolRiskLevel RiskLevel = LocalToolRiskLevel.Low);
public sealed record HermesToolCall(string ToolName, Dictionary<string, string> Arguments);

public sealed record RegisterRequest(string Email, string Password, string DisplayName, string? FirstName = null, string? LastName = null, string? BirthDate = null, string? PhoneNumber = null);
public sealed record LoginRequest(string Email, string Password);
public sealed record AuthResponse(string AccessToken, DateTimeOffset ExpiresAt, UserProfileDto User);
public sealed record ExchangeOAuthCompletionRequest(string Completion);
public sealed record ExchangeOAuthCompletionResponse(AuthResponse Auth, string ReturnUrl, Guid? DesktopSessionId);
public sealed record UserProfileDto(Guid Id, string Email, string DisplayName, string Role, string PlanName, long StorageQuotaBytes, long StorageUsedBytes, string? FirstName = null, string? LastName = null);
public sealed record AgentPolicyDto(int DailyAgentRunLimit, int ActiveScheduledTaskLimit, int PersistentMemoryLimit, bool IsSubAgentEnabled, bool IsTerminalEnabled, bool IsSystemCommandEnabled, int MaxRunSeconds, int MaxConcurrentRuns, string FileAccessScope);
public sealed record AgentProfileDto(
    Guid Id,
    Guid UserId,
    string HermesProfileName,
    string HermesHomePath,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastStartedAt,
    string? WorkerId = null,
    string? WorkspaceId = null,
    long StorageQuotaBytes = 0,
    long StorageUsedBytes = 0,
    int RuntimeMemoryLimitMb = 512,
    int RuntimeIdleReleaseSeconds = 300,
    string? StoragePrefix = null);
public sealed record AgentUsageDto(DateOnly Date, int AgentRuns, int InputTokens, int OutputTokens, decimal EstimatedCost, DateTimeOffset UpdatedAt);
public sealed record AgentStatusDto(AgentProfileDto? Profile, AgentPolicyDto Policy, AgentUsageDto Usage, int RemainingRunsToday, int ActiveScheduledTaskCount, int RemainingScheduledTasks);
public sealed record AgentChatRequest(string Message, Guid? RequestedProfileId = null, string? Model = null, string? IdempotencyKey = null, Guid? ConversationId = null, IReadOnlyList<AgentChatFileReferenceDto>? Files = null);
public sealed record AgentChatFileReferenceDto(Guid Id);
public sealed record WorkerJobFileDto(Guid FileId, string Name, string ContentType, long SizeBytes, string RelativeWorkspacePath);
public sealed record AgentChatResponse(string RequestId, string Response, string ProfileName, int RemainingRunsToday, Guid? JobId = null);

public sealed record TranscriptionResponse(string Text);
public sealed record AgentTaskDto(Guid Id, string Name, string Schedule, string TimeZone, bool IsEnabled, DateTimeOffset CreatedAt);
public sealed record CreateAgentTaskRequest(string Name, string Schedule, string TimeZone);
public sealed record UserFileDto(Guid Id, string Name, long SizeBytes, string ContentType, DateTimeOffset CreatedAt);
public sealed record UserFileListDto(IReadOnlyList<UserFileDto> Files, long StorageUsedBytes, long StorageQuotaBytes);
public sealed record ConnectedAccountDto(string Provider, string DisplayName, bool IsConnected, bool HasAutomationToken = false);
public sealed record ConnectedAccountsDto(IReadOnlyList<ConnectedAccountDto> Accounts);
public sealed record ChatSessionDto(Guid Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, bool IsArchived = false, bool IsFavorite = false);
public sealed record CreateChatRequest(string? Title = null);
public sealed record RenameChatRequest(string Title);
public sealed record ArchiveChatRequest(bool Archive);
public sealed record FavoriteChatRequest(bool Favorite);
public sealed record ChatActiveStateDto(Guid? ActiveChatSessionId, DateTimeOffset? UpdatedAt = null);
public sealed record UpdateChatActiveStateRequest(Guid? ActiveChatSessionId);
public sealed record ChatMessageDto(Guid Id, Guid ChatSessionId, string Role, string Content, DateTimeOffset CreatedAt, string? ModelName, bool IsStreaming, string? ErrorMessage);
public sealed record AiChatMessage(string Role, string Content);
public sealed record ChatCompletionRequest(Guid? ChatSessionId, string Message, string? RequestedModel, string? SystemPrompt, IReadOnlyList<AttachedFileDto>? Files, bool Stream = true, string? IdempotencyKey = null);
public sealed record ChatCompletionChunk(string Delta, bool IsFinal, string? ModelName = null, string? ErrorMessage = null, string? CorrelationId = null);
public sealed record DirectChatCreditStatusDto(string Model, long UsedTokenUnits, long ReservedTokenUnits, long RemainingTokenUnits, decimal UsedCredits, decimal RemainingCredits, decimal TotalCredits, int TokensPerCredit, long TotalTokenUnits, DateTimeOffset WindowStartedAtUtc, DateTimeOffset ResetAtUtc);
public sealed record AttachedFileDto(Guid Id, string FileName, string ContentType, long SizeBytes, string? ExtractedText = null);
public sealed record AiModelDto(Guid Id, Guid ProviderId, string Name, string DisplayName, bool IsPremium, bool SupportsStreaming, bool SupportsTools, int ContextWindowTokens);
public sealed record StartDesktopAuthRequest(string StateHash, string CodeChallenge, string CallbackUri);
public sealed record StartDesktopAuthResponse(Guid SessionId, string AuthorizationUrl, DateTimeOffset ExpiresAt);
public sealed record CompleteDesktopAuthRequest(Guid SessionId, string State);
public sealed record CompleteDesktopAuthResponse(string CallbackUrl, string Message);
public sealed record ExchangeDesktopCodeRequest(Guid SessionId, string Code, string CodeVerifier, string State);
public sealed record DesktopAuthStatusResponse(Guid SessionId, bool Completed, bool Consumed, DateTimeOffset ExpiresAt);
public sealed record WebRegisterRequest(string Email, string Password, string DisplayName, bool AcceptTerms, string? FirstName = null, string? LastName = null, string? BirthDate = null, string? PhoneNumber = null);
public sealed record WebLoginRequest(string Email, string Password, bool RememberMe);
public sealed record OAuthStartResponse(string AuthorizationUrl);
public sealed record LocalAgentHello(string AgentName, string Version, string Platform, IReadOnlyList<LocalToolDescriptor> Tools);
public sealed record LocalAgentDeviceDto(Guid Id, string DeviceName, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt);
public sealed record LocalToolDescriptor(string Name, string Description, bool IsEnabled, bool RequiresConfirmation, LocalToolRiskLevel RiskLevel);
public sealed record LocalToolRequest(string RequestId, string ToolName, Dictionary<string, string> Arguments, DateTimeOffset ExpiresAt, string Signature, bool UserConfirmed);
public sealed record LocalToolResponse(string RequestId, bool Succeeded, string Message, string? Output = null);
public sealed record QueueLocalAgentToolRequest(Guid DeviceId, string ToolName, Dictionary<string, string>? Arguments = null, bool UserConfirmed = false, bool DryRun = false);
public sealed record LocalAgentToolPlan(string ToolName, bool UsesPreparedTool, bool RequiresUserConfirmation, string? FallbackCommand = null);
public sealed record AudioDeviceDto(string Id, string Name, bool IsDefaultInput, bool IsDefaultOutput);
public sealed record SpeechToTextRequest(string AudioBase64, string ContentType, string? Language);
public sealed record SpeechToTextResponse(string Text, decimal Confidence);
public sealed record TextToSpeechRequest(string Text, string? Voice, string? Language);
public sealed record TextToSpeechResponse(bool Succeeded, string Message);
public sealed record AgentJobStatusDto(Guid JobId, Guid UserId, Guid AgentProfileId, Guid? ConversationId, string RequestId, AgentJobStatus Status, AgentJobPriority Priority, DateTimeOffset CreatedAt, DateTimeOffset? ClaimedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, DateTimeOffset? LeaseExpiresAt, int AttemptCount, int MaxAttempts, string? ErrorCode, string? WorkerId, string? Result, bool CancellationRequested);
public sealed record WorkerHeartbeatRequest(bool HermesReady, bool ModelReady, bool StorageHealthy, string? Message = null);
public sealed record WorkerReadinessDto(string WorkerId, bool Authenticated, bool HermesReady, bool ModelReady, bool StorageHealthy, WorkerReadinessState State, DateTimeOffset ServerTime, string? Message = null);
public sealed record WorkerClaimRequest(int MaxJobs = 1, int LeaseSeconds = 60);
public sealed record WorkerJobLeaseDto(Guid JobId, Guid UserId, Guid AgentProfileId, Guid? ConversationId, string RequestId, string WorkspaceId, string HermesProfileName, string Input, AgentJobPriority Priority, DateTimeOffset LeaseExpiresAt, int AttemptCount, int MaxAttempts, int MaxRunSeconds, string FileAccessScope, long StorageQuotaBytes = 0, long StorageUsedBytes = 0, bool IsSubAgentEnabled = false, bool IsTerminalEnabled = false, bool IsSystemCommandEnabled = false, int RuntimeMemoryLimitMb = 512, int RuntimeIdleReleaseSeconds = 300, string? StoragePrefix = null);
public sealed record WorkerCompleteJobRequest(bool Succeeded, string? Result, string? ErrorCode = null, bool Retryable = false, int InputTokens = 0, int OutputTokens = 0, long? StorageUsedBytes = null);
public sealed record HermesActionProposal(string Kind, string ToolName, IReadOnlyDictionary<string, string> Arguments, string Status, string? Summary = null);

public sealed record ExecutorHello(string ExecutorId, string Version, string Platform, string Capabilities, DateTimeOffset SentAt);
public sealed record ExecutorHeartbeat(string ExecutorId, bool Ready, string? Message, DateTimeOffset SentAt);
public sealed record ExecutorJobEnvelope(string JobId, string Action, string Command, string? WorkingDirectory, IReadOnlyList<string> Arguments, bool RequiresApproval, DateTimeOffset ExpiresAt);
public sealed record ExecutorJobResult(string JobId, bool Succeeded, string? StdOut, string? StdErr, int ExitCode, string? ErrorCode, string? Message, DateTimeOffset CompletedAt);
public sealed record ExecutorControlMessage(string Type, string? Message = null);
public sealed record ExecutorPolicy(string ToolName, bool Enabled, bool RequiresApproval, LocalToolRiskLevel RiskLevel, string[] AllowedArguments);
public sealed record ExecutorWireMessage(string Type, JsonElement? Payload = null);
public sealed record ExecutorDeviceJob(string JobId, string ToolName, JsonElement Arguments);
public sealed record ExecutorDeviceJobResult(string JobId, bool Success, string Code, string Message, bool DryRun, string? PlannedCommand, string? Output, string? TechnicalDetails, string[] Timeline);
public sealed record ExecutorCancelJob(string JobId, string Reason);
public sealed record ExecutorStreamChunk(string JobId, string Stream, string Text, DateTimeOffset SentAt);

public static class SecretMasker
{
    public static string Mask(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        if (trimmed.Length <= 8) return "••••";
        return trimmed[..Math.Min(3, trimmed.Length)] + "-••••••••••••••••" + trimmed[^4..];
    }
}

public static class SigningCanonical
{
    public static string Create(string method, string pathAndQuery, string timestamp, string nonce, string bodySha256)
        => string.Join('\n', method.ToUpperInvariant(), pathAndQuery, timestamp, nonce, bodySha256);

    public static string Hash(byte[] body) => Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();

    public static string Sign(string canonical, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

public static class PathSafety
{
    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (Path.IsPathRooted(path)) return false;
        var normalized = path.Replace('\\', '/');
        return !normalized.Split('/').Any(part => part is ".." or "" || part.Contains('\0'));
    }
}


public sealed record ServerTextToSpeechRequest(string Text, string? Voice = null, string? Language = null, string? Model = null, string? Provider = null);
public sealed record ServerTextToSpeechResponse(bool Succeeded, string Message, string? AudioBase64 = null, string? ContentType = null);


