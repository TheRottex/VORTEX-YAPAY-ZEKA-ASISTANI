using System.Diagnostics;

namespace Vortex.Desktop.Services;

public sealed class ProcessDesktopSecretToolRunner : IDesktopSecretToolRunner
{
    public async Task<string?> LookupAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Start("lookup", key, redirectInput: false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task StoreAsync(string key, string value, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Start("store", key, redirectInput: true);
            await process.StandardInput.WriteAsync(value.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Fallback: ignore if keyring/secret-tool is not available
        }
    }

    public async Task ClearAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Start("clear", key, redirectInput: false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Ignore if keyring/secret-tool is not available
        }
    }

    private static Process Start(string operation, string key, bool redirectInput)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "secret-tool",
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(operation);
        if (operation == "store") startInfo.ArgumentList.Add($"--label=VortexAI-{key}");
        startInfo.ArgumentList.Add("app");
        startInfo.ArgumentList.Add("vortex-desktop");
        startInfo.ArgumentList.Add("key");
        startInfo.ArgumentList.Add(key);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("secret-tool not available.");
    }
}
