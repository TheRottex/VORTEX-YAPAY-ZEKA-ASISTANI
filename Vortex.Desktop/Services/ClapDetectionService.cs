namespace Vortex.Desktop.Services;

/// <summary>
/// Saf C# akustik-geçiş (clap-like) algılayıcı. Harici bir clap-listener
/// ikilisi gerektirmez; mevcut <see cref="VoiceInputService.AudioLevelChanged"/>
/// olayının yaydığı 0-1 RMS genlik seviyesini izler ve keskin bir tepe ardından
/// yaklaşık 300 ms içinde sessizliğe düşen tek kısa pencereyi "alkış" olarak sayar.
/// 200 ms çalıştırma sonrası debounce ile yanlış pozitifleri azaltır.
/// Tüm iş parçacığı güvenlidir; hatalar yutulur ve <see cref="DesktopLogService"/>
/// üzerinden kaydedilir; asla çağırana fırlatılmaz.
/// </summary>
public sealed class ClapDetectionService
{
    private readonly object _gate = new();
    private VoiceInputService? _voiceInput;
    private EventHandler<double>? _audioHandler;
    private bool _running;

    // Algılama durumu (gate altında).
    private DateTimeOffset? _peakAt;
    private bool _awaitingSilence;
    private DateTimeOffset _lastFireTime = DateTimeOffset.MinValue;

    /// <summary>RMS tepe eşiği; bu değerin üzerindeki tek pencere bir "şarp ses" adayıdır.</summary>
    public double ClapThreshold { get; set; } = 0.85;

    /// <summary>Tepe sonrası beklenen sessizlik tabanı; 300 ms içinde bu seviyenin altına düşilmelidir.</summary>
    public double SilenceFloor { get; set; } = 0.10;

    /// <summary>İki clap arasında en az bu kadar sessizlik/bekleme olmalıdır.</summary>
    public TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Tepe sonrası sessizliğin gelmesi için izin verilen en uzun süre.</summary>
    public TimeSpan SilenceWindow { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Bir alkış algılandığında yayınlanır; abone olanlar yalnızca yerel dinlemeyi başlatır/etkinleştirir.</summary>
    public event EventHandler? ClapDetected;

    /// <summary>
    /// Test ve mevcut ses hattından doğrudan besleme için tek örnek RMS seviyesi işler.
    /// Üretimde normal yol <see cref="VoiceInputService.AudioLevelChanged"/> aboneliğidir.
    /// </summary>
    public void SubmitAudioLevel(double level) => OnAudioLevelChanged(this, level);

    /// <summary>
    /// <paramref name="voiceInput"/> üzerinden <see cref="VoiceInputService.AudioLevelChanged"/>
    /// olayına abone olur. Hatalar yutulur; asla fırlatılmaz.
    /// </summary>
    public Task StartAsync(VoiceInputService voiceInput, CancellationToken ct)
    {
        try
        {
            if (ct.IsCancellationRequested) return Task.CompletedTask;

            lock (_gate)
            {
                if (_running)
                {
                    DesktopLogService.Info("ClapDetectionService zaten çalışıyor; StartAsync yoksayıldı.");
                    return Task.CompletedTask;
                }

                _voiceInput = voiceInput ?? throw new ArgumentNullException(nameof(voiceInput));
                _audioHandler = OnAudioLevelChanged;
                _voiceInput.AudioLevelChanged += _audioHandler;

                _peakAt = null;
                _awaitingSilence = false;
                _running = true;
            }

            DesktopLogService.Info($"ClapDetectionService başlatıldı (threshold={ClapThreshold:F2}, silenceFloor={SilenceFloor:F2}).");
        }
        catch (Exception ex)
        {
            DesktopLogService.Error("ClapDetectionService.StartAsync başlatılamadı; sessiz degradasyon.", ex);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Aboneliği kaldırır ve algılamayı durdurur. Idempotent; asla fırlatmaz.
    /// </summary>
    public void Stop()
    {
        try
        {
            lock (_gate)
            {
                if (!_running)
                {
                    return;
                }

                if (_voiceInput is not null && _audioHandler is not null)
                {
                    try
                    {
                        _voiceInput.AudioLevelChanged -= _audioHandler;
                    }
                    catch (Exception ex)
                    {
                        DesktopLogService.Error("ClapDetectionService abonelik kaldırma başarısız oldu.", ex);
                    }
                }

                _voiceInput = null;
                _audioHandler = null;
                _peakAt = null;
                _awaitingSilence = false;
                _running = false;
            }

            DesktopLogService.Info("ClapDetectionService durduruldu.");
        }
        catch (Exception ex)
        {
            DesktopLogService.Error("ClapDetectionService.Stop sırasında hata; işleme devam ediliyor.", ex);
        }
    }

    private void OnAudioLevelChanged(object? sender, double level)
    {
        try
        {
            if (level < 0) level = 0;
            if (level > 1) level = 1;

            var now = DateTimeOffset.UtcNow;
            bool fire = false;

            lock (_gate)
            {
                if (!_running) return;

                // Debounce penceresi içinde yeniden tetiklenmeyi engelle.
                if (now - _lastFireTime < Debounce)
                {
                    return;
                }

                if (!_awaitingSilence)
                {
                    // 1) Keskin tepe adayı: tepe eşiğin üzerindeyse pencere açılır.
                    if (level >= ClapThreshold)
                    {
                        _peakAt = now;
                        _awaitingSilence = true;
                    }
                }
                else if (_peakAt is { } peakAt)
                {
                    // 2) Tepe sonrası sessizliğe düşmüşse tek kısa pencere alkıştır.
                    if (level <= SilenceFloor)
                    {
                        fire = true;
                        _awaitingSilence = false;
                        _peakAt = null;
                    }
                    // 3) 300 ms içinde sessizlik gelmezseadayı iptal et; uzun süren gürültü alkış sayılmaz.
                    else if (now - peakAt > SilenceWindow)
                    {
                        _awaitingSilence = false;
                        _peakAt = null;
                    }
                }
            }

            if (fire)
            {
                _lastFireTime = now;
                DesktopLogService.Info($"ClapDetectionService: alkış algılandı (level={level:F2}).");
                try
                {
                    ClapDetected?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    DesktopLogService.Error("ClapDetectionService: ClapDetected abonesi exception fırlattı.", ex);
                }
            }
        }
        catch (Exception ex)
        {
            DesktopLogService.Error("ClapDetectionService.OnAudioLevelChanged işlenemedi; sessiz degradasyon.", ex);
        }
    }
}
