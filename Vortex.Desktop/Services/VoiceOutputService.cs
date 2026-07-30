using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Vortex.Desktop.Services;

public sealed class VoiceOutputService
{
    private readonly BackendClient? _backendClient;
    private System.Speech.Synthesis.SpeechSynthesizer? _synthesizer;
    private Process? _linuxSpeechProcess;
    private WaveOutEvent? _waveOut;
    private readonly EdgeTtsService _edgeTts = new();
    private readonly KokoroTtsService _kokoroTts = new();
    private CancellationTokenSource? _ttsCancellation;
    private static HttpClient _httpClient = new();

    public VoiceOutputService(BackendClient? backendClient = null) => _backendClient = backendClient;

    public bool ElevenLabsTtsEnabled { get; set; }
    public string ElevenLabsApiKey { get; set; } = string.Empty;
    public string ElevenLabsVoiceId { get; set; } = string.Empty;

    public bool MinMaxTtsEnabled { get; set; }
    public string MinMaxApiKey { get; set; } = string.Empty;
    public string MinMaxTtsVoiceId { get; set; } = string.Empty;
    public string MinMaxTtsModelId { get; set; } = "speech-01-turbo";

    public bool PreferLocalOfflineTts { get; set; }
    public KokoroTtsStatus KokoroStatus => _kokoroTts.GetStatus();

    public event EventHandler? SpeakingStarted;
    public event EventHandler? SpeakingStopped;
    public event EventHandler<VoiceOutputStatusEventArgs>? StatusChanged;

    private void ReportStatus(string kind, string provider, string reason)
        => StatusChanged?.Invoke(this, new VoiceOutputStatusEventArgs(kind, provider, reason));

    public void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Stop();

        _ttsCancellation = new CancellationTokenSource();
        _ = SpeakAsync(text, _ttsCancellation.Token);
    }


    private async Task<byte[]?> SynthesizeServerTtsAsync(string text, string voiceId, string? modelId, string provider, CancellationToken cancellationToken)
    {
        if (_backendClient is null) return null;
        var response = await _backendClient.TextToSpeechAsync(new Vortex.Shared.ServerTextToSpeechRequest(text, voiceId, "tr", modelId, provider), cancellationToken);
        if (!response.Succeeded || string.IsNullOrWhiteSpace(response.AudioBase64))
        {
            ReportStatus("tts_fallback", provider, string.IsNullOrWhiteSpace(response.Message) ? "provider_failed" : response.Message);
            return null;
        }
        return Convert.FromBase64String(response.AudioBase64);
    }

    private Task<byte[]?> SynthesizeServerElevenLabsAsync(string text, string voiceId, CancellationToken cancellationToken)
        => SynthesizeServerTtsAsync(text, voiceId, null, "elevenlabs", cancellationToken);

    private Task<byte[]?> SynthesizeServerMinMaxAsync(string text, string voiceId, string modelId, CancellationToken cancellationToken)
        => SynthesizeServerTtsAsync(text, voiceId, modelId, "minmax", cancellationToken);
    public async Task<byte[]?> SynthesizeElevenLabsAsync(string text, string apiKey, string voiceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(voiceId))
        {
            throw new ArgumentException("ElevenLabs API Key and Voice ID are required.");
        }

        if (_backendClient is not null)
        {
            var proxy = await SynthesizeServerElevenLabsAsync(text, voiceId, cancellationToken);
            if (proxy is not null) return proxy;
        }

        var url = $"https://api.elevenlabs.io/v1/text-to-speech/{voiceId}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("xi-api-key", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));

        var body = new
        {
            text = text,
            model_id = "eleven_multilingual_v2"
        };
        var json = JsonSerializer.Serialize(body);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task<byte[]?> SynthesizeMinMaxAsync(string text, string apiKey, string voiceId, string modelId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(voiceId))
        {
            throw new ArgumentException("MinMax API Key and Voice ID are required.");
        }

        var url = "https://api.minimax.chat/v1/t2a_v2";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var body = new
        {
            model = modelId,
            text = text,
            voice_setting = new
            {
                voice_id = voiceId,
                speed = 1.0,
                vol = 1.0,
                pitch = 0
            },
            audio_setting = new
            {
                sample_rate = 32000,
                bitrate = 128000,
                format = "mp3",
                channel = 1
            }
        };
        var json = JsonSerializer.Serialize(body);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var jsonDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (jsonDoc.RootElement.TryGetProperty("data", out var dataProp) &&
            dataProp.TryGetProperty("audio", out var audioProp))
        {
            var hexString = audioProp.GetString();
            if (!string.IsNullOrEmpty(hexString))
            {
                try { return Convert.FromHexString(hexString); } catch { }
            }
        }
        return null;
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

    private async Task<bool> TrySpeakWithPiperAsync(string text, CancellationToken cancellationToken)
    {
        var piperCmd = Environment.GetEnvironmentVariable("VORTEX_PIPER_COMMAND");
        if (string.IsNullOrWhiteSpace(piperCmd))
        {
            piperCmd = ResolveOnPath("piper");
        }
        if (string.IsNullOrWhiteSpace(piperCmd) || (!File.Exists(piperCmd) && ResolveOnPath(piperCmd) is null))
        {
            return false;
        }

        var tempFile = Path.Combine(Path.GetTempPath(), $"vortex_piper_{Guid.NewGuid():N}.wav");
        try
        {
            var psi = new ProcessStartInfo(piperCmd)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            var modelPath = Environment.GetEnvironmentVariable("VORTEX_PIPER_MODEL");
            if (!string.IsNullOrWhiteSpace(modelPath))
            {
                psi.ArgumentList.Add("--model");
                psi.ArgumentList.Add(modelPath);
            }
            psi.ArgumentList.Add("--output_file");
            psi.ArgumentList.Add(tempFile);

            using var process = Process.Start(psi);
            if (process is null) return false;

            await process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken);
            process.StandardInput.Close();

            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0 || !File.Exists(tempFile) || new FileInfo(tempFile).Length == 0)
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch {}
                return false;
            }

            SpeakingStarted?.Invoke(this, EventArgs.Empty);
            if (OperatingSystem.IsWindows())
            {
                var reader = new AudioFileReader(tempFile);
                _waveOut = new WaveOutEvent();
                _waveOut.Init(reader);
                _waveOut.PlaybackStopped += (_, _) =>
                {
                    reader.Dispose();
                    try { File.Delete(tempFile); } catch { }
                    SpeakingStopped?.Invoke(this, EventArgs.Empty);
                };
                _waveOut.Play();
                return true;
            }

            var player = ResolvePlayer();
            if (player is null)
            {
                try { File.Delete(tempFile); } catch {}
                return false;
            }
            _linuxSpeechProcess = Process.Start(new ProcessStartInfo(player) { ArgumentList = { tempFile }, UseShellExecute = false, CreateNoWindow = true });
            if (_linuxSpeechProcess is null)
            {
                try { File.Delete(tempFile); } catch {}
                return false;
            }
            _ = WaitForLinuxSpeechWithTempFileAsync(_linuxSpeechProcess, tempFile);
            return true;
        }
        catch (Exception ex)
        {
            DesktopLogService.Error("Piper yerel TTS kullanılamadı; sistem fallback'e geçiliyor.", ex);
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch {}
            return false;
        }
    }

    private async Task SpeakAsync(string text, CancellationToken cancellationToken)
    {
        byte[]? audioBytes = null;
        if (PreferLocalOfflineTts)
        {
            if (await TrySpeakWithKokoroAsync(text, cancellationToken))
            {
                return;
            }
            if (await TrySpeakWithPiperAsync(text, cancellationToken))
            {
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _synthesizer ??= new System.Speech.Synthesis.SpeechSynthesizer();
                    _synthesizer.SpeakCompleted -= OnSpeakCompleted;
                    _synthesizer.SpeakCompleted += OnSpeakCompleted;
                    SpeakingStarted?.Invoke(this, EventArgs.Empty);
                    _synthesizer.SpeakAsync(text);
                }
                catch (Exception ex)
                {
                    SpeakingStopped?.Invoke(this, EventArgs.Empty);
                    DesktopLogService.Error("Windows SAPI ile sesli yanıt oynatılamadı.", ex);
                }
            }
            else
            {
                try
                {
                    _linuxSpeechProcess = Process.Start(new ProcessStartInfo("espeak-ng") { ArgumentList = { text }, UseShellExecute = false });
                    SpeakingStarted?.Invoke(this, EventArgs.Empty);
                    if (_linuxSpeechProcess is not null) _ = WaitForLinuxSpeechAsync(_linuxSpeechProcess);
                }
                catch (Exception ex)
                {
                    SpeakingStopped?.Invoke(this, EventArgs.Empty);
                    DesktopLogService.Error("espeak-ng ile sesli yanıt oynatılamadı (kurulu olmayabilir).", ex);
                }
            }
            return;
        }

        bool serverTtsSuccess = false;

        if (!PreferLocalOfflineTts && MinMaxTtsEnabled && !string.IsNullOrWhiteSpace(MinMaxTtsVoiceId))
        {
            try
            {
                SpeakingStarted?.Invoke(this, EventArgs.Empty);
                audioBytes = await SynthesizeServerMinMaxAsync(text, MinMaxTtsVoiceId, string.IsNullOrWhiteSpace(MinMaxTtsModelId) ? "speech-01-turbo" : MinMaxTtsModelId, cancellationToken);
                if (audioBytes is not null && audioBytes.Length > 0)
                {
                    serverTtsSuccess = true;
                }
            }
            catch (Exception ex)
            {
                DesktopLogService.Error("Sunucu MinMax TTS proxy sentezi başarısız, ElevenLabs/Edge TTS fallback'e geçiliyor.", ex);
            }
        }

        if (!PreferLocalOfflineTts && !serverTtsSuccess && ElevenLabsTtsEnabled && !string.IsNullOrWhiteSpace(ElevenLabsVoiceId))
        {
            try
            {
                SpeakingStarted?.Invoke(this, EventArgs.Empty);
                audioBytes = await SynthesizeServerElevenLabsAsync(text, ElevenLabsVoiceId, cancellationToken);
                if (audioBytes is not null && audioBytes.Length > 0)
                {
                    serverTtsSuccess = true;
                }
            }
            catch (Exception ex)
            {
                DesktopLogService.Error("Sunucu TTS proxy sentezi başarısız, Edge TTS/espeak-ng fallback'e geçiliyor.", ex);
            }
        }

        if (!serverTtsSuccess && !PreferLocalOfflineTts)
        {
            try
            {
                SpeakingStarted?.Invoke(this, EventArgs.Empty);
                audioBytes = await _edgeTts.SynthesizeAsync(text, "tr-TR-AhmetNeural", cancellationToken);
            }
            catch (Exception ex)
            {
                DesktopLogService.Error("Edge TTS sentezi başarısız oldu.", ex);
            }
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (audioBytes is not null && audioBytes.Length > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var ms = new MemoryStream(audioBytes);
                    var mp3Reader = new Mp3FileReader(ms);
                    _waveOut = new WaveOutEvent();
                    _waveOut.Init(mp3Reader);
                    _waveOut.PlaybackStopped += (s, e) =>
                    {
                        SpeakingStopped?.Invoke(this, EventArgs.Empty);
                        mp3Reader.Dispose();
                        ms.Dispose();
                    };
                    _waveOut.Play();
                    return;
                }
            }
            catch (Exception ex)
            {
                DesktopLogService.Error("Birincil ses sentez/oynatma başarısız oldu, System.Speech fallback devreye giriyor.", ex);
            }

            // Fallback to System.Speech
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                _synthesizer ??= new System.Speech.Synthesis.SpeechSynthesizer();
                _synthesizer.SpeakCompleted -= OnSpeakCompleted;
                _synthesizer.SpeakCompleted += OnSpeakCompleted;
                SpeakingStarted?.Invoke(this, EventArgs.Empty);
                _synthesizer.SpeakAsync(text);
            }
            catch (Exception ex)
            {
                SpeakingStopped?.Invoke(this, EventArgs.Empty);
                DesktopLogService.Error("Windows SAPI ile sesli yanıt oynatılamadı.", ex);
            }
        }
        else
        {
            // Linux/Pardus: Try playing synthesized MP3, fallback to espeak-ng
            bool nativePlaybackSuccess = false;
            if (audioBytes is not null && audioBytes.Length > 0)
            {
                string tempFile = Path.Combine(Path.GetTempPath(), $"vortex_tts_{Guid.NewGuid():N}.mp3");
                try
                {
                    await File.WriteAllBytesAsync(tempFile, audioBytes, cancellationToken);

                    var player = ResolvePlayer();
                    if (player is not null)
                    {
                        var psi = new ProcessStartInfo(player)
                        {
                            ArgumentList = { tempFile },
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        _linuxSpeechProcess = Process.Start(psi);
                        if (_linuxSpeechProcess is not null)
                        {
                            if (!serverTtsSuccess) SpeakingStarted?.Invoke(this, EventArgs.Empty);
                            _ = WaitForLinuxSpeechWithTempFileAsync(_linuxSpeechProcess, tempFile);
                            nativePlaybackSuccess = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    DesktopLogService.Error("Linux native MP3 oynatma adımı başarısız oldu.", ex);
                }

                if (!nativePlaybackSuccess && File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch {}
                }
            }

            if (!nativePlaybackSuccess)
            {
                try
                {
                    _linuxSpeechProcess = Process.Start(new ProcessStartInfo("espeak-ng") { ArgumentList = { text }, UseShellExecute = false });
                    if (!serverTtsSuccess) SpeakingStarted?.Invoke(this, EventArgs.Empty);
                    if (_linuxSpeechProcess is not null) _ = WaitForLinuxSpeechAsync(_linuxSpeechProcess);
                }
                catch (Exception ex)
                {
                    SpeakingStopped?.Invoke(this, EventArgs.Empty);
                    DesktopLogService.Error("espeak-ng ile sesli yanıt oynatılamadı (kurulu olmayabilir).", ex);
                }
            }
        }
    }

    private async Task<bool> TrySpeakWithKokoroAsync(string text, CancellationToken cancellationToken)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"vortex_kokoro_{Guid.NewGuid():N}.wav");
        try
        {
            if (!await _kokoroTts.TrySynthesizeAsync(text, tempFile, cancellationToken)) return false;
            SpeakingStarted?.Invoke(this, EventArgs.Empty);
            if (OperatingSystem.IsWindows())
            {
                var reader = new AudioFileReader(tempFile);
                _waveOut = new WaveOutEvent();
                _waveOut.Init(reader);
                _waveOut.PlaybackStopped += (_, _) =>
                {
                    reader.Dispose();
                    try { File.Delete(tempFile); } catch { }
                    SpeakingStopped?.Invoke(this, EventArgs.Empty);
                };
                _waveOut.Play();
                return true;
            }

            var player = ResolvePlayer();
            if (player is null) return false;
            _linuxSpeechProcess = Process.Start(new ProcessStartInfo(player) { ArgumentList = { tempFile }, UseShellExecute = false, CreateNoWindow = true });
            if (_linuxSpeechProcess is null) return false;
            _ = WaitForLinuxSpeechWithTempFileAsync(_linuxSpeechProcess, tempFile);
            return true;
        }
        catch (Exception ex)
        {
            DesktopLogService.Error("Kokoro yerel TTS kullanılamadı; sistem fallback'e geçiliyor.", ex);
            return false;
        }
    }

    private static string? ResolvePlayer()
    {
        foreach (var player in new[] { "pw-play", "paplay", "aplay", "mpg123" })
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(path)) continue;
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var candidate = Path.Combine(directory, player);
                if (File.Exists(candidate)) return player;
            }
        }
        return null;
    }

    private async Task WaitForLinuxSpeechWithTempFileAsync(Process process, string tempFile)
    {
        try
        {
            await process.WaitForExitAsync();
        }
        catch
        {
        }
        finally
        {
            if (ReferenceEquals(_linuxSpeechProcess, process)) _linuxSpeechProcess = null;
            process.Dispose();
            try
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
            catch {}
            SpeakingStopped?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Stop()
    {
        _ttsCancellation?.Cancel();
        _ttsCancellation = null;

        if (_waveOut is not null)
        {
            try
            {
                _waveOut.Stop();
                _waveOut.Dispose();
                _waveOut = null;
            }
            catch (Exception ex)
            {
                DesktopLogService.Error("NAudio waveOut durdurulamadı.", ex);
            }
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                _synthesizer?.SpeakAsyncCancelAll();
            }
            catch (Exception ex)
            {
                DesktopLogService.Error("Windows SAPI konuşması durdurulamadı.", ex);
            }
        }

        try
        {
            if (_linuxSpeechProcess is { HasExited: false }) _linuxSpeechProcess.Kill();
            _linuxSpeechProcess?.Dispose();
            _linuxSpeechProcess = null;
        }
        catch (Exception ex)
        {
            DesktopLogService.Error("espeak-ng konuşması durdurulamadı.", ex);
        }

        SpeakingStopped?.Invoke(this, EventArgs.Empty);
    }

    private async Task WaitForLinuxSpeechAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync();
        }
        catch
        {
        }
        finally
        {
            if (ReferenceEquals(_linuxSpeechProcess, process)) _linuxSpeechProcess = null;
            process.Dispose();
            SpeakingStopped?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnSpeakCompleted(object? sender, System.Speech.Synthesis.SpeakCompletedEventArgs e)
    {
        SpeakingStopped?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record VoiceOutputStatusEventArgs(string Kind, string Provider, string Reason);


