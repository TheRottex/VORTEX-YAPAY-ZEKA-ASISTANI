using System.Diagnostics;
using System.Security.Cryptography;

namespace Vortex.Desktop.Services;

public sealed record LocalAgentRuntimeResult(bool Ready, string Reason, string? Detail = null);

/// <summary>
/// Owns the packaged loopback companion process and its private Desktop credential.
/// The credential never appears in command-line arguments, settings JSON, or UI.
/// </summary>
public sealed class LocalAgentRuntimeService
{
    private const string LoopbackUrl = "http://127.0.0.1:47891";
    private readonly DesktopSettingsService _settings;
    private readonly LocalAgentClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _ownedProcess;
    private string? _secret;

    public LocalAgentRuntimeService(DesktopSettingsService settings, LocalAgentClient client)
    {
        _settings = settings;
        _client = client;
    }

    public async Task<LocalAgentRuntimeResult> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _secret ??= await _settings.GetOrCreateDesktopLocalAgentSecretAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(_secret)) return new(false, "secure_storage_unavailable");

            var existing = await _client.HealthAsync(LoopbackUrl, _secret, cancellationToken);
            if (existing.Ok) return new(true, "ok", existing.Detail);
            if (existing.Reason == "not_authenticated") return new(false, "loopback_auth_conflict");

            if (_ownedProcess is { HasExited: false }) return new(false, "starting");
            var companion = ResolveCompanionPath();
            if (companion is null) return new(false, "companion_missing");

            var startInfo = new ProcessStartInfo(companion.FileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = companion.WorkingDirectory
            };
            if (companion.IsManagedDll) startInfo.ArgumentList.Add(companion.PayloadPath);
            startInfo.Environment["VORTEX_LOCAL_AGENT_SECRET"] = _secret;
            startInfo.Environment["LocalAgent__Url"] = LoopbackUrl;
            _ownedProcess = Process.Start(startInfo);
            if (_ownedProcess is null) return new(false, "start_failed");
            _ownedProcess.EnableRaisingEvents = true;

            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (_ownedProcess.HasExited) return new(false, "start_failed");
                await Task.Delay(250, cancellationToken);
                var health = await _client.HealthAsync(LoopbackUrl, _secret, cancellationToken);
                if (health.Ok) return new(true, "ok", health.Detail);
                if (health.Reason == "not_authenticated") return new(false, "loopback_auth_conflict");
            }

            StopOwnedProcess();
            return new(false, "start_timeout");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalAgentInvokeResult> InvokeToolAsync(string toolName, Dictionary<string, string>? arguments, bool userConfirmed, bool dryRun, CancellationToken cancellationToken)
    {
        var ready = await EnsureReadyAsync(cancellationToken);
        if (!ready.Ready) return new(false, "local_assistant_unavailable", "Çevrimdışı Vortex Yapay Zeka Asistanı kullanılamıyor.");
        return await _client.InvokeToolAsync(LoopbackUrl, _secret, toolName, arguments, userConfirmed, dryRun, cancellationToken);
    }

    public void StopOwnedProcess()
    {
        try
        {
            if (_ownedProcess is { HasExited: false }) _ownedProcess.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        finally
        {
            _ownedProcess?.Dispose();
            _ownedProcess = null;
        }
    }

    private static LocalAgentCompanion? ResolveCompanionPath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "local-agent");
        var executable = Path.Combine(directory, OperatingSystem.IsWindows() ? "Vortex.LocalAgent.exe" : "Vortex.LocalAgent");
        if (File.Exists(executable)) return new(executable, directory, executable, false);

        // Development builds use the managed companion payload; published Desktop packages use the executable above.
        var developmentDll = Path.Combine(AppContext.BaseDirectory, "local-agent", "Vortex.LocalAgent.dll");
        if (File.Exists(developmentDll)) return new("dotnet", directory, developmentDll, true);
        return null;
    }

    private sealed record LocalAgentCompanion(string FileName, string WorkingDirectory, string PayloadPath, bool IsManagedDll);
}
