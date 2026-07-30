namespace Vortex.Desktop.Services;

public sealed record WakeWordEngineStatus(string Mode, string Detail, bool IsDedicatedWakeWordEngineAvailable);

public static class WakeWordEngineStatusService
{
    public static WakeWordEngineStatus GetCurrent()
        => new(
            "Yerel transkript kapısı",
            "openWakeWord/ONNX motoru yapılandırılmadı. Sürekli dinleme sesi cihazda Whisper.cpp ile yazıya çevirir; 'Hey Vortex' ifadesi yalnızca transkriptte aranır.",
            false);
}
