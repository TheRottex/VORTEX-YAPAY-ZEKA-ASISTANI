namespace Vortex.Desktop.Services;

public enum SetupReadinessState
{
    Ready,
    NotReady,
    Untested
}

public sealed record SetupReadinessItem(string Name, SetupReadinessState State, string Detail);

public sealed record DesktopSetupReadinessReport(IReadOnlyList<SetupReadinessItem> Items)
{
    public bool VoiceReady => Items
        .Where(item => item.Name != "Microphone")
        .All(item => item.State == SetupReadinessState.Ready);
}

public sealed class DesktopSetupReadinessService
{
    private readonly VoiceInputService _voiceInput;
    private readonly Func<string, string?> _resolveOnPath;
    private readonly Func<(bool Ready, string? ExecutablePath, string? ModelPath)> _inspectWhisper;

    public DesktopSetupReadinessService(
        VoiceInputService voiceInput,
        Func<string, string?>? resolveOnPath = null,
        Func<(bool Ready, string? ExecutablePath, string? ModelPath)>? inspectWhisper = null)
    {
        _voiceInput = voiceInput;
        _resolveOnPath = resolveOnPath ?? ResolveOnPath;
        _inspectWhisper = inspectWhisper ?? InspectWhisper;
    }

    public DesktopSetupReadinessReport Inspect(bool microphoneProbed = false, bool microphoneSucceeded = false)
    {
        var items = new List<SetupReadinessItem>();
        if (OperatingSystem.IsLinux())
        {
            AddCommand(items, "arecord", "arecord", "arecord komutu bulundu.", "arecord komutu bulunamadı.");
        }
        else
        {
            items.Add(new("arecord", SetupReadinessState.Ready, "Bu platformda arecord gerekli değil."));
        }

        var (whisperReady, executablePath, modelPath) = _inspectWhisper();
        items.Add(new("Whisper executable", executablePath is null ? SetupReadinessState.NotReady : SetupReadinessState.Ready,
            executablePath is null ? "Whisper çalıştırılabilir dosyası bulunamadı." : "Whisper çalıştırılabilir dosyası bulundu."));
        items.Add(new("Whisper model", modelPath is null ? SetupReadinessState.NotReady : SetupReadinessState.Ready,
            modelPath is null ? "Whisper modeli bulunamadı." : "Whisper modeli bulundu."));
        if (!whisperReady && executablePath is not null && modelPath is not null)
        {
            items.Add(new("Whisper", SetupReadinessState.NotReady, "Whisper yapılandırması kullanıma hazır değil."));
        }

        var player = FirstAvailable("mpv", "ffplay", "mpg123", "paplay", "pw-play", "aplay");
        items.Add(new("Audio player", player is null ? SetupReadinessState.NotReady : SetupReadinessState.Ready,
            player is null ? "Desteklenen ses oynatıcı bulunamadı." : $"Ses oynatıcı bulundu: {player}."));

        var tts = FirstAvailable("espeak-ng", "piper");
        items.Add(new("Local TTS", tts is null ? SetupReadinessState.NotReady : SetupReadinessState.Ready,
            tts is null ? "Yerel TTS komutu bulunamadı." : $"Yerel TTS bulundu: {tts}."));

        items.Add(new("Microphone",
            microphoneProbed ? (microphoneSucceeded ? SetupReadinessState.Ready : SetupReadinessState.NotReady) : SetupReadinessState.Untested,
            microphoneProbed ? (microphoneSucceeded ? "Mikrofon probu başarılı." : "Mikrofon probu başarısız.") : "Mikrofon açıkça test edilmedi."));

        return new(items);
    }

    private (bool Ready, string? ExecutablePath, string? ModelPath) InspectWhisper()
    {
        var ready = _voiceInput.GetWhisperReadiness(out var executablePath, out var modelPath);
        return (ready, executablePath, modelPath);
    }

    private void AddCommand(List<SetupReadinessItem> items, string name, string command, string ready, string missing)
    {
        var resolved = _resolveOnPath(command);
        items.Add(new(name, resolved is null ? SetupReadinessState.NotReady : SetupReadinessState.Ready,
            resolved is null ? missing : ready));
    }

    private string? FirstAvailable(params string[] commands) => commands.FirstOrDefault(command => _resolveOnPath(command) is not null);

    private static string? ResolveOnPath(string executable)
    {
        foreach (var segment in Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [])
        {
            if (string.IsNullOrWhiteSpace(segment)) continue;
            var candidate = Path.Combine(segment.Trim(), executable);
            if (File.Exists(candidate)) return executable;
        }
        return null;
    }
}
