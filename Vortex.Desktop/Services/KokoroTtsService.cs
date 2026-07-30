using System.Diagnostics;

namespace Vortex.Desktop.Services;

public sealed record KokoroTtsStatus(bool IsAvailable, string Detail, string? CommandPath);

public sealed class KokoroTtsService
{
    public KokoroTtsStatus GetStatus()
    {
        var configured = Environment.GetEnvironmentVariable("VORTEX_KOKORO_COMMAND");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return new KokoroTtsStatus(true, "Kokoro yerel ses motoru hazır.", configured);
        }

        // Prefer Desktop-embedded installer wrapper under %AppData%/VortexAI/kokoro
        var wrapper = KokoroInstallService.GetWrapperPath();
        if (File.Exists(wrapper))
        {
            return new KokoroTtsStatus(true, "Kokoro yerel ses motoru hazır (kurulu sarmalayıcı).", wrapper);
        }

        var candidates = OperatingSystem.IsWindows()
            ? new[] { "kokoro.exe", "kokoro-cli.exe", "kokoro-cli.cmd" }
            : new[] { "kokoro", "kokoro-cli" };
        foreach (var candidate in candidates)
        {
            var resolved = ResolveOnPath(candidate);
            if (resolved is not null) return new KokoroTtsStatus(true, "Kokoro yerel ses motoru hazır.", resolved);
        }

        return new KokoroTtsStatus(false, "Kokoro bulunamadı. Çevrimdışı ses için kurulum gerekir; sistem sesi yedek seçenek olarak kullanılacak.", null);
    }

    public async Task<bool> TrySynthesizeAsync(string text, string outputPath, CancellationToken cancellationToken)
    {
        var status = GetStatus();
        if (!status.IsAvailable || string.IsNullOrWhiteSpace(status.CommandPath)) return false;

        // Embedded install: run python + vortex_kokoro_tts.py with ArgumentList (no shell).
        if (IsEmbeddedWrapper(status.CommandPath) &&
            TryBuildEmbeddedStartInfo(text, outputPath, out var embedded))
        {
            return await RunSynthesizeProcessAsync(embedded, outputPath, cancellationToken);
        }

        var startInfo = new ProcessStartInfo(status.CommandPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("--text");
        startInfo.ArgumentList.Add(text);
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(outputPath);

        return await RunSynthesizeProcessAsync(startInfo, outputPath, cancellationToken);
    }

    private static bool IsEmbeddedWrapper(string commandPath)
    {
        try
        {
            var wrapper = KokoroInstallService.GetWrapperPath();
            return string.Equals(
                Path.GetFullPath(commandPath),
                Path.GetFullPath(wrapper),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryBuildEmbeddedStartInfo(string text, string outputPath, out ProcessStartInfo startInfo)
    {
        startInfo = null!;
        var script = KokoroInstallService.GetPythonScriptPath();
        if (!File.Exists(script)) return false;

        string? python = null;
        try
        {
            var stored = KokoroInstallService.GetPythonPathFile();
            if (File.Exists(stored))
            {
                python = File.ReadAllText(stored).Trim();
            }
        }
        catch
        {
            // ignore
        }

        if (string.IsNullOrWhiteSpace(python))
        {
            var compatible = KokoroInstallService.ResolveCompatiblePython();
            python = compatible?.ExecutablePath ?? compatible?.FileName ?? KokoroInstallService.ResolvePython();
        }

        if (string.IsNullOrWhiteSpace(python) || !File.Exists(python) && !KokoroInstallService.IsPyLauncher(python))
        {
            // File.Exists may fail for py on some systems; still try launcher name
            if (string.IsNullOrWhiteSpace(python)) return false;
        }

        // Prefer CreatePythonStartInfo so py launcher gets a compatible -3.x arg when needed.
        startInfo = KokoroInstallService.CreatePythonStartInfo(python);
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add("--text");
        startInfo.ArgumentList.Add(text);
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(outputPath);

        var site = KokoroInstallService.GetSitePackagesDirectory();
        if (Directory.Exists(site))
        {
            var existing = Environment.GetEnvironmentVariable("PYTHONPATH");
            startInfo.Environment["PYTHONPATH"] = string.IsNullOrWhiteSpace(existing)
                ? site
                : site + Path.PathSeparator + existing;
        }

        return true;
    }

    private static async Task<bool> RunSynthesizeProcessAsync(
        ProcessStartInfo startInfo,
        string outputPath,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return false;
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static string? ResolveOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
