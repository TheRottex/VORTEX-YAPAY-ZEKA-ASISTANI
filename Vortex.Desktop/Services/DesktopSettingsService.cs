using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vortex.Desktop.Services;

public sealed record DesktopSettings(double MainUiScale = 1.0, double CompactUiScale = 1.0, bool RememberMe = false, bool IsOfflineMode = false, bool IsVoiceReplyEnabled = false, bool ElevenLabsTtsEnabled = false, string ElevenLabsApiKey = "", string ElevenLabsVoiceId = "", bool MinMaxTtsEnabled = false, string MinMaxApiKey = "", string MinMaxTtsVoiceId = "", string MinMaxTtsModelId = "speech-01-turbo", string WhisperModelId = "small", string ThemePreference = "System", string LocalAgentBaseUrl = "http://127.0.0.1:47891", string LocalAgentSecret = "", string LocalLlmBaseUrl = "", string LocalLlmApiKey = "", string LocalLlmModel = "", string ProxyMode = "SystemDefault", string ManualHttpProxyUrl = "", string ManualHttpsProxyUrl = "", string ProxyUsername = "", string ProxyPassword = "", int SetupWizardVersion = 0, string ServerBaseUrl = "https://api.example.invalid", string WebBaseUrl = "https://app.example.invalid")
{
    public double SafeMainUiScale => Math.Clamp(MainUiScale, 0.85, 1.25);
    public double SafeCompactUiScale => Math.Clamp(CompactUiScale, 0.85, 1.35);

    public DesktopProxyMode SafeProxyMode => ProxyMode?.Trim().ToLowerInvariant() switch
    {
        "noproxy" => DesktopProxyMode.NoProxy,
        "manual" => DesktopProxyMode.Manual,
        _ => DesktopProxyMode.SystemDefault
    };

    /// <summary>Normalized LocalAgent base URL (trim, no trailing slash). Empty if unset.</summary>
    public string SafeLocalAgentBaseUrl
    {
        get
        {
            var url = (LocalAgentBaseUrl ?? string.Empty).Trim().TrimEnd('/');
            return string.IsNullOrWhiteSpace(url) ? string.Empty : url;
        }
    }

    /// <summary>Normalized theme preference: System, Dark, or Light.</summary>
    public string SafeThemePreference
    {
        get
        {
            var p = (ThemePreference ?? "System").Trim();
            if (p.Equals("Light", StringComparison.OrdinalIgnoreCase)) return "Light";
            if (p.Equals("Dark", StringComparison.OrdinalIgnoreCase)) return "Dark";
            return "System";
        }
    }

    public Uri ResolveServerBaseUri() => ResolveConfiguredEndpoint(ServerBaseUrl);

    public Uri ResolveWebBaseUri() => ResolveConfiguredEndpoint(WebBaseUrl);

    private static Uri ResolveConfiguredEndpoint(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.Host.EndsWith(".example.invalid", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Vortex sunucu adresi yerel ayarlarda yapılandırılmadı.");
        }

        return uri;
    }
}

public interface IDesktopSecretToolRunner
{
    Task<string?> LookupAsync(string key, CancellationToken cancellationToken);
    Task StoreAsync(string key, string value, CancellationToken cancellationToken);
    Task ClearAsync(string key, CancellationToken cancellationToken);
}

public sealed class DesktopSettingsService
{
    private const string SecretServiceCollection = "vortex-desktop";
    private static readonly string[] SecretKeys = ["ElevenLabsApiKey", "MinMaxApiKey", "LocalAgentSecret", "DesktopLocalAgentSecret", "LocalLlmApiKey", "ProxyUsername", "ProxyPassword"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly string _secretPath;
    private readonly bool _useLinuxSecretTool;
    private readonly IDesktopSecretToolRunner _secretTool;

    public DesktopSettingsService()
        : this(GetDefaultSettingsDirectory(), OperatingSystem.IsLinux(), new ProcessDesktopSecretToolRunner())
    {
    }

    public DesktopSettingsService(string settingsDirectory)
        : this(settingsDirectory, OperatingSystem.IsLinux(), new ProcessDesktopSecretToolRunner())
    {
    }

    public DesktopSettingsService(string settingsDirectory, bool useLinuxSecretTool, IDesktopSecretToolRunner secretTool)
    {
        Directory.CreateDirectory(settingsDirectory);
        _path = Path.Combine(settingsDirectory, "desktop-settings.json");
        _secretPath = Path.Combine(settingsDirectory, "desktop-secrets.bin");
        _useLinuxSecretTool = useLinuxSecretTool;
        _secretTool = secretTool;
    }

    private static string GetDefaultSettingsDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = AppContext.BaseDirectory;
        return Path.Combine(root, "VortexAI");
    }

    public async Task<DesktopSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return new DesktopSettings();
        try
        {
            var json = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
            var loaded = JsonSerializer.Deserialize<DesktopSettings>(json, JsonOptions) ?? new DesktopSettings();
            var secrets = await LoadSecretsAsync(cancellationToken).ConfigureAwait(false);
            var migrated = false;
            if (!string.IsNullOrWhiteSpace(loaded.ElevenLabsApiKey))
            {
                secrets["ElevenLabsApiKey"] = loaded.ElevenLabsApiKey;
                loaded = loaded with { ElevenLabsApiKey = string.Empty };
                migrated = true;
            }
            if (!string.IsNullOrWhiteSpace(loaded.MinMaxApiKey))
            {
                secrets["MinMaxApiKey"] = loaded.MinMaxApiKey;
                loaded = loaded with { MinMaxApiKey = string.Empty };
                migrated = true;
            }
            if (!string.IsNullOrWhiteSpace(loaded.LocalAgentSecret))
            {
                secrets["LocalAgentSecret"] = loaded.LocalAgentSecret;
                loaded = loaded with { LocalAgentSecret = string.Empty };
                migrated = true;
            }
            if (!string.IsNullOrWhiteSpace(loaded.LocalLlmApiKey))
            {
                secrets["LocalLlmApiKey"] = loaded.LocalLlmApiKey;
                loaded = loaded with { LocalLlmApiKey = string.Empty };
                migrated = true;
            }
            if (!string.IsNullOrWhiteSpace(loaded.ProxyUsername) || !string.IsNullOrWhiteSpace(loaded.ProxyPassword))
            {
                UpdateSecret(secrets, "ProxyUsername", loaded.ProxyUsername);
                UpdateSecret(secrets, "ProxyPassword", loaded.ProxyPassword);
                loaded = loaded with { ProxyUsername = string.Empty, ProxyPassword = string.Empty };
                migrated = true;
            }

            if (secrets.TryGetValue("ElevenLabsApiKey", out var elevenKey)) loaded = loaded with { ElevenLabsApiKey = elevenKey };
            if (secrets.TryGetValue("MinMaxApiKey", out var minMaxKey)) loaded = loaded with { MinMaxApiKey = minMaxKey };
            if (secrets.TryGetValue("LocalAgentSecret", out var localAgentSecret)) loaded = loaded with { LocalAgentSecret = localAgentSecret };
            if (secrets.TryGetValue("LocalLlmApiKey", out var localLlmApiKey)) loaded = loaded with { LocalLlmApiKey = localLlmApiKey };
            if (secrets.TryGetValue("ProxyUsername", out var proxyUsername)) loaded = loaded with { ProxyUsername = proxyUsername };
            if (secrets.TryGetValue("ProxyPassword", out var proxyPassword)) loaded = loaded with { ProxyPassword = proxyPassword };
            if (migrated)
            {
                await SaveSecretsAsync(secrets, cancellationToken).ConfigureAwait(false);
                await SaveAsync(loaded, cancellationToken).ConfigureAwait(false);
            }
            return loaded;
        }
        catch
        {
            return new DesktopSettings();
        }
    }

    public async Task<string?> GetOrCreateDesktopLocalAgentSecretAsync(CancellationToken cancellationToken)
    {
        try
        {
            var secrets = await LoadSecretsAsync(cancellationToken).ConfigureAwait(false);
            if (secrets.TryGetValue("DesktopLocalAgentSecret", out var existing) && !string.IsNullOrWhiteSpace(existing)) return existing;
            var generated = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            secrets["DesktopLocalAgentSecret"] = generated;
            await SaveSecretsAsync(secrets, cancellationToken).ConfigureAwait(false);
            return generated;
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveAsync(DesktopSettings settings, CancellationToken cancellationToken)
    {
        var secrets = await LoadSecretsAsync(cancellationToken).ConfigureAwait(false);
        UpdateSecret(secrets, "ElevenLabsApiKey", settings.ElevenLabsApiKey);
        UpdateSecret(secrets, "MinMaxApiKey", settings.MinMaxApiKey);
        UpdateSecret(secrets, "LocalAgentSecret", settings.LocalAgentSecret);
        UpdateSecret(secrets, "LocalLlmApiKey", settings.LocalLlmApiKey);
        UpdateSecret(secrets, "ProxyUsername", settings.ProxyUsername);
        UpdateSecret(secrets, "ProxyPassword", settings.ProxyPassword);
        await SaveSecretsAsync(secrets, cancellationToken).ConfigureAwait(false);
        var persisted = settings with { ElevenLabsApiKey = string.Empty, MinMaxApiKey = string.Empty, LocalAgentSecret = string.Empty, LocalLlmApiKey = string.Empty, ProxyUsername = string.Empty, ProxyPassword = string.Empty };
        await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(persisted, JsonOptions), cancellationToken).ConfigureAwait(false);
    }

    private static void UpdateSecret(Dictionary<string, string> secrets, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) secrets.Remove(key);
        else secrets[key] = value;
    }

    private async Task<Dictionary<string, string>> LoadSecretsAsync(CancellationToken cancellationToken)
    {
        if (_useLinuxSecretTool)
        {
            return await LoadSecretsFromSecretToolAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!OperatingSystem.IsWindows()) return [];

        if (!File.Exists(_secretPath)) return [];
        try
        {
            var bytes = await File.ReadAllBytesAsync(_secretPath, cancellationToken).ConfigureAwait(false);
            var json = Encoding.UTF8.GetString(WindowsUnprotect(bytes));
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private async Task SaveSecretsAsync(Dictionary<string, string> secrets, CancellationToken cancellationToken)
    {
        if (_useLinuxSecretTool)
        {
            await SaveSecretsToSecretToolAsync(secrets, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Secure desktop secret storage is supported on Windows and Linux only.");
        }

        var json = JsonSerializer.Serialize(secrets, JsonOptions);
        await File.WriteAllBytesAsync(_secretPath, WindowsProtect(Encoding.UTF8.GetBytes(json)), cancellationToken).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, string>> LoadSecretsFromSecretToolAsync(CancellationToken cancellationToken)
    {
        var secrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in SecretKeys)
        {
            var value = await _secretTool.LookupAsync(key, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(value)) secrets[key] = value;
        }
        return secrets;
    }

    private async Task SaveSecretsToSecretToolAsync(Dictionary<string, string> secrets, CancellationToken cancellationToken)
    {
        foreach (var key in SecretKeys)
        {
            if (secrets.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                await _secretTool.StoreAsync(key, value, cancellationToken).ConfigureAwait(false);
            else
                await _secretTool.ClearAsync(key, cancellationToken).ConfigureAwait(false);
        }
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static byte[] WindowsProtect(byte[] bytes) => ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static byte[] WindowsUnprotect(byte[] bytes) => ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
}



