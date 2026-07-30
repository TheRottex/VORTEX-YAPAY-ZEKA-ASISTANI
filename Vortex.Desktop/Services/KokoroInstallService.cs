using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Vortex.Desktop.Services;

public sealed record KokoroInstallProgress(string Line, bool IsError = false);

/// <summary>
/// Resolved Python interpreter usable for Kokoro install/synthesis.
/// FileName is the executable; PyVersionArg is e.g. "-3.12" for the Windows py launcher, or null.
/// ExecutablePath is the real sys.executable when probed (preferred for storage/wrapper).
/// </summary>
public sealed record KokoroPythonResolution(
    string FileName,
    string? PyVersionArg,
    string DisplayVersion,
    string? ExecutablePath);

/// <summary>
/// Desktop-embedded Kokoro installer. Uses ProcessStartInfo.ArgumentList only (no shell injection).
/// Install root: %AppData%/VortexAI/kokoro (ApplicationData).
/// Requires Python &gt;= 3.10 and &lt; 3.14 (kokoro package constraint).
/// </summary>
public sealed class KokoroInstallService
{
    public const string DocsUrl = "https://github.com/hexgrad/kokoro";

    public const string IncompatiblePythonMessage =
        "Kokoro Python 3.10–3.13 ister; sistemde Python 3.14 bulundu. Python 3.12 kurun (python.org) veya 'py -3.12' ekleyin.";

    public static string GetInstallDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = AppContext.BaseDirectory;
        return Path.Combine(root, "VortexAI", "kokoro");
    }

    public static string GetSitePackagesDirectory() => Path.Combine(GetInstallDirectory(), "site");

    public static string GetWrapperPath()
    {
        var dir = GetInstallDirectory();
        return OperatingSystem.IsWindows()
            ? Path.Combine(dir, "kokoro-cli.cmd")
            : Path.Combine(dir, "kokoro-cli");
    }

    public static string GetPythonScriptPath() => Path.Combine(GetInstallDirectory(), "vortex_kokoro_tts.py");

    public static string GetPythonPathFile() => Path.Combine(GetInstallDirectory(), "python.path");

    /// <summary>
    /// True when wrapper + synthesis script exist under the install directory.
    /// Does not run network or heavy import checks (call VerifyPackageImportAsync for that).
    /// </summary>
    public bool IsInstalled()
    {
        return File.Exists(GetWrapperPath()) && File.Exists(GetPythonScriptPath());
    }

    /// <summary>
    /// Kokoro requires Python &gt;= 3.10 and &lt; 3.14.
    /// </summary>
    public static bool IsCompatiblePythonVersion(int major, int minor)
    {
        if (major != 3) return false;
        return minor >= 10 && minor < 14;
    }

    /// <summary>
    /// Parses "3.12", "3.12.7", etc. Returns false if not a valid major.minor.
    /// </summary>
    public static bool TryParsePythonVersion(string? text, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = Regex.Match(text.Trim(), @"^(?<maj>\d+)\.(?<min>\d+)");
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups["maj"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out major)) return false;
        if (!int.TryParse(m.Groups["min"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out minor)) return false;
        return true;
    }

    /// <summary>
    /// Resolves any Python on PATH (legacy helper). Prefer <see cref="ResolveCompatiblePython"/> for install.
    /// </summary>
    public static string? ResolvePython()
    {
        var compatible = ResolveCompatiblePython();
        if (compatible is not null)
        {
            return compatible.ExecutablePath ?? compatible.FileName;
        }

        // Fallback: any python for diagnostics only (install will still reject incompatible).
        foreach (var name in new[] { "python", "python3" })
        {
            var resolved = ResolveOnPath(name) ?? ResolveOnPath(name + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
            if (resolved is not null && LooksLikePython(resolved)) return resolved;
        }

        if (OperatingSystem.IsWindows())
        {
            var py = ResolveOnPath("py.exe") ?? ResolveOnPath("py");
            if (py is not null) return py;
        }

        return null;
    }

    /// <summary>
    /// Prefer highest compatible Python (3.13 &gt; 3.12 &gt; 3.11 &gt; 3.10).
    /// Windows: try py -3.13 … -3.10 first, then versioned PATH names, then python3/python.
    /// </summary>
    public static KokoroPythonResolution? ResolveCompatiblePython()
    {
        var candidates = BuildPythonCandidates();
        KokoroPythonResolution? best = null;
        var bestMinor = -1;
        string? sawIncompatible314 = null;

        foreach (var (fileName, pyVersionArg) in candidates)
        {
            if (string.IsNullOrWhiteSpace(fileName)) continue;
            if (!IsPyLauncher(fileName) && !File.Exists(fileName) && ResolveOnPath(fileName) is null)
            {
                // bare names may still resolve via Process PATH
            }

            var probe = ProbePythonVersion(fileName, pyVersionArg);
            if (probe is null) continue;

            var (versionText, executable) = probe.Value;
            if (!TryParsePythonVersion(versionText, out var major, out var minor)) continue;

            if (!IsCompatiblePythonVersion(major, minor))
            {
                if (major == 3 && minor >= 14)
                {
                    sawIncompatible314 ??= versionText;
                }

                continue;
            }

            if (minor > bestMinor)
            {
                bestMinor = minor;
                best = new KokoroPythonResolution(
                    fileName,
                    pyVersionArg,
                    $"{major}.{minor}",
                    string.IsNullOrWhiteSpace(executable) ? null : executable);
            }
        }

        // Stash last 3.14 sighting is only used by InstallAsync messaging when best is null.
        _ = sawIncompatible314;
        return best;
    }

    /// <summary>
    /// Probes whether any Python was found and whether only incompatible 3.14+ exists.
    /// </summary>
    public static (bool AnyPythonFound, bool OnlyIncompatible314, string? SampleVersion) DiagnosePythonAvailability()
    {
        var candidates = BuildPythonCandidates();
        var any = false;
        var compatible = false;
        string? sample314 = null;
        string? sampleAny = null;

        foreach (var (fileName, pyVersionArg) in candidates)
        {
            var probe = ProbePythonVersion(fileName, pyVersionArg);
            if (probe is null) continue;
            any = true;
            var (versionText, _) = probe.Value;
            sampleAny ??= versionText;
            if (!TryParsePythonVersion(versionText, out var major, out var minor)) continue;
            if (IsCompatiblePythonVersion(major, minor))
            {
                compatible = true;
            }
            else if (major == 3 && minor >= 14)
            {
                sample314 ??= versionText;
            }
        }

        return (any, any && !compatible && sample314 is not null, sample314 ?? sampleAny);
    }

    public static bool IsPyLauncher(string pythonPath)
    {
        var file = Path.GetFileNameWithoutExtension(pythonPath);
        return string.Equals(file, "py", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> InstallAsync(IProgress<KokoroInstallProgress>? progress, CancellationToken cancellationToken)
    {
        void Log(string line, bool error = false) => progress?.Report(new KokoroInstallProgress(line, error));

        Log("Kurulum başlıyor...");

        var resolution = ResolveCompatiblePython();
        if (resolution is null)
        {
            var (any, only314, sample) = DiagnosePythonAvailability();
            if (!any)
            {
                Log("Python bulunamadı. Lütfen Python 3.10–3.13 kurun ve PATH'e ekleyin (önerilen: 3.12).", error: true);
            }
            else if (only314 || (sample is not null && TryParsePythonVersion(sample, out var maj, out var min) && maj == 3 && min >= 14))
            {
                Log(IncompatiblePythonMessage, error: true);
                if (!string.IsNullOrWhiteSpace(sample))
                {
                    Log($"Algılanan sürüm: {sample}", error: true);
                }
            }
            else
            {
                Log(
                    "Uyumlu Python bulunamadı. Kokoro Python 3.10–3.13 ister. Python 3.12 kurun (python.org) veya 'py -3.12' ekleyin.",
                    error: true);
                if (!string.IsNullOrWhiteSpace(sample))
                {
                    Log($"Algılanan sürüm: {sample}", error: true);
                }
            }

            return false;
        }

        // Prefer real sys.executable for storage/wrapper when available.
        var pythonForStorage = !string.IsNullOrWhiteSpace(resolution.ExecutablePath)
            ? resolution.ExecutablePath!
            : resolution.FileName;

        Log($"Uyumlu Python seçildi: {resolution.DisplayVersion} ({DescribeResolution(resolution)})");

        var installDir = GetInstallDirectory();
        var siteDir = GetSitePackagesDirectory();
        try
        {
            Directory.CreateDirectory(installDir);
            Directory.CreateDirectory(siteDir);
        }
        catch (Exception ex)
        {
            Log($"Kurulum klasörü oluşturulamadı: {ex.Message}", error: true);
            return false;
        }

        Log($"Kurulum dizini: {installDir}");

        // Prefer PyPI package; fall back to git source if needed.
        var pipOk = await RunPipInstallAsync(resolution, siteDir, "kokoro", progress, cancellationToken).ConfigureAwait(false);
        if (!pipOk)
        {
            Log("PyPI 'kokoro' kurulumu başarısız; git kaynağı deneniyor...", error: true);
            pipOk = await RunPipInstallAsync(
                resolution,
                siteDir,
                "git+https://github.com/hexgrad/kokoro.git",
                progress,
                cancellationToken).ConfigureAwait(false);
        }

        if (!pipOk)
        {
            Log("pip kurulumu başarısız. İnternet bağlantısını ve Python/pip kurulumunu kontrol edin.", error: true);
            return false;
        }

        try
        {
            // Store real interpreter path (not bare "py") when we have sys.executable.
            await File.WriteAllTextAsync(GetPythonPathFile(), pythonForStorage, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(GetPythonScriptPath(), BuildPythonScript(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
            await WriteWrapperAsync(pythonForStorage, resolution, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"Sarmalayıcı yazılamadı: {ex.Message}", error: true);
            return false;
        }

        Log("Sarmalayıcı ve sentez betiği yazıldı.");

        var importOk = await VerifyPackageImportAsync(resolution, siteDir, progress, cancellationToken).ConfigureAwait(false);
        if (!importOk)
        {
            Log("Paket kuruldu ancak import doğrulanamadı. Ses sentezi başarısız olabilir.", error: true);
            // Still leave files in place; GetStatus can report available via wrapper presence.
        }

        if (!IsInstalled())
        {
            Log("Kurulum dosyaları eksik kaldı.", error: true);
            return false;
        }

        Log("Kokoro kurulumu tamamlandı.");
        return true;
    }

    public async Task<bool> VerifyPackageImportAsync(
        string? pythonPath,
        string? siteDir,
        IProgress<KokoroInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        KokoroPythonResolution? resolution = null;
        if (!string.IsNullOrWhiteSpace(pythonPath))
        {
            resolution = new KokoroPythonResolution(pythonPath, IsPyLauncher(pythonPath) ? "-3" : null, "?", null);
        }
        else
        {
            var stored = ReadStoredPython();
            if (!string.IsNullOrWhiteSpace(stored))
            {
                resolution = new KokoroPythonResolution(stored, IsPyLauncher(stored) ? "-3" : null, "?", null);
            }
            else
            {
                resolution = ResolveCompatiblePython();
            }
        }

        if (resolution is null) return false;
        return await VerifyPackageImportAsync(resolution, siteDir ?? GetSitePackagesDirectory(), progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> VerifyPackageImportAsync(
        KokoroPythonResolution resolution,
        string siteDir,
        IProgress<KokoroInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var code =
            "import sys\n" +
            $"sys.path.insert(0, r'{siteDir.Replace("'", "\\'")}')\n" +
            "ok=False\n" +
            "for m in ('kokoro','kokoro_onnx','kokoro.pipeline'):\n" +
            "  try:\n" +
            "    __import__(m.split('.')[0] if '.' in m else m)\n" +
            "    ok=True\n" +
            "    print('import_ok', m)\n" +
            "    break\n" +
            "  except Exception as e:\n" +
            "    print('import_fail', m, type(e).__name__)\n" +
            "sys.exit(0 if ok else 1)\n";

        var startInfo = CreatePythonStartInfo(resolution);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(code);
        startInfo.Environment["PYTHONPATH"] = PrependPath(siteDir, startInfo.Environment.TryGetValue("PYTHONPATH", out var existing) ? existing : null);

        var (exit, _, _) = await RunProcessAsync(startInfo, progress, cancellationToken).ConfigureAwait(false);
        return exit == 0;
    }

    private async Task<bool> RunPipInstallAsync(
        KokoroPythonResolution resolution,
        string siteDir,
        string packageSpec,
        IProgress<KokoroInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new KokoroInstallProgress($"pip install --target \"{siteDir}\" {packageSpec} (Python {resolution.DisplayVersion})"));

        var startInfo = CreatePythonStartInfo(resolution);
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add("pip");
        startInfo.ArgumentList.Add("install");
        startInfo.ArgumentList.Add("--upgrade");
        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add(siteDir);
        startInfo.ArgumentList.Add(packageSpec);

        var (exit, _, _) = await RunProcessAsync(startInfo, progress, cancellationToken).ConfigureAwait(false);
        return exit == 0;
    }

    private async Task WriteWrapperAsync(string pythonForStorage, KokoroPythonResolution resolution, CancellationToken cancellationToken)
    {
        var installDir = GetInstallDirectory();
        var scriptPath = GetPythonScriptPath();
        var siteDir = GetSitePackagesDirectory();
        var wrapperPath = GetWrapperPath();

        // Prefer stored real executable; if still py launcher, keep version arg from resolution.
        var usePy = IsPyLauncher(pythonForStorage);
        var pyArg = usePy
            ? (resolution.PyVersionArg ?? "-3")
            : null;

        if (OperatingSystem.IsWindows())
        {
            var pyPrefix = pyArg is not null ? " " + pyArg : string.Empty;
            var content =
                "@echo off\r\n" +
                "setlocal\r\n" +
                $"set \"VORTEX_KOKORO_HOME={installDir}\"\r\n" +
                $"set \"PYTHONPATH={siteDir};%PYTHONPATH%\"\r\n" +
                $"\"{pythonForStorage}\"{pyPrefix} \"{scriptPath}\" %*\r\n" +
                "exit /b %ERRORLEVEL%\r\n";
            await File.WriteAllTextAsync(wrapperPath, content, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var pyPrefix = pyArg is not null ? " " + pyArg : string.Empty;
            var content =
                "#!/usr/bin/env bash\n" +
                "set -euo pipefail\n" +
                $"export VORTEX_KOKORO_HOME=\"{installDir}\"\n" +
                $"export PYTHONPATH=\"{siteDir}${{PYTHONPATH:+:$PYTHONPATH}}\"\n" +
                $"exec \"{pythonForStorage}\"{pyPrefix} \"{scriptPath}\" \"$@\"\n";
            await File.WriteAllTextAsync(wrapperPath, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            try
            {
                // Best-effort chmod +x
                var chmod = new ProcessStartInfo("chmod")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                chmod.ArgumentList.Add("+x");
                chmod.ArgumentList.Add(wrapperPath);
                using var p = Process.Start(chmod);
                if (p is not null) await p.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // ignore chmod failures; user may still invoke via bash
            }
        }
    }

    private static string BuildPythonScript() =>
        """
        #!/usr/bin/env python3
        # Vortex-managed Kokoro TTS bridge. CLI: --text TEXT --output PATH
        # Best-effort: tries known package APIs; exits non-zero if synthesis is unavailable.
        from __future__ import annotations
        import argparse
        import sys
        import os

        def fail(msg: str, code: int = 1) -> None:
            print(msg, file=sys.stderr)
            raise SystemExit(code)

        def write_wav(path: str, audio, sample_rate: int = 24000) -> None:
            import wave
            import array
            # audio may be numpy array or list of floats [-1,1]
            try:
                import numpy as np
                arr = np.asarray(audio, dtype=np.float32).reshape(-1)
                pcm = (arr * 32767.0).clip(-32768, 32767).astype(np.int16)
                data = pcm.tobytes()
            except Exception:
                samples = array.array('h')
                for x in audio:
                    v = int(max(-1.0, min(1.0, float(x))) * 32767)
                    samples.append(v)
                data = samples.tobytes()
            with wave.open(path, 'wb') as wf:
                wf.setnchannels(1)
                wf.setsampwidth(2)
                wf.setframerate(int(sample_rate))
                wf.writeframes(data)

        def try_kokoro(text: str, output: str) -> bool:
            try:
                from kokoro import KPipeline  # type: ignore
            except Exception as e:
                print(f"kokoro.KPipeline import failed: {e}", file=sys.stderr)
                return False
            try:
                # lang_code 'a' = American English; Turkish may fall back poorly but keeps path honest
                pipeline = KPipeline(lang_code='a')
                voice = os.environ.get('VORTEX_KOKORO_VOICE', 'af_heart')
                generator = pipeline(text, voice=voice)
                chunks = []
                sr = 24000
                for item in generator:
                    # item may be (gs, ps, audio) or similar
                    if isinstance(item, (list, tuple)) and len(item) >= 3:
                        audio = item[2]
                    else:
                        audio = item
                    try:
                        import numpy as np
                        chunks.append(np.asarray(audio, dtype=np.float32).reshape(-1))
                    except Exception:
                        chunks.append(audio)
                if not chunks:
                    return False
                try:
                    import numpy as np
                    audio_out = np.concatenate(chunks)
                except Exception:
                    audio_out = []
                    for c in chunks:
                        audio_out.extend(list(c))
                write_wav(output, audio_out, sr)
                return os.path.isfile(output) and os.path.getsize(output) > 0
            except Exception as e:
                print(f"kokoro synthesis failed: {e}", file=sys.stderr)
                return False

        def try_kokoro_onnx(text: str, output: str) -> bool:
            try:
                from kokoro_onnx import Kokoro  # type: ignore
            except Exception as e:
                print(f"kokoro_onnx import failed: {e}", file=sys.stderr)
                return False
            try:
                model = os.environ.get('VORTEX_KOKORO_ONNX_MODEL')
                voices = os.environ.get('VORTEX_KOKORO_ONNX_VOICES')
                if not model or not voices:
                    print("kokoro_onnx requires VORTEX_KOKORO_ONNX_MODEL and VORTEX_KOKORO_ONNX_VOICES", file=sys.stderr)
                    return False
                k = Kokoro(model, voices)
                voice = os.environ.get('VORTEX_KOKORO_VOICE', 'af_sarah')
                samples, sample_rate = k.create(text, voice=voice)
                write_wav(output, samples, sample_rate)
                return os.path.isfile(output) and os.path.getsize(output) > 0
            except Exception as e:
                print(f"kokoro_onnx synthesis failed: {e}", file=sys.stderr)
                return False

        def main() -> None:
            p = argparse.ArgumentParser(description='Vortex Kokoro TTS bridge')
            p.add_argument('--text', required=True)
            p.add_argument('--output', required=True)
            args = p.parse_args()
            text = (args.text or '').strip()
            output = args.output
            if not text:
                fail('empty --text')
            os.makedirs(os.path.dirname(os.path.abspath(output)) or '.', exist_ok=True)
            if try_kokoro(text, output):
                print('ok kokoro')
                return
            if try_kokoro_onnx(text, output):
                print('ok kokoro_onnx')
                return
            fail('No working Kokoro synthesis backend. Package installed but API unavailable or models missing.')

        if __name__ == '__main__':
            main()
        """;

    /// <summary>
    /// Builds ProcessStartInfo for a resolved Python (supports py -3.12 style ArgumentList).
    /// </summary>
    public static ProcessStartInfo CreatePythonStartInfo(KokoroPythonResolution resolution)
    {
        var fileName = resolution.FileName;
        // If we have a real executable path, prefer it (no py version arg needed).
        if (!string.IsNullOrWhiteSpace(resolution.ExecutablePath) &&
            File.Exists(resolution.ExecutablePath) &&
            !IsPyLauncher(resolution.ExecutablePath))
        {
            fileName = resolution.ExecutablePath;
            var startInfo = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = false
            };
            return startInfo;
        }

        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = false
        };
        if (!string.IsNullOrWhiteSpace(resolution.PyVersionArg))
        {
            psi.ArgumentList.Add(resolution.PyVersionArg);
        }
        else if (IsPyLauncher(fileName))
        {
            psi.ArgumentList.Add("-3");
        }

        return psi;
    }

    /// <summary>
    /// Legacy CreatePythonStartInfo(string) used by KokoroTtsService for stored python paths.
    /// </summary>
    public static ProcessStartInfo CreatePythonStartInfo(string python)
    {
        var startInfo = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = false
        };
        if (IsPyLauncher(python))
        {
            // Prefer a compatible minor if possible via py -3.12 etc.; fall back to -3.
            var compatible = ResolveCompatiblePython();
            if (compatible?.PyVersionArg is not null && IsPyLauncher(compatible.FileName))
            {
                startInfo.ArgumentList.Add(compatible.PyVersionArg);
            }
            else
            {
                startInfo.ArgumentList.Add("-3");
            }
        }

        return startInfo;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(
        ProcessStartInfo startInfo,
        IProgress<KokoroInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                progress?.Report(new KokoroInstallProgress("İşlem başlatılamadı.", IsError: true));
                return (-1, string.Empty, "process_start_failed");
            }

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                stdout.AppendLine(e.Data);
                progress?.Report(new KokoroInstallProgress(e.Data));
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                stderr.AppendLine(e.Data);
                progress?.Report(new KokoroInstallProgress(e.Data, IsError: true));
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return (process.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new KokoroInstallProgress("Kurulum iptal edildi.", IsError: true));
            throw;
        }
        catch (Exception ex)
        {
            progress?.Report(new KokoroInstallProgress($"İşlem hatası: {ex.Message}", IsError: true));
            return (-1, string.Empty, ex.Message);
        }
    }

    private static string? ReadStoredPython()
    {
        try
        {
            var path = GetPythonPathFile();
            if (!File.Exists(path)) return null;
            var value = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    private static string PrependPath(string first, string? existing)
    {
        if (string.IsNullOrWhiteSpace(existing)) return first;
        return first + Path.PathSeparator + existing;
    }

    private static bool LooksLikePython(string path)
    {
        // Accept path existence; py launcher may not be a full python binary.
        return File.Exists(path);
    }

    private static string? ResolveOnPath(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;
        if (Path.IsPathRooted(executable) && File.Exists(executable)) return executable;

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static List<(string FileName, string? PyVersionArg)> BuildPythonCandidates()
    {
        var list = new List<(string, string?)>();

        if (OperatingSystem.IsWindows())
        {
            var py = ResolveOnPath("py.exe") ?? ResolveOnPath("py");
            if (py is not null)
            {
                // Prefer highest compatible first.
                foreach (var minor in new[] { 13, 12, 11, 10 })
                {
                    list.Add((py, $"-3.{minor}"));
                }
            }
        }

        // Versioned PATH names (highest first).
        foreach (var name in new[]
                 {
                     "python3.13", "python3.12", "python3.11", "python3.10",
                     "python3.13.exe", "python3.12.exe", "python3.11.exe", "python3.10.exe",
                     "python3", "python3.exe", "python", "python.exe"
                 })
        {
            var resolved = ResolveOnPath(name);
            if (resolved is not null)
            {
                list.Add((resolved, null));
            }
            else if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                // Still try bare name via Process PATH resolution
                list.Add((name, null));
            }
        }

        return list;
    }

    private static (string VersionText, string? Executable)? ProbePythonVersion(string fileName, string? pyVersionArg)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = false
            };
            if (!string.IsNullOrWhiteSpace(pyVersionArg))
            {
                psi.ArgumentList.Add(pyVersionArg);
            }

            // Print major.minor and real executable path on one line.
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}'); print(sys.executable)");

            using var process = Process.Start(psi);
            if (process is null) return null;
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(4000);
            if (process.ExitCode != 0) return null;

            var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0) return null;
            var version = lines[0].Trim();
            var executable = lines.Length > 1 ? lines[1].Trim() : null;
            if (string.IsNullOrWhiteSpace(executable)) executable = null;
            return (version, executable);
        }
        catch
        {
            return null;
        }
    }

    private static string DescribeResolution(KokoroPythonResolution r)
    {
        if (!string.IsNullOrWhiteSpace(r.ExecutablePath)) return r.ExecutablePath!;
        if (!string.IsNullOrWhiteSpace(r.PyVersionArg)) return $"{r.FileName} {r.PyVersionArg}";
        return r.FileName;
    }
}
