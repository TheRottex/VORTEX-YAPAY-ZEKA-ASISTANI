using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Vortex.Contracts;

var config = WorkerConfig.FromEnvironment(args);
WorkerLog.Initialize(config.DataRoot);
using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };
using var http = new HttpClient { BaseAddress = new Uri(config.ServerBaseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(90) };
var runtime = new HermesRuntime(config);
WorkerLog.Info($"Vortex Hermes Worker started. WorkerId={config.WorkerId}; Server={config.ServerBaseUrl}");

while (!stopping.IsCancellationRequested)
{
    try
    {
        var readiness = runtime.GetReadiness();
        var heartbeat = await SendAsync(HttpMethod.Post, "api/worker/heartbeat", new WorkerHeartbeatRequest(readiness.HermesReady, readiness.ModelReady, readiness.StorageHealthy, readiness.Message), config, stopping.Token);
        heartbeat.EnsureSuccessStatusCode();
        WorkerLog.Info($"Worker heartbeat accepted. ready={readiness.IsReady}; hermesReady={readiness.HermesReady}; modelReady={readiness.ModelReady}; storageHealthy={readiness.StorageHealthy}; message={readiness.Message ?? "none"}");
        if (!readiness.IsReady)
        {
            await Task.Delay(TimeSpan.FromSeconds(config.IdlePollSeconds), stopping.Token);
            continue;
        }

        var claim = await SendAsync(HttpMethod.Post, "api/worker/jobs/claim", new WorkerClaimRequest(1, config.LeaseSeconds), config, stopping.Token);
        if (claim.StatusCode == HttpStatusCode.NoContent)
        {
            await Task.Delay(TimeSpan.FromSeconds(config.IdlePollSeconds), stopping.Token);
            continue;
        }
        claim.EnsureSuccessStatusCode();
        var job = await claim.Content.ReadFromJsonAsync<WorkerJobLeaseDto>(WorkerJson.Options, stopping.Token) ?? throw new InvalidOperationException("Server boş iş döndürdü.");
        WorkerLog.Info($"Worker claimed job {job.JobId}; attempt={job.AttemptCount}/{job.MaxAttempts}; maxRunSeconds={job.MaxRunSeconds}");
        (await SendAsync(HttpMethod.Post, $"api/worker/jobs/{job.JobId}/heartbeat", new { }, config, stopping.Token)).EnsureSuccessStatusCode();
        using var jobHeartbeat = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        var heartbeatTask = HeartbeatDuringRunAsync(job.JobId, config, jobHeartbeat.Token);
        var result = await runtime.RunAsync(job, stopping.Token);
        await jobHeartbeat.CancelAsync();
        try { await heartbeatTask; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { WorkerLog.Error($"Worker job heartbeat failed for {job.JobId}", ex); }
        (await SendAsync(HttpMethod.Post, $"api/worker/jobs/{job.JobId}/complete", result, config, stopping.Token)).EnsureSuccessStatusCode();
        WorkerLog.Info($"Worker completed job {job.JobId}; succeeded={result.Succeeded}; errorCode={result.ErrorCode ?? "none"}");
    }
    catch (OperationCanceledException) when (stopping.IsCancellationRequested)
    {
        break;
    }
    catch (Exception ex)
    {
        WorkerLog.Error("Worker loop failed", ex);
        await Task.Delay(TimeSpan.FromSeconds(config.ErrorDelaySeconds), stopping.Token);
    }
}

async Task HeartbeatDuringRunAsync(Guid jobId, WorkerConfig config, CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, config.LeaseSeconds / 3)), cancellationToken);
        await SendAsync(HttpMethod.Post, $"api/worker/jobs/{jobId}/heartbeat", new { }, config, cancellationToken);
    }
}

async Task<HttpResponseMessage> SendAsync<T>(HttpMethod method, string path, T body, WorkerConfig config, CancellationToken cancellationToken)
{
    var bytes = JsonSerializer.SerializeToUtf8Bytes(body, WorkerJson.Options);
    using var request = new HttpRequestMessage(method, path);
    request.Content = new ByteArrayContent(bytes);
    request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
    var timestamp = DateTimeOffset.UtcNow.ToString("O");
    var nonce = Guid.NewGuid().ToString("N");
    var target = "/" + path.TrimStart('/');
    var bodyHash = SigningCanonical.Hash(bytes);
    var canonical = SigningCanonical.Create(method.Method, target, timestamp, nonce, bodyHash);
    request.Headers.Add("X-Vortex-Worker-Id", config.WorkerId);
    request.Headers.Add("X-Vortex-Timestamp", timestamp);
    request.Headers.Add("X-Vortex-Nonce", nonce);
    request.Headers.Add("X-Vortex-Signature", SigningCanonical.Sign(canonical, config.ServiceToken));
    return await http.SendAsync(request, cancellationToken);
}

public sealed record WorkerReadiness(bool HermesReady, bool ModelReady, bool StorageHealthy, string? Message)
{
    public bool IsReady => HermesReady && ModelReady && StorageHealthy;
}

public sealed class HermesRuntime(WorkerConfig config)
{
    private readonly object probeLock = new();
    private HermesProbeResult? cachedProbe;
    private DateTimeOffset cachedProbeUntil;

    public WorkerReadiness GetReadiness()
    {
        if (!WorkerConfig.IsValidHermesInputMode(config.HermesInputMode)) return new WorkerReadiness(false, false, Directory.Exists(config.DataRoot), "Hermes input mode must be 'args' or 'stdin'.");
        if (!config.IsDockerExecutionMode) return new WorkerReadiness(false, false, Directory.Exists(config.DataRoot), "DockerOnlyExecutionModeRequired");
        var storageHealthy = Directory.Exists(config.DataRoot);
        var docker = GetDockerReadiness();
        if (!docker.IsReady) return new WorkerReadiness(false, false, storageHealthy, docker.Message);
        if (string.IsNullOrWhiteSpace(config.DockerHermesImage) || !IsSafeDockerImage(config.DockerHermesImage)) return new WorkerReadiness(false, false, storageHealthy, "DockerHermesImageInvalid");
        if (!HostIdentity.TryGetCurrent(out _, out _)) return new WorkerReadiness(false, false, storageHealthy, "DockerHostIdentityUnavailable");
        return new WorkerReadiness(true, true, storageHealthy, null);
    }

    public HermesProbeResult GetProbeResult()
    {
        lock (probeLock)
        {
            if (cachedProbe is not null && DateTimeOffset.UtcNow < cachedProbeUntil) return cachedProbe;
        }

        var result = ProbeHermesExecutable();
        lock (probeLock)
        {
            cachedProbe = result;
            cachedProbeUntil = DateTimeOffset.UtcNow.AddSeconds(30);
        }
        return result;
    }

    public DockerReadiness GetDockerReadiness()
    {
        var dockerCommand = string.IsNullOrWhiteSpace(config.DockerCliPath) ? "docker" : config.DockerCliPath;
        var resolvedDocker = ResolveCommand(dockerCommand);
        if (resolvedDocker is null) return new DockerReadiness(false, null, "DockerUnavailable: Docker CLI unavailable.");
        var probe = TryProbe(resolvedDocker, "version --format {{.Server.Version}}");
        if (!probe.Started) return new DockerReadiness(false, resolvedDocker, "DockerUnavailable: Docker CLI could not start.");
        if (probe.ExitCode != 0) return new DockerReadiness(false, resolvedDocker, "DockerUnavailable: Docker daemon/version check failed.");
        return new DockerReadiness(true, resolvedDocker, null);
    }

    public async Task<WorkerCompleteJobRequest> RunAsync(WorkerJobLeaseDto job, CancellationToken cancellationToken)
    {
        var readiness = GetReadiness();
        if (!readiness.IsReady) return CompleteFailure(readiness.Message ?? "WorkerNotReady", true, job, 0);

        var policyError = ValidatePolicy(job);
        if (policyError is not null) return CompleteFailure(policyError, false, job, null);

        WorkspaceInfo workspace;
        long initialUsage;
        var effectiveQuotaBytes = EffectiveQuotaBytes(job.StorageQuotaBytes);
        try
        {
            workspace = ResolveWorkspace(job.WorkspaceId);
            SeedHermesHome(workspace);
            initialUsage = MeasureUserStorage(workspace.RootPath);
            if (effectiveQuotaBytes > 0 && initialUsage > effectiveQuotaBytes) return CompleteFailure("StorageQuotaExceeded", false, job, initialUsage);
            if (AvailableBytes(workspace.RootPath) < EffectiveMinimumFreeBytes()) return CompleteFailure("StorageUnavailable", true, job, initialUsage);
        }
        catch (InvalidOperationException ex)
        {
            return CompleteFailure(ex.Message, false, job, null);
        }

        var docker = GetDockerReadiness();
        if (!docker.IsReady || string.IsNullOrWhiteSpace(docker.ResolvedDockerCli)) return CompleteFailure(docker.Message ?? "DockerUnavailable", true, job, initialUsage);

        ProcessStartInfo startInfo;
        try
        {
            startInfo = BuildDockerHermesStartInfo(config, job, workspace, docker.ResolvedDockerCli);
        }
        catch (InvalidOperationException ex)
        {
            return CompleteFailure(ex.Message, false, job, initialUsage);
        }

        using var process = new Process { StartInfo = startInfo };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(job.MaxRunSeconds, 1, 86400)));
        using var quotaMonitor = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        try
        {
            var effectiveMinimumFreeBytes = EffectiveMinimumFreeBytes();
            ValidateWorkspaceForProcessStart(workspace, effectiveQuotaBytes, effectiveMinimumFreeBytes);
            process.Start();
            if (process.StartInfo.RedirectStandardInput)
            {
                await process.StandardInput.WriteAsync(job.Input.AsMemory(), timeout.Token);
                await process.StandardInput.FlushAsync(timeout.Token);
                process.StandardInput.Close();
            }
            var monitorTask = MonitorQuotaAsync(process, workspace.RootPath, effectiveQuotaBytes, effectiveMinimumFreeBytes, quotaMonitor.Token);
            var stdoutTask = ReadBoundedAsync(process.StandardOutput, config.MaxStdoutBytes, process, timeout.Token);
            var stderrTask = ReadBoundedAsync(process.StandardError, config.MaxStderrBytes, process, timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                KillProcessTree(process);
                return CompleteFailure("TimedOut", false, job, SafeMeasure(workspace.RootPath));
            }
            await quotaMonitor.CancelAsync();
            var quotaError = await ObserveMonitorAsync(monitorTask);
            if (quotaError is not null) return CompleteFailure(quotaError, false, job, SafeMeasure(workspace.RootPath));

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (stdout.Exceeded || stderr.Exceeded)
            {
                KillProcessTree(process);
                return CompleteFailure(stdout.Exceeded ? "StdoutLimitExceeded" : "StderrLimitExceeded", false, job, SafeMeasure(workspace.RootPath));
            }
            if (process.ExitCode != 0)
            {
                WorkerLog.Error($"Hermes failed for job {job.JobId}; stderr length={stderr.Text.Length}");
                return CompleteFailure(config.IsDockerExecutionMode ? "DockerHermesProcessFailed" : "HermesProcessFailed", true, job, SafeMeasure(workspace.RootPath));
            }
            var finalUsage = SafeMeasure(workspace.RootPath);
            if (finalUsage is null) return CompleteFailure("WorkspaceReparsePointRejected", false, job, null);
            if (effectiveQuotaBytes > 0 && finalUsage > effectiveQuotaBytes) return CompleteFailure("StorageQuotaExceeded", false, job, finalUsage);
            var proposal = TryCreateDeviceActionProposal(stdout.Text);
            if (proposal is not null)
            {
                return new WorkerCompleteJobRequest(true, JsonSerializer.Serialize(proposal, WorkerJson.Options), null, false, CountTokens(job.Input), CountTokens(stdout.Text), finalUsage);
            }
            if (ParseHermesToolCall(stdout.Text) is not null)
            {
                return CompleteFailure("HermesToolProposalUnsupported", false, job, finalUsage);
            }
            return new WorkerCompleteJobRequest(true, stdout.Text.Trim(), null, false, CountTokens(job.Input), CountTokens(stdout.Text), finalUsage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            KillProcessTree(process);
            throw;
        }
        catch (InvalidOperationException ex)
        {
            KillProcessTree(process);
            return CompleteFailure(ex.Message, false, job, SafeMeasure(workspace.RootPath));
        }
    }

    private HermesProbeResult ProbeHermesExecutable()
    {
        var command = config.HermesExecutablePath;
        if (string.IsNullOrWhiteSpace(command)) command = "hermes";
        var resolved = ResolveCommand(command);
        if (resolved is null) return new HermesProbeResult(false, null, "Hermes executable unavailable; set HERMES_EXECUTABLE_PATH or install 'hermes' on PATH.", null);

        var probe = TryProbe(resolved, config.HermesProbeArgumentsTemplate);
        if (!probe.Started) return new HermesProbeResult(false, resolved, "Hermes command could not start.", null);
        if (probe.ExitCode != config.HermesProbeRequiredExitCode) return new HermesProbeResult(false, resolved, "Hermes probe failed.", null);
        var evidence = (probe.Stdout + "\n" + probe.Stderr).Trim();
        if (!string.IsNullOrWhiteSpace(config.HermesProbeExpectedText) && !evidence.Contains(config.HermesProbeExpectedText, StringComparison.OrdinalIgnoreCase))
        {
            return new HermesProbeResult(false, resolved, "Configured command did not satisfy Hermes probe expected text.", FirstLine(evidence));
        }

        return new HermesProbeResult(true, resolved, null, FirstLine(evidence));
    }

    public static string? ResolveCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        if (Path.IsPathRooted(command) || command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar)) return File.Exists(command) ? Path.GetFullPath(command) : null;
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = OperatingSystem.IsWindows()
            ? ((Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            : Array.Empty<string>();
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, command);
            if (File.Exists(candidate)) return candidate;
            foreach (var extension in extensions)
            {
                if (command.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;
                var extended = candidate + extension;
                if (File.Exists(extended)) return extended;
            }
        }
        return null;
    }

    private static ProbeProcessResult TryProbe(string fileName, string? argumentsTemplate)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        foreach (var argument in SplitArguments(argumentsTemplate ?? "--version")) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        RemoveWorkerSecrets(process.StartInfo);
        try
        {
            process.Start();
            if (!process.WaitForExit(5000))
            {
                KillProcessTree(process);
                return new ProbeProcessResult(true, -1, string.Empty, string.Empty);
            }
            return new ProbeProcessResult(true, process.ExitCode, process.StandardOutput.ReadToEnd(), process.StandardError.ReadToEnd());
        }
        catch
        {
            return new ProbeProcessResult(false, -1, string.Empty, string.Empty);
        }
    }

    private static string? FirstLine(string text)
        => text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    private static string? ValidatePolicy(WorkerJobLeaseDto job)
    {
        if (!string.Equals(job.FileAccessScope, "workspace", StringComparison.OrdinalIgnoreCase)) return "UnsupportedFileAccessScope";
        if (job.IsTerminalEnabled) return "TerminalToolPolicyUnsupported";
        if (job.IsSystemCommandEnabled) return "SystemCommandPolicyUnsupported";
        if (job.IsSubAgentEnabled) return "SubAgentPolicyUnsupported";
        return null;
    }

    private void SeedHermesHome(WorkspaceInfo workspace)
    {
        CopyHermesSeedFile(config.HermesConfigSeedPath, workspace.HermesHomePath, "config.yaml", required: true, "HermesConfigSeedUnavailable");
        CopyHermesSeedFile(config.HermesEnvSeedPath, workspace.HermesHomePath, ".env", required: false, "HermesEnvSeedUnavailable");
    }

    private static void CopyHermesSeedFile(string? sourcePath, string hermesHomePath, string targetFileName, bool required, string errorCode)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return;
        string fullSource;
        try
        {
            fullSource = Path.GetFullPath(sourcePath);
            if (!File.Exists(fullSource) || Directory.Exists(fullSource)) throw new InvalidOperationException(errorCode);
            var fileName = Path.GetFileName(fullSource);
            if (!fileName.Equals(targetFileName, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(errorCode);
            var attributes = File.GetAttributes(fullSource);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException(errorCode);
            Directory.CreateDirectory(hermesHomePath);
            var target = Path.GetFullPath(Path.Combine(hermesHomePath, targetFileName));
            EnsureInside(hermesHomePath, target, "WorkspacePathRejected");
            if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("WorkspaceReparsePointRejected");
            File.Copy(fullSource, target, overwrite: true);
        }
        catch (InvalidOperationException) when (!required)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException(errorCode, ex);
        }
    }

    private static void ApplyPolicyEnvironment(ProcessStartInfo startInfo, WorkerJobLeaseDto job, WorkspaceInfo workspace)
    {
        RemoveWorkerSecrets(startInfo);
        var tempPath = Path.GetFullPath(Path.Combine(workspace.RootPath, "temp"));
        Directory.CreateDirectory(tempPath);
        startInfo.Environment["VORTEX_TOOL_FILE_SCOPE"] = "workspace";
        startInfo.Environment["VORTEX_WORKSPACE_ROOT"] = workspace.WorkspacePath;
        startInfo.Environment["VORTEX_WORKSPACE_POLICY"] = "fail-closed-no-reparse-points";
        startInfo.Environment["VORTEX_ALLOW_TERMINAL"] = job.IsTerminalEnabled ? "0" : "0";
        startInfo.Environment["VORTEX_ALLOW_SYSTEM_COMMAND"] = job.IsSystemCommandEnabled ? "0" : "0";
        startInfo.Environment["VORTEX_ALLOW_SUBAGENT"] = job.IsSubAgentEnabled ? "0" : "0";
        startInfo.Environment["HERMES_HOME"] = workspace.HermesHomePath;
        startInfo.Environment["HOME"] = workspace.HermesHomePath;
        startInfo.Environment["USERPROFILE"] = workspace.HermesHomePath;
        startInfo.Environment["TMP"] = tempPath;
        startInfo.Environment["TEMP"] = tempPath;
        startInfo.Environment["TMPDIR"] = tempPath;
    }

    public static void RemoveWorkerSecrets(ProcessStartInfo startInfo)
    {
        foreach (var key in startInfo.Environment.Keys.Cast<string>().Where(IsWorkerSecretName).ToArray()) startInfo.Environment.Remove(key);
    }

    private static bool IsWorkerSecretName(string key)
        => key.StartsWith("VORTEX_WORKER_", StringComparison.OrdinalIgnoreCase)
            || key.Equals("VORTEX_LOCAL_AGENT_SECRET", StringComparison.OrdinalIgnoreCase)
            || key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
            || key.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
            || key.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)
            || key.Contains("API_KEY", StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<string> BuildHermesArguments(string? template, WorkerJobLeaseDto job, WorkspaceInfo workspace)
    {
        var argumentTemplate = string.IsNullOrWhiteSpace(template)
            ? "-z {input}"
            : template;
        foreach (var token in SplitArguments(argumentTemplate))
        {
            yield return token
                .Replace("{profile}", job.HermesProfileName, StringComparison.Ordinal)
                .Replace("{workspace}", workspace.WorkspacePath, StringComparison.Ordinal)
                .Replace("{input}", job.Input, StringComparison.Ordinal)
                .Replace("{jobId}", job.JobId.ToString(), StringComparison.Ordinal)
                .Replace("{workspaceId}", job.WorkspaceId, StringComparison.Ordinal);
        }
    }

    public static ProcessStartInfo BuildProcessHermesStartInfo(WorkerConfig config, WorkerJobLeaseDto job, WorkspaceInfo workspace, string resolvedHermesCommand)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = resolvedHermesCommand,
            WorkingDirectory = workspace.WorkspacePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = config.HermesInputMode.Equals("stdin", StringComparison.OrdinalIgnoreCase)
        };
        foreach (var argument in BuildHermesArguments(config.HermesArgumentsTemplate, job, workspace)) startInfo.ArgumentList.Add(argument);
        ApplyPolicyEnvironment(startInfo, job, workspace);
        return startInfo;
    }

    public static string BuildDockerContainerName(WorkerJobLeaseDto job)
    {
        var source = $"vortex-hermes-{job.UserId:N}-{job.WorkspaceId}-{job.HermesProfileName}";
        var builder = new StringBuilder(source.Length);
        foreach (var c in source.ToLowerInvariant()) builder.Append(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '.' || c == '-' ? c : '-');
        var safe = builder.ToString().Trim('-', '.', '_');
        if (string.IsNullOrWhiteSpace(safe)) safe = "vortex-hermes";
        return safe.Length <= 63 ? safe : safe[..63].TrimEnd('-', '.', '_');
    }

    public static ProcessStartInfo BuildDockerHermesStartInfo(WorkerConfig config, WorkerJobLeaseDto job, WorkspaceInfo workspace, string resolvedDockerCli)
    {
        if (string.IsNullOrWhiteSpace(config.DockerHermesImage) || !IsSafeDockerImage(config.DockerHermesImage)) throw new InvalidOperationException("DockerHermesImageInvalid");
        var startInfo = new ProcessStartInfo
        {
            FileName = resolvedDockerCli,
            WorkingDirectory = workspace.WorkspacePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = config.HermesInputMode.Equals("stdin", StringComparison.OrdinalIgnoreCase)
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--rm");
        if (!HostIdentity.TryGetCurrent(out var uid, out var gid)) throw new InvalidOperationException("DockerHostIdentityUnavailable");
        startInfo.ArgumentList.Add("--name");
        startInfo.ArgumentList.Add(BuildDockerContainerName(job));
        startInfo.ArgumentList.Add("--user");
        startInfo.ArgumentList.Add($"{uid}:{gid}");
        startInfo.ArgumentList.Add("--network");
        startInfo.ArgumentList.Add("bridge");
        startInfo.ArgumentList.Add("--add-host");
        startInfo.ArgumentList.Add("host.docker.internal:host-gateway");
        startInfo.ArgumentList.Add("--workdir");
        startInfo.ArgumentList.Add("/workspace");
        ApplyDockerResourceLimits(startInfo, config, job);
        startInfo.ArgumentList.Add("--mount");
        startInfo.ArgumentList.Add($"type=bind,source={workspace.WorkspacePath},target=/workspace");
        startInfo.ArgumentList.Add("--mount");
        startInfo.ArgumentList.Add($"type=bind,source={workspace.HermesHomePath},target=/vortex/hermes-home");
        ApplyPolicyEnvironment(startInfo, job, workspace);
        startInfo.Environment["HERMES_HOME"] = "/vortex/hermes-home";
        startInfo.Environment["HOME"] = "/vortex/hermes-home";
        startInfo.Environment["USERPROFILE"] = "/vortex/hermes-home";
        startInfo.ArgumentList.Add("--env");
        startInfo.ArgumentList.Add("HERMES_HOME=/vortex/hermes-home");
        startInfo.ArgumentList.Add("--env");
        startInfo.ArgumentList.Add("HOME=/vortex/hermes-home");
        startInfo.ArgumentList.Add("--env");
        startInfo.ArgumentList.Add("USERPROFILE=/vortex/hermes-home");
        startInfo.ArgumentList.Add("--env");
        startInfo.ArgumentList.Add("VORTEX_TOOL_FILE_SCOPE=workspace");
        startInfo.ArgumentList.Add("--env");
        startInfo.ArgumentList.Add("VORTEX_WORKSPACE_ROOT=/workspace");
        startInfo.ArgumentList.Add("--env");
        startInfo.ArgumentList.Add("VORTEX_WORKSPACE_POLICY=fail-closed-no-reparse-points");
        AddAllowedModelRouterEnvironment(startInfo);
        startInfo.ArgumentList.Add(config.DockerHermesImage);
        var containerWorkspace = new WorkspaceInfo("/workspace", "/workspace", "/vortex/hermes-home");
        foreach (var argument in BuildHermesArguments(config.HermesArgumentsTemplate, job, containerWorkspace)) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static void ApplyDockerResourceLimits(ProcessStartInfo startInfo, WorkerConfig config, WorkerJobLeaseDto job)
    {
        var limits = config.ResolveDockerResourceLimits(job.RuntimeMemoryLimitMb);
        if (limits is null) return;

        startInfo.ArgumentList.Add("--memory");
        startInfo.ArgumentList.Add($"{limits.MemoryMb}m");
        if (limits.MemorySwapMb is not null)
        {
            startInfo.ArgumentList.Add("--memory-swap");
            startInfo.ArgumentList.Add($"{limits.MemorySwapMb.Value}m");
        }
        if (limits.PidsLimit is not null)
        {
            startInfo.ArgumentList.Add("--pids-limit");
            startInfo.ArgumentList.Add(limits.PidsLimit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (limits.CpuLimit is not null)
        {
            startInfo.ArgumentList.Add("--cpus");
            startInfo.ArgumentList.Add(limits.CpuLimit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static void AddAllowedModelRouterEnvironment(ProcessStartInfo startInfo)
    {
        foreach (var key in new[] { "OPENAI_API_KEY", "OPENROUTER_API_KEY", "NVIDIA_API_KEY", "OPENAI_BASE_URL" })
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(value)) continue;
            startInfo.Environment[key] = value;
            startInfo.ArgumentList.Add("--env");
            startInfo.ArgumentList.Add($"{key}={value}");
        }
    }

    public static bool IsSafeDockerImage(string image)
    {
        if (string.IsNullOrWhiteSpace(image) || image.Length > 200) return false;
        return image.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '/' or '_' or '-' or ':')
            && !image.Contains("..", StringComparison.Ordinal)
            && !image.StartsWith("-", StringComparison.Ordinal)
            && !image.Contains(" ", StringComparison.Ordinal);
    }

    private static IEnumerable<string> SplitArguments(string template)
    {
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '\"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    private WorkspaceInfo ResolveWorkspace(string workspaceId)
    {
        if (!PathSafety.IsSafeRelativePath(workspaceId)) throw new InvalidOperationException("WorkspacePathRejected");
        var dataRoot = Path.GetFullPath(config.DataRoot);
        if (Directory.Exists(dataRoot)) RejectReparsePoints(dataRoot);
        var usersRoot = Path.GetFullPath(Path.Combine(dataRoot, "users"));
        EnsureInside(dataRoot, usersRoot, "WorkspacePathRejected");
        var root = Path.GetFullPath(Path.Combine(usersRoot, workspaceId));
        var workspace = Path.GetFullPath(Path.Combine(root, "workspace"));
        EnsureInside(usersRoot, root, "WorkspacePathRejected");
        EnsureInside(root, workspace, "WorkspacePathRejected");
        if (Directory.Exists(root)) RejectReparsePoints(root);
        Directory.CreateDirectory(root);
        foreach (var child in new[] { "workspace", "memory", "automations", "artifacts", "temp", "metadata", "hermes-home" })
        {
            var childPath = Path.GetFullPath(Path.Combine(root, child));
            EnsureInside(root, childPath, "WorkspacePathRejected");
            if (Directory.Exists(childPath)) RejectReparsePoints(childPath);
            Directory.CreateDirectory(childPath);
        }
        var hermesHome = Path.GetFullPath(Path.Combine(root, "hermes-home"));
        EnsureInside(root, hermesHome, "WorkspacePathRejected");
        RejectReparsePoints(root);
        return new WorkspaceInfo(root, workspace, hermesHome);
    }

    private async Task<string?> MonitorQuotaAsync(Process process, string root, long quotaBytes, long minimumFreeBytes, CancellationToken cancellationToken)
    {
        using var watcher = CreateWorkspaceWatcher(root);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (watcher is not null)
        {
            FileSystemEventHandler onChanged = (_, _) => changed.TrySetResult();
            RenamedEventHandler onRenamed = (_, _) => changed.TrySetResult();
            ErrorEventHandler onError = (_, _) => changed.TrySetResult();
            watcher.Created += onChanged;
            watcher.Changed += onChanged;
            watcher.Deleted += onChanged;
            watcher.Renamed += onRenamed;
            watcher.Error += onError;
            watcher.EnableRaisingEvents = true;
        }

        while (!cancellationToken.IsCancellationRequested && !process.HasExited)
        {
            var violation = CheckStorageLimits(root, quotaBytes, minimumFreeBytes);
            if (violation is not null)
            {
                KillProcessTree(process);
                try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
                return violation;
            }

            var delay = Task.Delay(Math.Max(10, config.QuotaPollMilliseconds), cancellationToken);
            var completed = await Task.WhenAny(delay, changed.Task);
            if (completed == changed.Task) changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        return null;
    }

    private static FileSystemWatcher? CreateWorkspaceWatcher(string root)
    {
        try
        {
            return new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes | NotifyFilters.Size | NotifyFilters.LastWrite
            };
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> ObserveMonitorAsync(Task<string?> monitorTask)
    {
        try { return await monitorTask; }
        catch (OperationCanceledException) { return null; }
    }

    private void ValidateWorkspaceForProcessStart(WorkspaceInfo workspace, long quotaBytes, long minimumFreeBytes)
    {
        EnsureInside(Path.GetFullPath(config.DataRoot), workspace.RootPath, "WorkspacePathRejected");
        EnsureInside(workspace.RootPath, workspace.WorkspacePath, "WorkspacePathRejected");
        RejectReparsePoints(workspace.RootPath);
        var violation = CheckStorageLimits(workspace.RootPath, quotaBytes, minimumFreeBytes);
        if (violation is not null) throw new InvalidOperationException(violation);
    }

    private static string? CheckStorageLimits(string root, long quotaBytes, long minimumFreeBytes)
    {
        long usage;
        try { usage = MeasureUserStorage(root); }
        catch (InvalidOperationException ex) { return ex.Message; }
        if (quotaBytes > 0 && usage > quotaBytes) return "StorageQuotaExceeded";
        if (AvailableBytes(root) < minimumFreeBytes) return "StorageUnavailable";
        return null;
    }

    private long EffectiveQuotaBytes(long leaseQuotaBytes)
    {
        var configured = config.DefaultStorageQuotaBytes > 0 ? config.DefaultStorageQuotaBytes : WorkerConfig.FallbackStorageQuotaBytes;
        if (leaseQuotaBytes <= 0) return configured;
        return Math.Min(leaseQuotaBytes, configured);
    }

    private long EffectiveMinimumFreeBytes()
        => config.MinimumFreeBytes > 0 ? config.MinimumFreeBytes : WorkerConfig.FallbackMinimumFreeBytes;

    private static long? SafeMeasure(string root)
    {
        try { return MeasureUserStorage(root); }
        catch { return null; }
    }

    private static long MeasureUserStorage(string root)
    {
        var total = 0L;
        var stack = new Stack<DirectoryInfo>();
        var rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists) return 0;
        if (IsReparsePoint(rootInfo)) throw new InvalidOperationException("WorkspaceReparsePointRejected");
        stack.Push(rootInfo);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var entry in current.EnumerateFileSystemInfos())
            {
                if (IsReparsePoint(entry)) throw new InvalidOperationException("WorkspaceReparsePointRejected");
                if (entry is FileInfo file) total += file.Length;
                else if (entry is DirectoryInfo directory) stack.Push(directory);
            }
        }
        return total;
    }

    private static void RejectReparsePoints(string root)
    {
        _ = MeasureUserStorage(root);
    }

    private static bool IsReparsePoint(FileSystemInfo info) => (info.Attributes & FileAttributes.ReparsePoint) != 0;

    private static void EnsureInside(string root, string target, string errorCode)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedTarget = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!normalizedTarget.Equals(normalizedRoot, comparison) && !normalizedTarget.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison)) throw new InvalidOperationException(errorCode);
    }

    private static long AvailableBytes(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        return string.IsNullOrWhiteSpace(root) ? 0 : new DriveInfo(root).AvailableFreeSpace;
    }

    private static HermesActionProposal? TryCreateDeviceActionProposal(string hermesOutput)
    {
        var call = ParseHermesToolCall(hermesOutput);
        if (call is null || !IsProposableDeviceTool(call.ToolName) || !AreProposalArgumentsSafe(call.Arguments)) return null;
        return new HermesActionProposal("device_action_proposal", call.ToolName, call.Arguments, "approval_required", $"{call.ToolName} eylemi için kullanıcı onayı gerekli.");
    }

    private static bool IsProposableDeviceTool(string toolName) =>
        toolName is "jarvis_open_app" or "jarvis_open_file" or "jarvis_create_folder" or "jarvis_add_note" or "jarvis_lock_screen" or "jarvis_write_document" or "pardus_set_theme" or "pardus_set_wallpaper";

    private static bool AreProposalArgumentsSafe(IReadOnlyDictionary<string, string> arguments)
    {
        if (arguments.Count > 8 || arguments.Any(pair => pair.Key.Length is 0 or > 64 || pair.Value.Length > 4096)) return false;
        return arguments.TryGetValue("mode", out var mode)
            ? mode is "dark" or "light"
            : arguments.TryGetValue("color", out var color)
                ? color is "black" or "#000000"
                : true;
    }


    public static HermesToolCall? ParseHermesToolCall(string output)
    {
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal)) continue;
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                var root = document.RootElement;
                if (!root.TryGetProperty("tool", out var toolElement) && !root.TryGetProperty("toolName", out toolElement)) continue;
                var toolName = toolElement.GetString();
                if (string.IsNullOrWhiteSpace(toolName)) continue;
                var arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (root.TryGetProperty("arguments", out var argsElement) && argsElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in argsElement.EnumerateObject()) arguments[property.Name] = property.Value.ToString();
                }
                return new HermesToolCall(toolName, arguments);
            }
            catch (JsonException) { }
        }
        return null;
    }

    private static WorkerCompleteJobRequest CompleteFailure(string errorCode, bool retryable, WorkerJobLeaseDto job, long? storageUsedBytes)
        => new(false, null, errorCode, retryable, CountTokens(job.Input), 0, storageUsedBytes);

    private static async Task<BoundedReadResult> ReadBoundedAsync(StreamReader reader, int maxChars, Process process, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(maxChars, 4096));
        var buffer = new char[Math.Min(4096, Math.Max(1, maxChars))];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0) return new BoundedReadResult(builder.ToString(), false);
            if (builder.Length + read > maxChars)
            {
                var allowed = Math.Max(0, maxChars - builder.Length);
                if (allowed > 0) builder.Append(buffer, 0, allowed);
                KillProcessTree(process);
                return new BoundedReadResult(builder.ToString(), true);
            }
            builder.Append(buffer, 0, read);
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private static int CountTokens(string? text) => Math.Max(1, (text ?? string.Empty).Length / 4);
}

public sealed record WorkerConfig(string ServerBaseUrl, string WorkerId, string ServiceToken, string DataRoot, string? HermesExecutablePath, int LeaseSeconds, int IdlePollSeconds, int ErrorDelaySeconds, int MaxStdoutBytes, int MaxStderrBytes, long MinimumFreeBytes = WorkerConfig.FallbackMinimumFreeBytes, int QuotaPollMilliseconds = 500, long DefaultStorageQuotaBytes = WorkerConfig.FallbackStorageQuotaBytes, string? HermesArgumentsTemplate = null, string HermesInputMode = "args", string HermesProbeArgumentsTemplate = "--version", string? HermesProbeExpectedText = "hermes", int HermesProbeRequiredExitCode = 0, string LocalAgentBaseUrl = "http://127.0.0.1:47891", string? LocalAgentSecret = null, string? HermesConfigSeedPath = null, string? HermesEnvSeedPath = null, bool RequirePrivateServerEndpoint = false, string HermesExecutionMode = "process", string? DockerCliPath = null, string? DockerHermesImage = null, int? DockerMemoryLimitMb = null, int? DockerMemorySwapLimitMb = null, int? DockerPidsLimit = null, decimal? DockerCpuLimit = null)
{
    public const long FallbackMinimumFreeBytes = 1073741824;
    public const long FallbackStorageQuotaBytes = 5L * 1024 * 1024 * 1024;

    public bool IsDockerExecutionMode => HermesExecutionMode.Equals("docker", StringComparison.OrdinalIgnoreCase);

    public DockerResourceLimits? ResolveDockerResourceLimits(int runtimeMemoryLimitMb)
    {
        if (!IsDockerExecutionMode) return null;
        if (runtimeMemoryLimitMb is <= 0 or > 512) throw new InvalidOperationException("RuntimeMemoryLimitMbInvalid");
        if (DockerMemoryLimitMb is <= 0 or > 512) throw new InvalidOperationException("DockerMemoryLimitMbInvalid");
        if (DockerMemorySwapLimitMb is <= 0 or > 512) throw new InvalidOperationException("DockerMemorySwapLimitMbInvalid");
        if (DockerPidsLimit is <= 0) throw new InvalidOperationException("DockerPidsLimitInvalid");
        if (DockerCpuLimit is <= 0) throw new InvalidOperationException("DockerCpuLimitInvalid");

        var memoryMb = Math.Min(runtimeMemoryLimitMb, DockerMemoryLimitMb ?? runtimeMemoryLimitMb);
        if (DockerMemorySwapLimitMb is not null && DockerMemorySwapLimitMb.Value < memoryMb) throw new InvalidOperationException("DockerMemorySwapLimitMbInvalid");
        return new DockerResourceLimits(memoryMb, DockerMemorySwapLimitMb, DockerPidsLimit, DockerCpuLimit);
    }

    public static bool IsValidHermesInputMode(string? inputMode)
        => inputMode is not null && (inputMode.Equals("args", StringComparison.OrdinalIgnoreCase) || inputMode.Equals("stdin", StringComparison.OrdinalIgnoreCase));

    public static bool IsValidHermesExecutionMode(string? executionMode)
        => string.Equals(executionMode, "docker", StringComparison.OrdinalIgnoreCase);

    public static string ParseHermesExecutionMode(string? executionMode)
    {
        var mode = string.IsNullOrWhiteSpace(executionMode) ? "docker" : executionMode.Trim().ToLowerInvariant();
        if (!IsValidHermesExecutionMode(mode)) throw new InvalidOperationException("VORTEX_HERMES_EXECUTION_MODE must be 'docker'.");
        return mode;
    }

    public static bool IsPrivateServerEndpoint(string? serverBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(serverBaseUrl) || !Uri.TryCreate(serverBaseUrl, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return false;
        if (IPAddress.TryParse(uri.Host, out var address)) return IsPrivateAddress(address);
        return uri.Host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127);
        }
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.GetAddressBytes()[0] == 0xfc || address.GetAddressBytes()[0] == 0xfd;
        }
        return false;
    }

    public static WorkerConfig FromEnvironment(string[] args)
    {
        var server = Read("VORTEX_SERVER_URL", args, "--server") ?? "https://vortex.example.invalid";
        var workerId = Read("VORTEX_WORKER_ID", args, "--worker-id") ?? "laptop-hermes-worker";
        var token = ReadSecret("VORTEX_WORKER_TOKEN") ?? throw new InvalidOperationException("VORTEX_WORKER_TOKEN zorunludur; command-line --token is not accepted.");
        var dataRoot = Read("VORTEX_WORKER_DATA", args, "--data") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VortexAI", "worker-data");
        var hermes = Read("HERMES_EXECUTABLE_PATH", args, "--hermes");
        var maxStdout = ReadInt("Worker:MaxStdoutBytes", "VORTEX_WORKER_MAX_STDOUT_BYTES", args, "--max-stdout-bytes", 65536);
        var maxStderr = ReadInt("Worker:MaxStderrBytes", "VORTEX_WORKER_MAX_STDERR_BYTES", args, "--max-stderr-bytes", 32768);
        var minimumFree = ReadLong("Worker:MinimumFreeBytes", "VORTEX_WORKER_MIN_FREE_BYTES", args, "--min-free-bytes", FallbackMinimumFreeBytes);
        var quotaPoll = ReadInt("Worker:QuotaPollMilliseconds", "VORTEX_WORKER_QUOTA_POLL_MS", args, "--quota-poll-ms", 500);
        var defaultQuota = ReadLong("Worker:DefaultStorageQuotaBytes", "VORTEX_WORKER_DEFAULT_STORAGE_QUOTA_BYTES", args, "--default-storage-quota-bytes", FallbackStorageQuotaBytes);
        var hermesArgumentsTemplate = Read("HERMES_ARGUMENTS_TEMPLATE", args, "--hermes-args-template");
        var hermesInputMode = Read("HERMES_INPUT_MODE", args, "--hermes-input-mode") ?? "args";
        var hermesProbeArgumentsTemplate = Read("HERMES_PROBE_ARGUMENTS_TEMPLATE", args, "--hermes-probe-args-template") ?? "--version";
        var hermesProbeExpectedText = Read("HERMES_PROBE_EXPECTED_TEXT", args, "--hermes-probe-expected-text") ?? "hermes";
        var hermesProbeRequiredExitCode = ReadInt("Hermes:ProbeRequiredExitCode", "HERMES_PROBE_REQUIRED_EXIT_CODE", args, "--hermes-probe-required-exit-code", 0, allowZero: true);
        var localAgentBaseUrl = Read("VORTEX_LOCAL_AGENT_URL", args, "--local-agent-url") ?? "http://127.0.0.1:47891";
        var localAgentSecret = ReadSecret("VORTEX_LOCAL_AGENT_SECRET");
        var hermesConfigSeedPath = Read("HERMES_CONFIG_SEED_PATH", args, "--hermes-config-seed");
        var hermesEnvSeedPath = Read("HERMES_ENV_SEED_PATH", args, "--hermes-env-seed");
        var requirePrivateServerEndpoint = ReadBool("VORTEX_REQUIRE_PRIVATE_SERVER_ENDPOINT", args, "--require-private-server-endpoint", false);
        var hermesExecutionMode = ParseHermesExecutionMode(Read("VORTEX_HERMES_EXECUTION_MODE", args, "--hermes-execution-mode"));
        var dockerCliPath = Read("VORTEX_DOCKER_CLI_PATH", args, "--docker-cli");
        var dockerHermesImage = Read("VORTEX_DOCKER_HERMES_IMAGE", args, "--docker-hermes-image");
        var dockerMemoryLimitMb = ReadOptionalPositiveInt("VORTEX_DOCKER_MEMORY_LIMIT_MB", args, "--docker-memory-limit-mb");
        var dockerMemorySwapLimitMb = ReadOptionalPositiveInt("VORTEX_DOCKER_MEMORY_SWAP_LIMIT_MB", args, "--docker-memory-swap-limit-mb");
        var dockerPidsLimit = ReadOptionalPositiveInt("VORTEX_DOCKER_PIDS_LIMIT", args, "--docker-pids-limit");
        var dockerCpuLimit = ReadOptionalPositiveDecimal("VORTEX_DOCKER_CPU_LIMIT", args, "--docker-cpu-limit");
        if (!string.IsNullOrWhiteSpace(dockerHermesImage) && !HermesRuntime.IsSafeDockerImage(dockerHermesImage)) throw new InvalidOperationException("VORTEX_DOCKER_HERMES_IMAGE is invalid.");
        if (dockerMemoryLimitMb is > 512 || dockerMemorySwapLimitMb is > 512) throw new InvalidOperationException("Docker memory limits must not exceed 512 MiB.");
        if (dockerMemoryLimitMb is not null && dockerMemorySwapLimitMb is not null && dockerMemorySwapLimitMb < dockerMemoryLimitMb) throw new InvalidOperationException("Docker memory-swap limit cannot be lower than Docker memory limit.");
        if (requirePrivateServerEndpoint && !IsPrivateServerEndpoint(server)) throw new InvalidOperationException("VORTEX_SERVER_URL must be a private/Tailscale endpoint when VORTEX_REQUIRE_PRIVATE_SERVER_ENDPOINT is enabled.");
        Directory.CreateDirectory(dataRoot);
        return new WorkerConfig(server, workerId, token, dataRoot, hermes, 60, 3, 10, maxStdout, maxStderr, minimumFree, quotaPoll, defaultQuota, hermesArgumentsTemplate, hermesInputMode, hermesProbeArgumentsTemplate, hermesProbeExpectedText, hermesProbeRequiredExitCode, localAgentBaseUrl, localAgentSecret, hermesConfigSeedPath, hermesEnvSeedPath, requirePrivateServerEndpoint, hermesExecutionMode, dockerCliPath, dockerHermesImage, dockerMemoryLimitMb, dockerMemorySwapLimitMb, dockerPidsLimit, dockerCpuLimit);
    }

    private static string? Read(string env, string[] args, string flag)
    {
        var value = Environment.GetEnvironmentVariable(env);
        if (!string.IsNullOrWhiteSpace(value)) return value;
        for (var i = 0; i < args.Length - 1; i++) if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    private static string? ReadSecret(string env)
    {
        var value = Environment.GetEnvironmentVariable(env);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int ReadInt(string configName, string env, string[] args, string flag, int fallback, bool allowZero = false)
    {
        var value = Read(env, args, flag) ?? Environment.GetEnvironmentVariable(configName);
        return int.TryParse(value, out var parsed) && (parsed > 0 || (allowZero && parsed == 0)) ? parsed : fallback;
    }

    private static long ReadLong(string configName, string env, string[] args, string flag, long fallback)
    {
        var value = Read(env, args, flag) ?? Environment.GetEnvironmentVariable(configName);
        return long.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
    }

    private static int? ReadOptionalPositiveInt(string env, string[] args, string flag)
    {
        var value = Read(env, args, flag);
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!int.TryParse(value, out var parsed) || parsed <= 0) throw new InvalidOperationException($"{env} must be a positive integer.");
        return parsed;
    }

    private static decimal? ReadOptionalPositiveDecimal(string env, string[] args, string flag)
    {
        var value = Read(env, args, flag);
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed <= 0) throw new InvalidOperationException($"{env} must be a positive invariant decimal.");
        return parsed;
    }

    private static bool ReadBool(string env, string[] args, string flag, bool fallback)
    {
        var envValue = Environment.GetEnvironmentVariable(env);
        if (!string.IsNullOrWhiteSpace(envValue)) return ParseBool(envValue);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals(flag, StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) return true;
            return ParseBool(args[i + 1]);
        }
        return fallback;
    }

    private static bool ParseBool(string value)
    {
        if (bool.TryParse(value, out var parsed)) return parsed;
        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
}

public static class WorkerLog
{
    private static readonly object Gate = new();
    private static string? _path;

    public static void Initialize(string dataRoot)
    {
        var directory = Path.Combine(dataRoot, "logs");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "worker.log");
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        var line = $"{DateTimeOffset.UtcNow:O} [{level}] {message}";
        if (exception is not null) line += $" | {exception.GetType().Name}: {exception.Message}";
        Console.WriteLine(line);
        if (string.IsNullOrWhiteSpace(_path)) return;
        try
        {
            lock (Gate) File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // Logging must not interrupt queued job processing.
        }
    }
}

public static class HostIdentity
{
    [DllImport("libc", SetLastError = true)]
    private static extern uint getuid();

    [DllImport("libc", SetLastError = true)]
    private static extern uint getgid();

    public static bool TryGetCurrent(out uint uid, out uint gid)
    {
        uid = 0;
        gid = 0;
        if (OperatingSystem.IsWindows()) return false;
        try
        {
            uid = getuid();
            gid = getgid();
            return uid != 0 && gid != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}

public sealed record DockerResourceLimits(int MemoryMb, int? MemorySwapMb, int? PidsLimit, decimal? CpuLimit);
public sealed record BoundedReadResult(string Text, bool Exceeded);
public sealed record WorkspaceInfo(string RootPath, string WorkspacePath, string HermesHomePath);
public sealed record HermesProbeResult(bool IsReady, string? ResolvedCommand, string? Message, string? VersionEvidence);
public sealed record DockerReadiness(bool IsReady, string? ResolvedDockerCli, string? Message);
internal sealed record ProbeProcessResult(bool Started, int ExitCode, string Stdout, string Stderr);

internal static class WorkerJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}




