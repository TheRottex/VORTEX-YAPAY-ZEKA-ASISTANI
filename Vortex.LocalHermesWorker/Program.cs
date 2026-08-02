using System.Diagnostics;

return await LocalHermesWorkerManager.RunAsync(args);

internal static class LocalHermesWorkerManager
{
    private static readonly string[] Commands = ["preflight", "status", "start", "stop", "restart", "logs", "test", "pair", "install", "update", "rollback", "revoke", "uninstall"];

    public static async Task<int> RunAsync(string[] args)
    {
        var command = args.FirstOrDefault()?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(command) || command is "help" or "--help" or "-h")
        {
            PrintUsage();
            return 0;
        }

        if (!Commands.Contains(command, StringComparer.Ordinal))
        {
            Console.Error.WriteLine("Bilinmeyen komut. Yardım için 'help' çalıştırın.");
            return 2;
        }

        if (command == "preflight") return await PreflightAsync();
        if (command == "status") return await RunWslAsync("systemctl --user status vortex-hermes-worker --no-pager -l", false);
        if (command == "logs") return await RunWslAsync("journalctl --user -u vortex-hermes-worker -n 100 --no-pager", false);
        if (command == "start") return await RunWslAsync("systemctl --user start vortex-hermes-worker", true);
        if (command == "stop") return await RunWslAsync("systemctl --user stop vortex-hermes-worker", true);
        if (command == "restart") return await RunWslAsync("systemctl --user restart vortex-hermes-worker", true);

        Console.Error.WriteLine($"'{command}' henüz güvenli biçimde otomatikleştirilmedi. README'deki deneysel sınıra bakın.");
        return 3;
    }

    private static async Task<int> PreflightAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Bu manager Windows üzerinden WSL2 yönetimi için tasarlanmıştır.");
            return 1;
        }

        var checks = new[]
        {
            ("WSL", "--status"),
            ("Dağıtımlar", "-l -v"),
            ("Docker Desktop", "docker version --format {{.Server.Version}}")
        };
        var failed = false;
        foreach (var (name, arguments) in checks)
        {
            var executable = name == "Docker Desktop" ? "docker" : "wsl.exe";
            var code = await RunAsync(executable, arguments, false);
            failed |= code != 0;
        }
        return failed ? 1 : 0;
    }

    private static Task<int> RunWslAsync(string command, bool changesRuntime)
    {
        if (changesRuntime)
        {
            Console.WriteLine("Bu komut Worker runtime durumunu değiştirir.");
        }
        return RunAsync("wsl.exe", $"-- sh -lc \"{command.Replace("\"", "\\\"")}\"", true);
    }

    private static async Task<int> RunAsync(string fileName, string arguments, bool inheritOutput)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = !inheritOutput,
                RedirectStandardError = !inheritOutput
            }
        };
        try
        {
            process.Start();
            if (!inheritOutput)
            {
                var stdout = await process.StandardOutput.ReadToEndAsync();
                var stderr = await process.StandardError.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout.TrimEnd());
                if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr.TrimEnd());
            }
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"Çalıştırılamadı: {fileName}. {ex.Message}");
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Vortex.LocalHermesWorker");
        Console.WriteLine("Kullanım: dotnet run --project Vortex.LocalHermesWorker -- <komut>");
        Console.WriteLine("Komutlar: preflight, status, start, stop, restart, logs");
        Console.WriteLine("Planlanan güvenli yaşam döngüsü komutları: pair, install, test, update, rollback, revoke, uninstall");
    }
}
