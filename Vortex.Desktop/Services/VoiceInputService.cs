using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using NAudio.Wave;

namespace Vortex.Desktop.Services;

public sealed class VoiceInputService : IDisposable
{
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _waveWriter;
    private Process? _arecordProcess;
    private string? _tempFilePath;
    private readonly string _modelDirectory;
    private Task? _linuxRecordingTask;
    private CancellationTokenSource? _linuxCts;

    public VoiceInputService(string? modelDirectory = null)
    {
        _modelDirectory = modelDirectory ?? WhisperModelService.GetDefaultModelDirectory();
    }

    public event EventHandler<double>? AudioLevelChanged;

    public Task StartRecordingAsync()
    {
        _tempFilePath = Path.Combine(Path.GetTempPath(), $"vortex-voice-{Guid.NewGuid():N}.wav");

        if (OperatingSystem.IsWindows())
        {
            _waveIn = new WaveInEvent { WaveFormat = new WaveFormat(16000, 1) };
            _waveWriter = new WaveFileWriter(_tempFilePath, _waveIn.WaveFormat);
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.StartRecording();
        }
        else
        {
            _linuxCts = new CancellationTokenSource();
            _arecordProcess = new Process
            {
                StartInfo = new ProcessStartInfo("arecord")
                {
                    ArgumentList = { "-f", "S16_LE", "-r", "16000", "-c", "1", "-t", "wav", "-" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = false,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            _arecordProcess.Start();

            var tempPath = _tempFilePath;
            var process = _arecordProcess;
            var cts = _linuxCts;

            _linuxRecordingTask = Task.Run(async () =>
            {
                try
                {
                    using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096, useAsync: true);
                    var stdout = process.StandardOutput.BaseStream;

                    // arecord streams 44-byte WAV header first
                    var headerBuffer = new byte[44];
                    var headerBytesRead = 0;
                    while (headerBytesRead < 44 && !cts.Token.IsCancellationRequested)
                    {
                        var read = await stdout.ReadAsync(headerBuffer.AsMemory(headerBytesRead, 44 - headerBytesRead), cts.Token);
                        if (read == 0) break;
                        headerBytesRead += read;
                    }

                    if (headerBytesRead > 0)
                    {
                        await fileStream.WriteAsync(headerBuffer.AsMemory(0, headerBytesRead), cts.Token);
                    }

                    var buffer = new byte[1024];
                    while (!cts.Token.IsCancellationRequested)
                    {
                        var read = await stdout.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token);
                        if (read == 0) break;

                        await fileStream.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                        var level = CalculateRmsLevel(buffer, read);
                        AudioLevelChanged?.Invoke(this, level);
                    }

                    // Fix WAV sizes on clean exit
                    if (fileStream.Length >= 44)
                    {
                        fileStream.Position = 4;
                        var chunkSize = BitConverter.GetBytes((int)fileStream.Length - 8);
                        await fileStream.WriteAsync(chunkSize, cts.Token);

                        fileStream.Position = 40;
                        var subchunk2Size = BitConverter.GetBytes((int)fileStream.Length - 44);
                        await fileStream.WriteAsync(subchunk2Size, cts.Token);
                    }
                }
                catch (OperationCanceledException) {}
                catch (Exception ex)
                {
                    DesktopLogService.Error("arecord stream reading failed.", ex);
                }
            });
        }

        return Task.CompletedTask;
    }

    public async Task<byte[]?> StopRecordingAsync()
    {
        AudioLevelChanged?.Invoke(this, 0.02);
        var path = _tempFilePath;
        _tempFilePath = null;
        if (path is null) return null;

        if (OperatingSystem.IsWindows())
        {
            if (_waveIn is not null) _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn?.StopRecording();
            _waveWriter?.Dispose();
            _waveIn?.Dispose();
            _waveWriter = null;
            _waveIn = null;
        }
        else if (_arecordProcess is not null)
        {
            if (_linuxCts is not null)
            {
                await _linuxCts.CancelAsync();
            }

            if (!_arecordProcess.HasExited)
            {
                try { _arecordProcess.Kill(); } catch { }
            }
            await _arecordProcess.WaitForExitAsync();

            if (_linuxRecordingTask is not null)
            {
                try { await _linuxRecordingTask; } catch { }
                _linuxRecordingTask = null;
            }

            if (_linuxCts is not null)
            {
                _linuxCts.Dispose();
                _linuxCts = null;
            }

            _arecordProcess.Dispose();
            _arecordProcess = null;
        }

        if (!File.Exists(path)) return null;
        var bytes = await File.ReadAllBytesAsync(path);
        try { File.Delete(path); } catch { }
        return bytes.Length == 0 ? null : bytes;
    }

    /// <summary>
    /// Yerel, offline, ücretsiz whisper.cpp STT. Çalıştırılabilir veya model
    /// dosyası bulunamazsa <c>null</c> döner; çağıran doğru başarısızlık durumu gösterir.
    /// Asla fırlatma yapılmaz; eksik ikili/model sahte transkript üretmez.
    /// </summary>
    public async Task<string?> TranscribeLocalAsync(byte[] wavBytes, CancellationToken cancellationToken)
    {
        if (wavBytes is null || wavBytes.Length == 0) return null;

        var exePath = ResolveWhisperExecutable();
        var modelPath = ResolveWhisperModelPath();
        if (exePath is null || modelPath is null) return null;

        string? wavPath = null;
        string? txtPath = null;
        Process? process = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outputBase = Path.Combine(Path.GetTempPath(), $"vortex-whisper-{Guid.NewGuid():N}");
            wavPath = outputBase + ".wav";
            txtPath = outputBase + ".txt";
            await File.WriteAllBytesAsync(wavPath, wavBytes, cancellationToken);

            process = new Process();
            var startInfo = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add(modelPath);
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add(wavPath);
            startInfo.ArgumentList.Add("-l");
            startInfo.ArgumentList.Add("tr");
            startInfo.ArgumentList.Add("-otxt");
            startInfo.ArgumentList.Add("-of");
            startInfo.ArgumentList.Add(outputBase);
            startInfo.ArgumentList.Add("--no-prints");
            process.StartInfo = startInfo;

            if (!process.Start()) return null;

            // whisper çıktısını asenkron tüket (kilitlenme olmasın).
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { if (!process.HasExited) process.Kill(); } catch { }
                return null;
            }

            if (process.ExitCode != 0) return null;

            if (!File.Exists(txtPath)) return null;

            var stderrText = string.Empty;
            try { stderrText = await stderrTask; } catch { }
            if (!string.IsNullOrEmpty(stderrText)) DesktopLogService.Info($"whisper.cpp stderr: {stderrText.Trim()}");
            // stdout görevini de tüket ki buffer dolmasın; transkripsiyon metni dosyada.
            try { _ = await stdoutTask; } catch { }

            var text = await File.ReadAllTextAsync(txtPath, cancellationToken);
            var trimmed = text.Trim();
            return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { if (process is not null && !process.HasExited) process.Kill(); } catch {}
            throw;
        }
        catch (Exception ex)
        {
            DesktopLogService.Error("Yerel whisper.cpp transkripsiyonu başarısız oldu.", ex);
            return null;
        }
        finally
        {
            if (process is not null)
            {
                process.Dispose();
            }
            if (wavPath is not null) { try { if (File.Exists(wavPath)) File.Delete(wavPath); } catch { } }
            if (txtPath is not null) { try { if (File.Exists(txtPath)) File.Delete(txtPath); } catch { } }
        }
    }

    public static string? ResolveWhisperExecutable(Func<string, bool>? fileExists = null, Func<string, string?>? resolveOnPath = null, IEnumerable<string>? searchDirectories = null, bool? isWindows = null)
    {
        fileExists ??= File.Exists;
        resolveOnPath ??= ResolveOnPath;
        searchDirectories ??= GetWhisperSearchDirectories();
        var windows = isWindows ?? OperatingSystem.IsWindows();
        var pathNames = windows
            ? new[] { "whisper-cli.exe", "whisper-cli", "whisper.exe", "whisper", "main.exe" }
            : new[] { "whisper-cli", "whisper" };

        var configuredExecutable = Environment.GetEnvironmentVariable("VORTEX_WHISPER_EXE");
        if (IsSupportedWhisperExecutable(configuredExecutable, fileExists, windows)) return configuredExecutable;

        foreach (var name in pathNames)
        {
            var fullPath = resolveOnPath(name);
            if (IsSupportedWhisperExecutable(fullPath, fileExists, windows)) return fullPath;
        }

        var appNames = windows
            ? new[] { "whisper-cli.exe", "whisper.exe", "main.exe" }
            : new[] { "whisper-cli", "whisper" };
        foreach (var directory in searchDirectories)
        {
            foreach (var name in appNames)
            {
                var candidate = Path.Combine(directory, name);
                if (IsSupportedWhisperExecutable(candidate, fileExists, windows)) return candidate;
            }

            foreach (var candidate in FindWhisperExecutableInDirectory(directory, appNames))
            {
                if (IsSupportedWhisperExecutable(candidate, fileExists, windows)) return candidate;
            }
        }
        return null;
    }

    public static bool IsSupportedWhisperExecutable(string? candidate, Func<string, bool>? fileExists = null, bool? isWindows = null)
    {
        if (string.IsNullOrWhiteSpace(candidate) || (!(isWindows ?? OperatingSystem.IsWindows()) && candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))) return false;
        return (fileExists ?? File.Exists)(candidate);
    }

    public string PreferredWhisperModelId { get; set; } = "tiny";

    public string? GetResolvedWhisperModelPath() => ResolveWhisperModelPath();

    public bool GetWhisperReadiness(out string? executablePath, out string? modelPath)
    {
        executablePath = ResolveWhisperExecutable();
        modelPath = ResolveWhisperModelPath();

        if (executablePath is null || !IsSupportedWhisperExecutable(executablePath))
        {
            return false;
        }

        if (modelPath is null || !File.Exists(modelPath))
        {
            return false;
        }

        return true;
    }

    private string? ResolveWhisperModelPath()
    {
        var envModel = Environment.GetEnvironmentVariable("VORTEX_WHISPER_MODEL");
        if (!string.IsNullOrWhiteSpace(envModel) && File.Exists(envModel)) return envModel;

        var preferred = WhisperModelService.AvailableModels.FirstOrDefault(model => string.Equals(model.Id, PreferredWhisperModelId, StringComparison.OrdinalIgnoreCase));
        var candidateNames = new List<string>();
        if (preferred is not null)
        {
            candidateNames.Add(preferred.FileName);
        }
        candidateNames.AddRange(new[] { "ggml-small.bin", "ggml-base.bin", "ggml-medium.bin", "ggml-tiny.bin", "ggml-model.bin" });

        foreach (var directory in new[]
        {
            _modelDirectory,
            WhisperModelService.GetDefaultModelDirectory(),
            Path.Combine(AppContext.BaseDirectory, "whisper", "models"),
            Path.Combine(AppContext.BaseDirectory, "whisper")
        }.Concat(GetWhisperSearchDirectories().Select(directory => Path.Combine(directory, "models")))
         .Concat(GetWhisperSearchDirectories()))
        {
            foreach (var name in candidateNames)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static IEnumerable<string> GetWhisperSearchDirectories()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("VORTEX_WHISPER_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot)) yield return configuredRoot;

        yield return Path.Combine(AppContext.BaseDirectory, "whisper");
        yield return Path.Combine(AppContext.BaseDirectory, "whisper.cpp");

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktop))
        {
            foreach (var rootName in new[] { "OpenCode-Tam-Yedek - Kopya (2)", "OpenCode-Tam-Yedek - Kopya", "OpenCode-Tam-Yedek" })
            {
                var root = Path.Combine(desktop, rootName);
                yield return Path.Combine(root, "whisper");
                yield return Path.Combine(root, "whisper.cpp");
                yield return Path.Combine(root, "Vortex-Clean-Source", "whisper");
                yield return Path.Combine(root, "Vortex-Clean-Source", "whisper.cpp");
            }
        }
    }

    private static IEnumerable<string> FindWhisperExecutableInDirectory(string directory, string[] appNames)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) yield break;

        foreach (var name in appNames)
        {
            string[] matches;
            try
            {
                matches = Directory.GetFiles(directory, name, SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            foreach (var match in matches)
            {
                yield return match;
            }
        }
    }

    private static string? ResolveOnPath(string fileName)
    {
        try
        {
            var found = Array.Find(Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>(), seg =>
            {
                if (string.IsNullOrWhiteSpace(seg)) return false;
                return File.Exists(Path.Combine(seg.Trim(), fileName));
            });
            return found is null ? null : Path.Combine(found.Trim(), fileName);
        }
        catch
        {
            return null;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _waveWriter?.Write(e.Buffer, 0, e.BytesRecorded);
        AudioLevelChanged?.Invoke(this, CalculateRmsLevel(e.Buffer, e.BytesRecorded));
    }

    private static double CalculateRmsLevel(byte[] buffer, int bytesRecorded)
    {
        if (bytesRecorded < 2) return 0;
        double sumSquares = 0;
        var samples = bytesRecorded / 2;
        for (var i = 0; i < bytesRecorded - 1; i += 2)
        {
            var sample = BitConverter.ToInt16(buffer, i) / 32768.0;
            sumSquares += sample * sample;
        }
        return Math.Clamp(Math.Sqrt(sumSquares / samples) * 4.5, 0, 1);
    }

    public void Dispose()
    {
        if (_linuxCts is not null)
        {
            try { _linuxCts.Cancel(); } catch {}
            _linuxCts.Dispose();
            _linuxCts = null;
        }

        if (_arecordProcess is not null)
        {
            try
            {
                if (!_arecordProcess.HasExited) _arecordProcess.Kill();
            }
            catch {}
            _arecordProcess.Dispose();
            _arecordProcess = null;
        }

        _waveWriter?.Dispose();
        _waveIn?.Dispose();
    }
}
