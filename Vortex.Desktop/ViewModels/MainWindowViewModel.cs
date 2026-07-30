using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vortex.Desktop;
using Vortex.Desktop.Services;
using Vortex.Shared;

namespace Vortex.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly BackendClient _backendClient;
    private readonly IDesktopAuthenticationService _authenticationService;
    private readonly DesktopSettingsService _settingsService;
    private readonly VoiceInputService _voiceInput = new();
    private readonly VoiceOutputService _voiceOutput;
    private readonly LocalAgentClient _localAgentClient = new();
    private readonly LocalAgentRuntimeService _localAgentRuntime;
    private ClapDetectionService? _clapDetection = new();
    private CancellationTokenSource? _authCancellation;
    private CancellationTokenSource? _operationCancellation;
    private bool _recordingOwnedByAlwaysListening;
    private bool _suppressLocalAgentPersist;
    private readonly bool _isPreviewMode;
    private readonly LocalDatabaseService _localDb;
    private TaskCompletionSource<bool>? _pendingApprovalTcs;


    [ObservableProperty] private string inputText = string.Empty;
    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private string statusText = "Giris yapilmadi";
    [ObservableProperty] private string activeModel = "Vortex Provider Router";
    [ObservableProperty] private bool isAuthenticated;
    [ObservableProperty] private bool isWelcomeVisible = true;
    [ObservableProperty] private bool isAuthenticating;
    [ObservableProperty] private string currentUserText = string.Empty;
    [ObservableProperty] private string hermesStatusText = "Hermes profili bekleniyor";
    [ObservableProperty] private string email = string.Empty;
    [ObservableProperty] private string password = string.Empty;
    [ObservableProperty] private string displayName = string.Empty;
    [ObservableProperty] private string firstName = string.Empty;
    [ObservableProperty] private string lastName = string.Empty;
    [ObservableProperty] private string confirmPassword = string.Empty;
    [ObservableProperty] private string birthDate = string.Empty;
    [ObservableProperty] private string phoneNumber = string.Empty;
    [ObservableProperty] private bool isRegisterScreenVisible;
    [ObservableProperty] private string authFormErrorText = string.Empty;
    [ObservableProperty] private ActiveJobViewModel? activeJob;
    [ObservableProperty] private bool isRecording;
    [ObservableProperty] private bool isTranscribing;
    [ObservableProperty] private bool isVoiceReplyEnabled;
    [ObservableProperty] private string orbState = "offline";
    [ObservableProperty] private double inputAudioLevel = 0.02;
    [ObservableProperty] private bool isAgentBusy;
    [ObservableProperty] private bool canStopCurrentOperation;
    [ObservableProperty] private double mainUiScale = 1.0;
    [ObservableProperty] private double compactUiScale = 1.0;
    [ObservableProperty] private ChatSessionViewModel? activeChat;
    [ObservableProperty] private bool isLoadingChats;
    [ObservableProperty] private bool isSidebarCollapsed;
    [ObservableProperty] private string chatsStatusText = string.Empty;
    [ObservableProperty] private bool isVoiceModeEnabled;
    [ObservableProperty] private bool isAlwaysListening;
    [ObservableProperty] private string wakeWordText = "Hey Vortex";
    [ObservableProperty] private string voiceModeStatusText = string.Empty;
    [ObservableProperty] private bool elevenLabsTtsEnabled;
    [ObservableProperty] private string elevenLabsApiKey = string.Empty;
    [ObservableProperty] private string elevenLabsVoiceId = string.Empty;
    [ObservableProperty] private bool minMaxTtsEnabled;
    [ObservableProperty] private string minMaxApiKey = string.Empty;
    [ObservableProperty] private string minMaxTtsVoiceId = string.Empty;
    [ObservableProperty] private string minMaxTtsModelId = "speech-01-turbo";
    [ObservableProperty] private string whisperModelId = "small";
    [ObservableProperty] private bool isOfflineMode;
    [ObservableProperty] private string kokoroStatusText = new KokoroTtsService().GetStatus().Detail;
    [ObservableProperty] private bool isKokoroInstallPromptVisible;
    [ObservableProperty] private bool isKokoroInstalling;
    [ObservableProperty] private bool isKokoroInstallFailed;
    [ObservableProperty] private string kokoroInstallLog = string.Empty;
    private bool _isKokoroInstallPromptDismissed;
    [ObservableProperty] private bool isLocalAgentApprovalCardVisible;
    [ObservableProperty] private string localAgentApprovalTitle = "Onay gerekli";
    [ObservableProperty] private string localAgentApprovalText = string.Empty;
    [ObservableProperty] private string kokoroInstallPromptText = string.Empty;
    [ObservableProperty] private bool rememberMe;
    [ObservableProperty] private string themePreference = "System";
    [ObservableProperty] private string localAgentBaseUrl = "http://127.0.0.1:47891";
    [ObservableProperty] private string localAgentSecret = string.Empty;
    [ObservableProperty] private string localAgentStatusText = "Çevrimdışı cihaz eylemleri henüz hazırlanmadı.";
    [ObservableProperty] private LocalAgentDeviceDto? selectedLocalAgentDevice;
    [ObservableProperty] private string localAgentDeviceStatusText = "Sunucuya bağlı Vortex Yapay Zeka Asistanı cihazları henüz yüklenmedi.";
    [ObservableProperty] private bool isLocalAgentDeviceLoading;
    private bool _hasFreshLocalAgentDeviceList;
    [ObservableProperty] private string scheduledTaskName = string.Empty;
    [ObservableProperty] private string scheduledTaskSchedule = string.Empty;
    [ObservableProperty] private string scheduledTaskTimeZone = string.Empty;
    [ObservableProperty] private string scheduledTasksStatusText = "Zamanlanmış görevler henüz yüklenmedi.";
    [ObservableProperty] private bool isScheduledTasksLoading;
    [ObservableProperty] private string localLlmBaseUrl = string.Empty;
    [ObservableProperty] private string localLlmApiKey = string.Empty;
    [ObservableProperty] private string localLlmModel = "";
    [ObservableProperty] private string localLlmStatusText = "Yapay Zeka API bağlantısı test edilmedi.";
    [ObservableProperty] private bool isMascotVisible = true;
    private bool _suppressThemePersist;

    [ObservableProperty] private bool isTasksTabSelected;
    [ObservableProperty] private bool isCreditsTabSelected;
    [ObservableProperty] private string creditStatusText = "Kredi bilgisi yüklenmedi.";
    [ObservableProperty] private bool isCreditStatusLoading;
    [ObservableProperty] private DirectChatCreditStatusDto? directChatCreditStatus;
    [ObservableProperty] private string creditCountdownText = "Sunucu kredi bilgisi bekleniyor.";
    private readonly DispatcherTimer _creditCountdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public bool IsCreditStatusUnavailable => !IsCreditStatusLoading && DirectChatCreditStatus is null;
    public string CreditRemainingText => DirectChatCreditStatus is null ? "Kalan kredi: —" : $"Kalan kredi: {DirectChatCreditStatus.RemainingCredits:0.00} / {DirectChatCreditStatus.TotalCredits:0.00}";
    public string CreditTokensText => DirectChatCreditStatus is null ? "Kalan token: —" : $"Kalan token: {DirectChatCreditStatus.RemainingTokenUnits:N0} / {DirectChatCreditStatus.TotalTokenUnits:N0}";
    public double CreditProgressPercent => DirectChatCreditStatus is null ? 0 : (double)(DirectChatCreditStatus.RemainingTokenUnits * 100m / DirectChatCreditStatus.TotalTokenUnits);

    public string MascotVisibilityIcon => IsMascotVisible
        ? "M12 4.5C7 4.5 2.73 7.61 1 12c1.73 4.39 6 7.5 11 7.5s9.27-3.11 11-7.5c-1.73-4.39-6-7.5-11-7.5zM12 17c-2.76 0-5-2.24-5-5s2.24-5 5-5 5 2.24 5 5-2.24 5-5 5zm0-8c-1.66 0-3 1.34-3 3s1.34 3 3 3 3-1.34 3-3-1.34-3-3-3z"
        : "M12 7c2.76 0 5 2.24 5 5 0 .65-.13 1.26-.36 1.82l2.92 2.92c1.51-1.26 2.7-2.89 3.44-4.74-1.73-4.39-6-7.5-11-7.5-1.4 0-2.74.25-3.98.7l2.16 2.16C10.74 7.13 11.35 7 12 7zM2 4.27l2.28 2.28.46.46C3.08 8.3 1.78 10.02 1 12c1.73 4.39 6 7.5 11 7.5 2.2 0 4.27-.61 6.04-1.66l.46.46L20.23 20 21.5 18.73 3.27 3 2 4.27zM12 17c-2.76 0-5-2.24-5-5 0-.77.18-1.5.49-2.14l1.57 1.57c-.03.18-.06.37-.06.57 0 1.66 1.34 3 3 3 .2 0 .39-.03.57-.06l1.57 1.57c-.64.31-1.37.49-2.14.49zm.93-7.57l1.64 1.64c.27-.57.43-1.21.43-1.89 0-2.21-1.79-4-4-4-.68 0-1.32.16-1.89.43l1.64 1.64c.08-.03.16-.07.25-.07.66 0 1.2 0.54 1.2 1.2 0 .09-.04.17-.07.25z";

    [RelayCommand]
    private void ToggleMascot()
    {
        IsMascotVisible = !IsMascotVisible;
    }

    partial void OnIsMascotVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(MascotVisibilityIcon));
    }

    public bool IsAssistantTabSelected => !IsTasksTabSelected && !IsCreditsTabSelected;
    public string AsistanTabBackground => IsAssistantTabSelected ? "#1e293b" : "Transparent";
    public string TasksTabBackground => IsTasksTabSelected ? "#1e293b" : "Transparent";
    public string CreditsTabBackground => IsCreditsTabSelected ? "#1e293b" : "Transparent";
    public string AsistanTabForeground => IsTasksTabSelected || IsCreditsTabSelected ? "#64748b" : "#ffffff";
    public string TasksTabForeground => IsTasksTabSelected ? "#ffffff" : "#64748b";
    public string CreditsTabForeground => IsCreditsTabSelected ? "#ffffff" : "#64748b";
    public bool IsRegisterMode => IsRegisterScreenVisible;
    public bool IsLoginMode => !IsRegisterScreenVisible;
    public string AuthScreenTitle => IsRegisterScreenVisible ? "Hesap Oluştur" : "Giriş Yap";
    public string AuthScreenDescription => IsRegisterScreenVisible
        ? "Vortex Yapay Zekâ Asistanı için yeni hesabını oluştur."
        : "Hesabınla giriş yap veya çevrimdışı modda devam et.";

    partial void OnIsTasksTabSelectedChanged(bool value)
    {
        if (value) IsCreditsTabSelected = false;
        RefreshTabProperties();
    }

    partial void OnIsCreditsTabSelectedChanged(bool value)
    {
        if (value) IsTasksTabSelected = false;
        RefreshTabProperties();
    }

    partial void OnDirectChatCreditStatusChanged(DirectChatCreditStatusDto? value)
    {
        OnPropertyChanged(nameof(IsCreditStatusUnavailable));
        OnPropertyChanged(nameof(CreditRemainingText));
        OnPropertyChanged(nameof(CreditTokensText));
        OnPropertyChanged(nameof(CreditProgressPercent));
        UpdateCreditCountdown();
    }

    partial void OnIsCreditStatusLoadingChanged(bool value) => OnPropertyChanged(nameof(IsCreditStatusUnavailable));

    private void RefreshTabProperties()
    {
        OnPropertyChanged(nameof(IsAssistantTabSelected));
        OnPropertyChanged(nameof(AsistanTabBackground));
        OnPropertyChanged(nameof(TasksTabBackground));
        OnPropertyChanged(nameof(CreditsTabBackground));
        OnPropertyChanged(nameof(AsistanTabForeground));
        OnPropertyChanged(nameof(TasksTabForeground));
        OnPropertyChanged(nameof(CreditsTabForeground));
    }

    public string MicButtonText => IsRecording ? "Kaydı bitir" : (IsTranscribing ? "Tanınmayı bekliyor..." : "Mikrofon");
    public string VoiceStatusText => OrbState switch
    {
        "recording" => "Dinliyor",
        "transcribing" => "Yazıya dönüştürülüyor",
        "speaking" => "Konuşuyor",
        "processing" => "Düşünüyor",
        "error" => "Son işlem hata verdi",
        "idle" => "Hazır",
        _ => "Ses kapalı"
    };
    public string ProviderMode => IsAuthenticated ? "cloud" : "offline";

    public string LlmProviderDisplay => string.IsNullOrWhiteSpace(LocalLlmBaseUrl)
        ? "Yapay Zekâ API ayarlı değil"
        : $"Yapay Zekâ API: {LocalLlmBaseUrl.Trim()}";
    public string MainScaleText => $"Ana ölçek {MainUiScale:0.00}x";
    public string CompactScaleText => $"Compact {CompactUiScale:0.00}x";

    public ObservableCollection<MessageViewModel> Messages { get; } = new()
    {
        new("Vortex", "Merhaba. Güvenli web girişi tamamlandıktan sonra sohbet ekranı açılır.")
    };

    public ObservableCollection<ActivityEventViewModel> Timeline { get; } = new();

    public bool IsTimelineEmpty => Timeline.Count == 0;

    public ObservableCollection<ChatSessionViewModel> Chats { get; } = new();
    public ObservableCollection<LocalAgentDeviceDto> RegisteredLocalAgentDevices { get; } = new();
    public ObservableCollection<AgentTaskDto> ScheduledTasks { get; } = new();

    public Guid? SelectedLocalAgentDeviceId => SelectedLocalAgentDevice?.Id;
    public bool HasRegisteredLocalAgentDevices => RegisteredLocalAgentDevices.Count > 0;
    public bool IsNoLocalAgentDeviceAvailable => !IsLocalAgentDeviceLoading && RegisteredLocalAgentDevices.Count == 0;
    public string SelectedLocalAgentDeviceDetail => SelectedLocalAgentDevice is null
        ? string.Empty
        : SelectedLocalAgentDevice.LastSeenAt is { } lastSeen
            ? $"{SelectedLocalAgentDevice.DeviceName} — son görülme: {lastSeen.LocalDateTime:g}"
            : $"{SelectedLocalAgentDevice.DeviceName} — henüz sunucuya bağlanmadı";

    public bool IsChatsEmpty => Chats.Count == 0;

    public MainWindowViewModel(BackendClient backendClient, IDesktopAuthenticationService authenticationService)
        : this(backendClient, authenticationService, new DesktopSettingsService())
    {
    }

    public MainWindowViewModel(BackendClient backendClient, IDesktopAuthenticationService authenticationService, DesktopSettingsService settingsService)
    {
        _backendClient = backendClient;
        _authenticationService = authenticationService;
        _settingsService = settingsService;
        _localDb = new LocalDatabaseService();
        _localAgentRuntime = new LocalAgentRuntimeService(settingsService, _localAgentClient);
        _voiceOutput = new VoiceOutputService(backendClient);
        _voiceInput.PreferredWhisperModelId = WhisperModelId;
        _voiceOutput.PreferLocalOfflineTts = IsOfflineMode;
        KokoroStatusText = _voiceOutput.KokoroStatus.Detail;
        _voiceInput.AudioLevelChanged += (_, level) =>
        {
            _ = RunOnUiThreadAsync(() => InputAudioLevel = level);
            EvaluateVoiceActivity(level);
        };
        _voiceOutput.SpeakingStarted += (_, _) => _ = RunOnUiThreadAsync(() => OrbState = "speaking");
        _voiceOutput.SpeakingStopped += (_, _) => _ = RunOnUiThreadAsync(() => OrbState = IsAuthenticated ? "idle" : "offline");
        _voiceOutput.StatusChanged += (_, status) => _ = RunOnUiThreadAsync(() =>
        {
            var message = GetTtsStatusMessage(status.Reason);
            AddTimelineEvent(status.Kind, $"provider={status.Provider} reason={status.Reason}");
            StatusText = message;
            if (status.Kind == "tts_failed") OrbState = "error";
        });
        _creditCountdownTimer.Tick += (_, _) => UpdateCreditCountdown();
        if (_clapDetection is not null)
        {
            _clapDetection.ClapDetected += OnClapDetected;
        }
        Timeline.CollectionChanged += OnTimelineCollectionChanged;
        Chats.CollectionChanged += OnChatsCollectionChanged;
        _isPreviewMode = string.Equals(Environment.GetEnvironmentVariable("VORTEX_DESKTOP_PREVIEW"), "1", StringComparison.Ordinal);
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await LoadSettingsAsync();
        if (_isPreviewMode)
        {
            EnablePreviewShell();
        }
        else
        {
            await LoadStoredSessionAsync();
        }
    }
    private void EnablePreviewShell()
    {
        IsAuthenticated = true;
        IsWelcomeVisible = false;
        IsRegisterScreenVisible = false;
        CurrentUserText = "Yerel UI önizleme / Sunucusuz";
        StatusText = "Yerel UI önizleme modu";
        HermesStatusText = "Sunucu kapalı: yalnızca arayüz önizleniyor";
        OrbState = "idle";
        ActiveModel = "Vortex UI Preview";

        Chats.Clear();
        var now = DateTimeOffset.Now;
        var first = new ChatSessionViewModel(Guid.NewGuid(), "Arayüz düzeni kontrolü", now, true);
        Chats.Add(first);
        Chats.Add(new ChatSessionViewModel(Guid.NewGuid(), "API proxy notlari", now.AddMinutes(-18)));
        Chats.Add(new ChatSessionViewModel(Guid.NewGuid(), "Sesli mod taslak akisi", now.AddHours(-2)));
        ActiveChat = first;

        Messages.Clear();
        Messages.Add(new MessageViewModel("Kullanıcı", "Ana sohbet alanının yüksekliğini, sol geçmiş listesini ve maskot görünümünü kontrol etmek istiyorum."));
        Messages.Add(new MessageViewModel("Asistan", "Bu ekran VortexOrb tasarım diline yakın, üç panelli yerel masaüstü kabuğudur."));
        Messages.Add(new MessageViewModel("Kullanıcı", "Türkçe karakterler ve yazı hizaları doğru görünüyor mu?"));
        Messages.Add(new MessageViewModel("Asistan", "Evet, bu metinler UTF-8 Türkçe karakterlerle çiziliyor: Ç, Ğ, İ, Ö, Ğ, Ü, ı."));

        Timeline.Clear();
        Timeline.Add(new ActivityEventViewModel("ui_preview", "Yerel UI önizleme modu etkin", now));
        Timeline.Add(new ActivityEventViewModel("completed", "Sunucu çağrısı yapılmadı", now.AddMinutes(-1)));
    }

    private void OnChatsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsChatsEmpty));
    }

    async partial void OnSearchTextChanged(string value)
    {
        await LoadChatsAsync(value);
    }

    partial void OnIsRecordingChanged(bool value)
    {
        OnPropertyChanged(nameof(MicButtonText));
        OnPropertyChanged(nameof(VoiceModeActionText));
    }

    partial void OnIsTranscribingChanged(bool value)
    {
        OnPropertyChanged(nameof(MicButtonText));
    }

    partial void OnOrbStateChanged(string value)
    {
        OnPropertyChanged(nameof(VoiceStatusText));
    }

    partial void OnIsAuthenticatedChanged(bool value)
    {
        IsWelcomeVisible = !value;
        OrbState = value ? "idle" : "offline";
        OnPropertyChanged(nameof(ProviderMode));
    }

    partial void OnIsAlwaysListeningChanged(bool value)
    {
        if (value && !IsRecording)
        {
            // Sürekli dinleme açıldığında mikrofonu Wake Word tetikleyicisi olarak başlat.
            _ = StartAlwaysListeningAsync();
        }
        else if (!value && IsRecording)
        {
            _vadTimer?.Stop();
            _isUserSpeaking = false;
            if (_recordingOwnedByAlwaysListening)
            {
                // Sürekli dinleme/clap tarafından sahiplenilen kayıt kapanırken manuel push-to-talk yoluna düşmez;
                // transkript varsa Step 9 wake-word kapısından geçer, non-wake içerik tutulmaz veya gönderilmez.
                _ = StopRecordingAndTranscribeAndSendAsync();
            }
            else
            {
                _ = StopRecordingAndTranscribeAsync();
            }
        }
        VoiceModeStatusText = value ? "Sürekli dinleme açık: yerel transkript kapısı açık; 'Hey Vortex' ifadesi aranır." : string.Empty;
    }

    async partial void OnIsVoiceModeEnabledChanged(bool value)
    {
        if (value)
        {
            IsTasksTabSelected = false; // Switch to Assistant tab to prevent crash and show Orb
            // Voice Mode açıldığında mevcut sohbetin içine geçici sesli konuşma durumunu yansıt
            OrbState = "idle";
            VoiceModeStatusText = "Voice Mode açık.";
            AddTimelineEvent("voice_mode_enabled", "Voice Mode açıldı, sesli sohbet aktif.");
            await StartClapListeningAsync();
            if (!IsRecording && !IsTranscribing)
            {
                await StartRecordingAsync();
            }
        }
        else
        {
            StopClapListening();
            if (IsRecording && !IsAlwaysListening && !IsTranscribing)
            {
                try
                {
                    await _voiceInput.StopRecordingAsync();
                }
                catch (Exception ex)
                {
                    DesktopLogService.Error("Alkış tetikleyici için yerel dinleme başlatılamadı.", ex);
                }
                IsRecording = false;
            }
            // Voice Mode kapatıldığında o ana kadarki sesli konuşmalar normal sohbet geçmişine aktarılır
            CommitVoiceModeTranscriptToActiveChat();
            AddTimelineEvent("voice_mode_disabled", "Voice Mode kapatıldı, sesli konuşma normal sohbet geçmişine aktarıldı.");
            IsAlwaysListening = false;
            VoiceModeStatusText = string.Empty;
        }
    }

    partial void OnMainUiScaleChanged(double value) => OnPropertyChanged(nameof(MainScaleText));

    partial void OnCompactUiScaleChanged(double value) => OnPropertyChanged(nameof(CompactScaleText));

    partial void OnElevenLabsTtsEnabledChanged(bool value)
    {
        _voiceOutput.ElevenLabsTtsEnabled = value;
        _ = SaveSettingsAsync();
    }

    partial void OnElevenLabsApiKeyChanged(string value)
    {
        _voiceOutput.ElevenLabsApiKey = value;
        _ = SaveSettingsAsync();
    }

    partial void OnElevenLabsVoiceIdChanged(string value)
    {
        _voiceOutput.ElevenLabsVoiceId = value;
        _ = SaveSettingsAsync();
    }

    partial void OnMinMaxTtsEnabledChanged(bool value)
    {
        _voiceOutput.MinMaxTtsEnabled = value;
        _ = SaveSettingsAsync();
    }

    partial void OnMinMaxApiKeyChanged(string value)
    {
        _voiceOutput.MinMaxApiKey = value;
        _ = SaveSettingsAsync();
    }

    partial void OnMinMaxTtsVoiceIdChanged(string value)
    {
        _voiceOutput.MinMaxTtsVoiceId = value;
        _ = SaveSettingsAsync();
    }

    partial void OnMinMaxTtsModelIdChanged(string value)
    {
        _voiceOutput.MinMaxTtsModelId = value;
        _ = SaveSettingsAsync();
    }

    partial void OnWhisperModelIdChanged(string value)
    {
        _voiceInput.PreferredWhisperModelId = value;
        _ = SaveSettingsAsync();
    }

    partial void OnThemePreferenceChanged(string value)
    {
        var normalized = NormalizeThemePreference(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            // Re-enter with canonical System/Dark/Light without double-save.
            ThemePreference = normalized;
            return;
        }
        App.ApplyThemePreference(normalized);
        if (!_suppressThemePersist)
        {
            _ = SaveSettingsAsync();
        }
    }

    public static string NormalizeThemePreference(string? preference)
    {
        var p = (preference ?? "System").Trim();
        if (p.Equals("Light", StringComparison.OrdinalIgnoreCase)) return "Light";
        if (p.Equals("Dark", StringComparison.OrdinalIgnoreCase)) return "Dark";
        return "System";
    }

    partial void OnRememberMeChanged(bool value)
    {
        _ = SaveSettingsAsync();
    }

    partial void OnLocalAgentBaseUrlChanged(string value)
    {
        if (_suppressLocalAgentPersist) return;
        _ = SaveSettingsAsync();
    }

    partial void OnLocalAgentSecretChanged(string value)
    {
        if (_suppressLocalAgentPersist) return;
        _ = SaveSettingsAsync();
    }

    partial void OnLocalLlmBaseUrlChanged(string value)
    {
        OnPropertyChanged(nameof(LlmProviderDisplay));
        _ = SaveSettingsAsync();
    }

    partial void OnLocalLlmApiKeyChanged(string value)
    {
        _ = SaveSettingsAsync();
    }

    partial void OnLocalLlmModelChanged(string value)
    {
        _ = SaveSettingsAsync();
    }

    partial void OnIsVoiceReplyEnabledChanged(bool value) => _ = SaveSettingsAsync();

    partial void OnSelectedLocalAgentDeviceChanged(LocalAgentDeviceDto? value)
    {
        OnPropertyChanged(nameof(SelectedLocalAgentDeviceId));
        OnPropertyChanged(nameof(SelectedLocalAgentDeviceDetail));
    }

    partial void OnIsLocalAgentDeviceLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNoLocalAgentDeviceAvailable));
    }

    private void ClearLocalAgentDevices()
    {
        _hasFreshLocalAgentDeviceList = false;
        RegisteredLocalAgentDevices.Clear();
        SelectedLocalAgentDevice = null;
        OnPropertyChanged(nameof(HasRegisteredLocalAgentDevices));
        OnPropertyChanged(nameof(IsNoLocalAgentDeviceAvailable));
        OnPropertyChanged(nameof(SelectedLocalAgentDeviceDetail));
    }

    [RelayCommand]
    public async Task RefreshLocalAgentDevicesAsync()
    {
        if (IsOfflineMode || !IsAuthenticated)
        {
            ClearLocalAgentDevices();
            LocalAgentDeviceStatusText = "Sunucuya bağlı Vortex Yapay Zeka Asistanı cihazları için giriş yapılmalıdır.";
            return;
        }

        if (IsLocalAgentDeviceLoading) return;
        IsLocalAgentDeviceLoading = true;
        LocalAgentDeviceStatusText = "Sunucuya bağlı Vortex Yapay Zeka Asistanı cihazları yükleniyor...";
        var selectedId = SelectedLocalAgentDevice?.Id;
        try
        {
            var result = await _backendClient.ListLocalAgentDevicesDetailedAsync(CancellationToken.None);
            if (!result.Ok || result.Devices is null)
            {
                _hasFreshLocalAgentDeviceList = false;
                LocalAgentDeviceStatusText = result.Reason switch
                {
                    "not_authenticated" => "Cihaz listesini görmek için yeniden giriş yapın.",
                    "forbidden" => "Cihaz listesine erişim izniniz yok.",
                    "transport_error" => "Sunucuya bağlı Vortex Yapay Zeka Asistanı cihazlarına ulaşılamadı.",
                    "cancelled" => "Cihaz listesi yenilemesi iptal edildi.",
                    "invalid_response" => "Sunucu geçersiz bir cihaz listesi döndürdü.",
                    _ => "Sunucuya bağlı Vortex Yapay Zeka Asistanı cihazları yüklenemedi."
                };
                return;
            }

            _hasFreshLocalAgentDeviceList = true;
            RegisteredLocalAgentDevices.Clear();
            foreach (var device in result.Devices) RegisteredLocalAgentDevices.Add(device);
            SelectedLocalAgentDevice = selectedId is Guid id
                ? RegisteredLocalAgentDevices.FirstOrDefault(device => device.Id == id)
                : null;
            SelectedLocalAgentDevice ??= RegisteredLocalAgentDevices.FirstOrDefault();
            LocalAgentDeviceStatusText = RegisteredLocalAgentDevices.Count == 0
                ? "Kayıtlı Vortex Yapay Zeka Asistanı cihazı yok."
                : "Sunucuya bağlı Vortex Yapay Zeka Asistanı cihazları güncel.";
            OnPropertyChanged(nameof(HasRegisteredLocalAgentDevices));
            OnPropertyChanged(nameof(IsNoLocalAgentDeviceAvailable));
        }
        finally
        {
            IsLocalAgentDeviceLoading = false;
        }
    }

    [RelayCommand]
    public async Task RefreshScheduledTasksAsync()
    {
        if (IsOfflineMode || !IsAuthenticated)
        {
            ScheduledTasks.Clear();
            ScheduledTasksStatusText = "Zamanlanmış görevler için giriş yapılmalıdır.";
            return;
        }

        if (IsScheduledTasksLoading) return;
        IsScheduledTasksLoading = true;
        ScheduledTasksStatusText = "Zamanlanmış görevler yükleniyor...";
        try
        {
            var result = await _backendClient.ListScheduledTasksAsync(CancellationToken.None);
            if (!result.Ok || result.Tasks is null)
            {
                ScheduledTasksStatusText = result.Reason switch
                {
                    "not_authenticated" => "Zamanlanmış görevleri görmek için yeniden giriş yapın.",
                    "forbidden" => "Zamanlanmış görevlere erişim izniniz yok.",
                    "transport_error" => "Zamanlanmış görevlere ulaşılamadı.",
                    "cancelled" => "Zamanlanmış görev yenilemesi iptal edildi.",
                    "invalid_response" => "Sunucu geçersiz bir görev listesi döndürdü.",
                    _ => "Zamanlanmış görevler yüklenemedi."
                };
                return;
            }

            ScheduledTasks.Clear();
            foreach (var task in result.Tasks) ScheduledTasks.Add(task);
            ScheduledTasksStatusText = ScheduledTasks.Count == 0
                ? "Henüz zamanlanmış görev yok."
                : $"{ScheduledTasks.Count} zamanlanmış görev yüklendi.";
        }
        finally
        {
            IsScheduledTasksLoading = false;
        }
    }

    [RelayCommand]
    public async Task CreateScheduledTaskAsync()
    {
        if (IsOfflineMode || !IsAuthenticated)
        {
            ScheduledTasksStatusText = "Zamanlanmış görev oluşturmak için giriş yapılmalıdır.";
            return;
        }

        var name = ScheduledTaskName.Trim();
        var schedule = ScheduledTaskSchedule.Trim();
        var timeZone = ScheduledTaskTimeZone.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(schedule))
        {
            ScheduledTasksStatusText = "Görev adı ve zamanlama (cron) gereklidir.";
            return;
        }
        if (string.IsNullOrWhiteSpace(timeZone)) timeZone = TimeZoneInfo.Local.Id;

        var result = await _backendClient.CreateScheduledTaskAsync(new CreateAgentTaskRequest(name, schedule, timeZone), CancellationToken.None);
        if (!result.Ok)
        {
            ScheduledTasksStatusText = result.Reason switch
            {
                "not_authenticated" => "Görev oluşturmak için yeniden giriş yapın.",
                "scheduled_task_limit_exceeded" => "Zamanlanmış görev sınırına ulaşıldı.",
                "invalid_request" => "Zamanlama ifadesi geçersiz. 5 alanlı cron kullanın.",
                "transport_error" => "Sunucuya ulaşılamadı.",
                "cancelled" => "Görev oluşturma iptal edildi.",
                _ => "Zamanlanmış görev oluşturulamadı."
            };
            return;
        }

        ScheduledTaskName = string.Empty;
        ScheduledTaskSchedule = string.Empty;
        ScheduledTaskTimeZone = string.Empty;
        if (result.Task is not null) ScheduledTasks.Add(result.Task);
        ScheduledTasksStatusText = "Zamanlanmış görev oluşturuldu.";
    }

    [RelayCommand]
    public async Task DeleteScheduledTaskAsync(AgentTaskDto? task)
    {
        if (task is null) return;
        if (IsOfflineMode || !IsAuthenticated)
        {
            ScheduledTasksStatusText = "Zamanlanmış görev silmek için giriş yapılmalıdır.";
            return;
        }

        var reason = await _backendClient.DeleteScheduledTaskAsync(task.Id, CancellationToken.None);
        if (reason is "ok" or "not_found")
        {
            ScheduledTasks.Remove(task);
            ScheduledTasksStatusText = "Zamanlanmış görev silindi.";
            return;
        }

        ScheduledTasksStatusText = reason switch
        {
            "not_authenticated" => "Görev silmek için yeniden giriş yapın.",
            "transport_error" => "Sunucuya ulaşılamadı.",
            "cancelled" => "Silme işlemi iptal edildi.",
            _ => "Zamanlanmış görev silinemedi."
        };
    }

    partial void OnIsOfflineModeChanged(bool value)
    {
        _voiceOutput.PreferLocalOfflineTts = value;
        if (value) _isKokoroInstallPromptDismissed = false;
        RefreshKokoroInstallPrompt();
        _ = HandleOfflineModeChangeAsync(value);
    }

    private async Task HandleOfflineModeChangeAsync(bool value)
    {
        if (value)
        {
            ClearLocalAgentDevices();
            LocalAgentDeviceStatusText = "Çevrimdışı modda sunucuya bağlı Vortex Yapay Zeka Asistanı cihazları kullanılamaz.";
            IsAuthenticated = true;
            IsWelcomeVisible = false;
            IsRegisterScreenVisible = false;
            CurrentUserText = "Çevrimdışı Kullanıcı";
            StatusText = "Çevrimdışı Mod Aktif";
            OrbState = "offline";
            await LoadChatsAsync(null);
            // Light UX: do not auto-download large Whisper models; only hint if missing.
            try
            {
                if (_voiceInput.GetResolvedWhisperModelPath() is null)
                {
                    StatusText = "Çevrimdışı Mod Aktif. Whisper modeli Ayarlar > Ses bölümünden indirilebilir.";
                }
            }
            catch
            {
                // best-effort only
            }
        }
        else
        {
            if (IsAuthenticated && !string.Equals(CurrentUserText, "Çevrimdışı Kullanıcı", StringComparison.Ordinal))
            {
                await SaveSettingsAsync();
                return;
            }

            IsAuthenticated = false;
            IsWelcomeVisible = true;
            CurrentUserText = string.Empty;
            StatusText = "Giriş yapılmadı";
            OrbState = "offline";
            Chats.Clear();
            Messages.Clear();
            Messages.Add(new MessageViewModel("Vortex", "Merhaba. Güvenli web girişi tamamlandıktan sonra sohbet ekranı açılır."));
        }
        await SaveSettingsAsync();
    }

    private async Task LoadSettingsAsync()
    {
        var settings = await _settingsService.LoadAsync(CancellationToken.None);
        MainUiScale = settings.SafeMainUiScale;
        CompactUiScale = settings.SafeCompactUiScale;
        ElevenLabsTtsEnabled = settings.ElevenLabsTtsEnabled;
        ElevenLabsApiKey = settings.ElevenLabsApiKey;
        ElevenLabsVoiceId = settings.ElevenLabsVoiceId;
        MinMaxTtsEnabled = settings.MinMaxTtsEnabled;
        MinMaxApiKey = settings.MinMaxApiKey;
        MinMaxTtsVoiceId = settings.MinMaxTtsVoiceId;
        MinMaxTtsModelId = string.IsNullOrWhiteSpace(settings.MinMaxTtsModelId) ? "speech-01-turbo" : settings.MinMaxTtsModelId;
        WhisperModelId = string.IsNullOrWhiteSpace(settings.WhisperModelId) ? "small" : settings.WhisperModelId;
        IsVoiceReplyEnabled = settings.IsVoiceReplyEnabled;
        _voiceInput.PreferredWhisperModelId = WhisperModelId;
        _voiceOutput.PreferLocalOfflineTts = IsOfflineMode;
        KokoroStatusText = _voiceOutput.KokoroStatus.Detail;
        IsOfflineMode = settings.IsOfflineMode;
        RememberMe = settings.RememberMe;
        _suppressThemePersist = true;
        ThemePreference = settings.SafeThemePreference;
        _suppressThemePersist = false;
        _suppressLocalAgentPersist = true;
        LocalAgentBaseUrl = string.IsNullOrWhiteSpace(settings.LocalAgentBaseUrl)
            ? "http://127.0.0.1:47891"
            : settings.LocalAgentBaseUrl.Trim();
        LocalAgentSecret = settings.LocalAgentSecret ?? string.Empty;
        _suppressLocalAgentPersist = false;
        LocalAgentStatusText = "Çevrimdışı cihaz eylemleri henüz hazırlanmadı.";
        LocalLlmBaseUrl = string.IsNullOrWhiteSpace(settings.LocalLlmBaseUrl) ? string.Empty : settings.LocalLlmBaseUrl.Trim();
        LocalLlmApiKey = settings.LocalLlmApiKey ?? string.Empty;
        LocalLlmModel = settings.LocalLlmModel ?? string.Empty;

        // Apply settings directly to VoiceOutputService
        _voiceOutput.ElevenLabsTtsEnabled = ElevenLabsTtsEnabled;
        _voiceOutput.ElevenLabsApiKey = ElevenLabsApiKey;
        _voiceOutput.ElevenLabsVoiceId = ElevenLabsVoiceId;
        _voiceOutput.MinMaxTtsEnabled = MinMaxTtsEnabled;
        _voiceOutput.MinMaxApiKey = MinMaxApiKey;
        _voiceOutput.MinMaxTtsVoiceId = MinMaxTtsVoiceId;
        _voiceOutput.MinMaxTtsModelId = MinMaxTtsModelId;
        _voiceInput.PreferredWhisperModelId = WhisperModelId;
        _voiceOutput.PreferLocalOfflineTts = IsOfflineMode;
        KokoroStatusText = _voiceOutput.KokoroStatus.Detail;
        RefreshKokoroInstallPrompt();
    }

    private void RefreshKokoroInstallPrompt()
    {
        var status = _voiceOutput.KokoroStatus;
        KokoroStatusText = status.Detail;
        IsKokoroInstallPromptVisible = !_isKokoroInstallPromptDismissed &&
            (IsKokoroInstalling || (IsOfflineMode && !status.IsAvailable));
        KokoroInstallPromptText = IsKokoroInstallPromptVisible
            ? "Çevrimdışı sesli yanıt için Kokoro yerel TTS önerilir. Kurulum günlüğünü izleyebilir veya dilediğiniz anda yapay zekâ asistanına danışabilirsiniz."
            : string.Empty;
    }

    private async Task SaveSettingsAsync()
    {
        var current = await _settingsService.LoadAsync(CancellationToken.None);
        await _settingsService.SaveAsync(current with
        {
            MainUiScale = MainUiScale,
            CompactUiScale = CompactUiScale,
            RememberMe = RememberMe,
            IsOfflineMode = IsOfflineMode,
            IsVoiceReplyEnabled = IsVoiceReplyEnabled,
            ElevenLabsTtsEnabled = ElevenLabsTtsEnabled,
            ElevenLabsApiKey = ElevenLabsApiKey,
            ElevenLabsVoiceId = ElevenLabsVoiceId,
            MinMaxTtsEnabled = MinMaxTtsEnabled,
            MinMaxApiKey = MinMaxApiKey,
            MinMaxTtsVoiceId = MinMaxTtsVoiceId,
            MinMaxTtsModelId = MinMaxTtsModelId,
            WhisperModelId = WhisperModelId,
            ThemePreference = NormalizeThemePreference(ThemePreference),
            LocalAgentBaseUrl = string.IsNullOrWhiteSpace(LocalAgentBaseUrl)
                ? "http://127.0.0.1:47891"
                : LocalAgentBaseUrl.Trim(),
            LocalAgentSecret = LocalAgentSecret ?? string.Empty,
            LocalLlmBaseUrl = string.IsNullOrWhiteSpace(LocalLlmBaseUrl) ? string.Empty : LocalLlmBaseUrl.Trim(),
            LocalLlmApiKey = LocalLlmApiKey ?? string.Empty,
            LocalLlmModel = LocalLlmModel ?? string.Empty
        }, CancellationToken.None);
    }

    /// <summary>
    /// Direct LocalAgent health check. Not blocked by offline mode or public Server.
    /// </summary>
    public async Task TestLocalAgentConnectionAsync()
    {
        LocalAgentStatusText = "Çevrimdışı Vortex Yapay Zeka Asistanı hazırlanıyor...";
        var result = await _localAgentRuntime.EnsureReadyAsync(CancellationToken.None);
        LocalAgentStatusText = result.Ready
            ? "Çevrimdışı Vortex Yapay Zeka Asistanı hazır."
            : "Çevrimdışı Vortex Yapay Zeka Asistanı kullanılamıyor.";
        AddTimelineEvent(result.Ready ? "local_agent_health_ok" : "local_agent_health_failed", result.Reason);
    }

    public async Task TestLocalLlmConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(LocalLlmBaseUrl))
        {
            LocalLlmStatusText = "Önce çevrimdışı API Base URL girin.";
            return;
        }

        LocalLlmStatusText = "Çevrimdışı API test ediliyor...";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            if (!string.IsNullOrWhiteSpace(LocalLlmApiKey))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalLlmApiKey);
            }

            var baseUrl = LocalLlmBaseUrl.TrimEnd('/');
            if (baseUrl.EndsWith("/vl", StringComparison.OrdinalIgnoreCase))
            {
                LocalLlmStatusText = "API URL'sinde '/vl' yazıyor. OpenAI uyumlu URL için '/v1' kullanın (rakam 1).";
                return;
            }

            var isOllama = IsOllamaApi(baseUrl);
            var url = BuildModelListUrl(baseUrl, isOllama);
            using var response = await client.GetAsync(url, CancellationToken.None);
            if (!response.IsSuccessStatusCode)
            {
                LocalLlmStatusText = $"Çevrimdışı API liste endpoint'i HTTP {(int)response.StatusCode} döndürdü: {url}";
                return;
            }

            var chatResult = await ProbeLocalLlmChatAsync(client, baseUrl, isOllama, CancellationToken.None);
            LocalLlmStatusText = string.IsNullOrWhiteSpace(chatResult)
                ? "Çevrimdışı API erişilebilir, ancak desteklenen metin yanıtı üretmedi. Model adını veya endpoint tipini kontrol edin."
                : "Çevrimdışı API chat testi başarılı.";
        }
        catch (Exception ex)
        {
            LocalLlmStatusText = $"Çevrimdışı API'ye ulaşılamıyor: {ex.Message}";
        }
    }

    private async Task<string?> ProbeLocalLlmChatAsync(HttpClient client, string baseUrl, bool isOllama, CancellationToken cancellationToken)
    {
        var chatUrl = BuildChatUrl(baseUrl, isOllama);

        object body = isOllama
            ? new
            {
                model = string.IsNullOrWhiteSpace(LocalLlmModel) ? "llama3.1" : LocalLlmModel.Trim(),
                stream = false,
                messages = new[] { new { role = "user", content = "Say only: ok" } }
            }
            : new
            {
                model = string.IsNullOrWhiteSpace(LocalLlmModel) ? "local-model" : LocalLlmModel.Trim(),
                messages = new[] { new { role = "user", content = "Say only: ok" } }
            };

        using var response = await client.PostAsJsonAsync(chatUrl, body, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await ReadErrorBodyAsync(response, cancellationToken);
            LocalLlmStatusText = $"Çevrimdışı API erişilebilir, ancak chat endpoint'i HTTP {(int)response.StatusCode} döndürdü: {chatUrl}";
            if (!string.IsNullOrWhiteSpace(errorBody))
            {
                LocalLlmStatusText += $" | {TrimForStatus(errorBody)}";
            }
            return null;
        }

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), default, cancellationToken);
        var content = ExtractLocalLlmContent(doc.RootElement);
        if (!string.IsNullOrWhiteSpace(content)) return content;

        if (!isOllama)
        {
            return await ProbeOpenAiResponsesAsync(client, baseUrl, cancellationToken);
        }

        return null;
    }

    private async Task<string?> ProbeOpenAiResponsesAsync(HttpClient client, string baseUrl, CancellationToken cancellationToken)
    {
        var content = await SendOpenAiResponsesAsync(client, baseUrl, "Say only: ok", cancellationToken);
        if (!string.IsNullOrWhiteSpace(content)) return content;

        var responsesUrl = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? baseUrl + "/responses" : baseUrl + "/v1/responses";
        LocalLlmStatusText = $"Yapay Zeka API chat endpoint'i 404 verdi; responses endpoint'i de yanıt üretmedi: {responsesUrl}";
        return null;
    }

    private async Task<string?> SendOpenAiResponsesAsync(HttpClient client, string baseUrl, string input, CancellationToken cancellationToken)
    {
        var responsesUrl = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? baseUrl + "/responses" : baseUrl + "/v1/responses";
        var body = new
        {
            model = string.IsNullOrWhiteSpace(LocalLlmModel) ? "gpt-4o-mini" : LocalLlmModel.Trim(),
            input
        };

        using var response = await client.PostAsJsonAsync(responsesUrl, body, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), default, cancellationToken);
        return ExtractLocalLlmContent(doc.RootElement);
    }

    public static string? ExtractLocalLlmContent(JsonElement root)
    {
        if (TryGetText(root, "output_text", out var outputText)) return outputText;

        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content))
            {
                var messageText = ExtractTextValue(content);
                if (!string.IsNullOrWhiteSpace(messageText)) return messageText;
            }
            if (first.TryGetProperty("text", out var text) && TryGetText(text, out var legacyText)) return legacyText;
        }

        if (root.TryGetProperty("message", out var ollamaMessage) && ollamaMessage.TryGetProperty("content", out var ollamaContent))
        {
            var ollamaText = ExtractTextValue(ollamaContent);
            if (!string.IsNullOrWhiteSpace(ollamaText)) return ollamaText;
        }
        if (root.TryGetProperty("response", out var ollamaResponse) && TryGetText(ollamaResponse, out var responseText)) return responseText;

        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            var parts = output.EnumerateArray()
                .SelectMany(item => item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
                    ? content.EnumerateArray().Select(ExtractTextValue)
                    : [item.TryGetProperty("text", out var text) ? ExtractTextValue(text) : null])
                .Where(text => !string.IsNullOrWhiteSpace(text));
            var joined = string.Join(string.Empty, parts!);
            if (!string.IsNullOrWhiteSpace(joined)) return joined;
        }

        return null;
    }

    private static string? ExtractTextValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Array => string.Join(string.Empty, value.EnumerateArray()
            .Select(part => TryGetText(part, "text", out var text) ? text : null)
            .Where(text => !string.IsNullOrWhiteSpace(text))),
        JsonValueKind.Object when TryGetText(value, "text", out var text) => text,
        _ => null
    };

    private static bool TryGetText(JsonElement parent, string propertyName, out string? text)
    {
        text = null;
        return parent.TryGetProperty(propertyName, out var value) && TryGetText(value, out text);
    }

    private static bool TryGetText(JsonElement value, out string? text)
    {
        text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return !string.IsNullOrWhiteSpace(text);
    }

    private static async Task<string?> ReadErrorBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static string TrimForStatus(string text, int maxLength = 240) => text.Length <= maxLength ? text : text[..maxLength] + "...";

    private static bool IsOllamaApi(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return false;
        return uri.Port == 11434 || uri.AbsolutePath.Equals("/api", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildModelListUrl(string baseUrl, bool isOllama) => isOllama
        ? (baseUrl.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? baseUrl + "/tags" : baseUrl + "/api/tags")
        : (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? baseUrl + "/models" : baseUrl + "/v1/models");

    private static string BuildChatUrl(string baseUrl, bool isOllama) => isOllama
        ? (baseUrl.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? baseUrl + "/chat" : baseUrl + "/api/chat")
        : (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? baseUrl + "/chat/completions" : baseUrl + "/v1/chat/completions");

    /// <summary>
    /// Direct LocalAgent tool invoke. Works offline / without Hermes / without public Server.
    /// </summary>
    public Task<LocalAgentInvokeResult> InvokeLocalAgentToolAsync(
        string toolName,
        Dictionary<string, string>? arguments,
        bool userConfirmed,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        return _localAgentRuntime.InvokeToolAsync(
            toolName,
            arguments,
            userConfirmed,
            dryRun,
            cancellationToken);
    }

    private void OnTimelineCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsTimelineEmpty));
    }

    private async Task LoadStoredSessionAsync()
    {
        if (IsOfflineMode)
        {
            await HandleOfflineModeChangeAsync(true);
            return;
        }
        if (!RememberMe) return;
        try
        {
            if (await _backendClient.TryLoadStoredTokenAsync(CancellationToken.None))
            {
                var result = await _backendClient.GetMeDetailedAsync(CancellationToken.None);
                if (result.Ok && result.User is not null)
                {
                    await ApplyAuthenticatedUserAsync(result.User, CancellationToken.None);
                }
                else if (result.Reason == "not_authenticated")
                {
                    StatusText = "Kaydedilmiş oturum geçersiz. Lütfen tekrar giriş yapın.";
                }
                else
                {
                    StatusText = "Kaydedilmiş oturum doğrulanamadı.";
                    OrbState = "error";
                }
            }
        }
        catch
        {
            StatusText = "Kaydedilmis oturum okunamadi.";
            OrbState = "error";
        }
    }

    [RelayCommand]
    private void ContinueOffline()
    {
        ResetVoiceInteraction();
        AuthFormErrorText = string.Empty;
        IsRegisterScreenVisible = false;
        IsOfflineMode = true;
    }

    [RelayCommand]
    private async Task LoginAsync() => await AuthenticateAsync(preferRegister: false);

    [RelayCommand]
    private async Task RegisterAsync() => await AuthenticateAsync(preferRegister: true);

    [RelayCommand]
    private async Task ContinueWithGoogleAsync() => await AuthenticateWithProviderAsync("google");

    [RelayCommand]
    private async Task ContinueWithGitHubAsync() => await AuthenticateWithProviderAsync("github");

    [RelayCommand]
    private void ShowRegisterScreen()
    {
        ResetVoiceInteraction();
        AuthFormErrorText = string.Empty;
        IsRegisterScreenVisible = true;
        OnPropertyChanged(nameof(IsLoginMode));
        OnPropertyChanged(nameof(IsRegisterMode));
    }

    public string VoiceModeActionText => IsRecording ? "Konuşmayı Gönder" : "Konuşmayı Başlat";

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarCollapsed = !IsSidebarCollapsed;
    }

    [RelayCommand]
    private async Task ToggleVoiceModeMic()
    {
        if (IsTranscribing) return;
        if (IsRecording)
        {
            await StopRecordingAndTranscribeAndSendAsync(requireWakeWord: false);
        }
        else
        {
            await StartRecordingAsync();
        }
    }

    [RelayCommand]
    private void ShowAsistanTab()
    {
        IsTasksTabSelected = false;
        IsCreditsTabSelected = false;
    }

    [RelayCommand]
    private void ShowTasksTab()
    {
        IsTasksTabSelected = true;
    }

    [RelayCommand]
    private async Task ShowCreditsTabAsync()
    {
        IsCreditsTabSelected = true;
        await RefreshDirectChatCreditsAsync(CancellationToken.None);
    }

    private async Task RefreshDirectChatCreditsAsync(CancellationToken cancellationToken)
    {
        IsCreditStatusLoading = true;
        DirectChatCreditStatus = null;
        CreditStatusText = "Kredi bilgisi sunucudan yükleniyor.";
        var result = await _backendClient.GetDirectChatCreditStatusAsync(cancellationToken);
        IsCreditStatusLoading = false;
        DirectChatCreditStatus = result.Status;
        CreditStatusText = result.Reason == "ok" ? "Sunucu kredi bilgisi güncel." : $"Kredi bilgisi kullanılamıyor: {result.Reason}";
    }

    private void UpdateCreditCountdown()
    {
        if (DirectChatCreditStatus is null) { CreditCountdownText = "Sunucu kredi bilgisi bekleniyor."; _creditCountdownTimer.Stop(); return; }
        var remaining = DirectChatCreditStatus.ResetAtUtc - DateTimeOffset.UtcNow;
        CreditCountdownText = remaining <= TimeSpan.Zero ? "Yenilenme zamanı sunucudan doğrulanacak." : $"Yenilenmeye kalan: {remaining:hh\\:mm\\:ss}";
        if (!_creditCountdownTimer.IsEnabled) _creditCountdownTimer.Start();
    }

    [RelayCommand]
    private void ShowLoginScreen()
    {
        ResetVoiceInteraction();
        AuthFormErrorText = string.Empty;
        IsRegisterScreenVisible = false;
        OnPropertyChanged(nameof(IsLoginMode));
        OnPropertyChanged(nameof(IsRegisterMode));
    }

    [RelayCommand]
    private void OpenRegisterScreen()
    {
        ShowRegisterScreen();
    }

    partial void OnIsRegisterScreenVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLoginMode));
        OnPropertyChanged(nameof(IsRegisterMode));
        OnPropertyChanged(nameof(AuthScreenTitle));
        OnPropertyChanged(nameof(AuthScreenDescription));
    }

    [RelayCommand]
    private void CancelLogin()
    {
        _authCancellation?.Cancel();
        IsAuthenticating = false;
        StatusText = "Giris iptal edildi.";
        OrbState = "idle";
    }

    private async Task AuthenticateWithProviderAsync(string provider)
    {
        if (IsAuthenticating) return;
        var providerDisplayName = provider.Equals("github", StringComparison.OrdinalIgnoreCase) ? "GitHub" : "Google";
        _authCancellation = new CancellationTokenSource();
        IsAuthenticating = true;
        AuthFormErrorText = string.Empty;
        OrbState = "processing";
        StatusText = $"{providerDisplayName} ile giriş yapılıyor...";
        try
        {
            var user = await _authenticationService.SignInWithProviderAsync(provider, _authCancellation.Token);
            if (user is null)
            {
                OrbState = "error";
                AuthFormErrorText = $"{providerDisplayName} ile giriş tamamlanamadı. Lütfen tekrar deneyin.";
                StatusText = AuthFormErrorText;
                return;
            }
            await ApplyAuthenticatedUserAsync(user, _authCancellation.Token);
        }
        catch (OperationCanceledException) when (_authCancellation.IsCancellationRequested)
        {
            OrbState = "idle";
            AuthFormErrorText = string.Empty;
            await RunOnUiThreadAsync(() => StatusText = "Giriş iptal edildi.");
        }
        catch (Exception ex)
        {
            OrbState = "error";
            DesktopLogService.Error("AuthenticateWithProviderAsync exception yakaladı.", ex);
            AuthFormErrorText = $"{providerDisplayName} ile giriş başlatılamadı. Lütfen tekrar deneyin.";
            await RunOnUiThreadAsync(() => StatusText = AuthFormErrorText);
        }
        finally
        {
            IsAuthenticating = false;
        }
    }

    [RelayCommand]
    private void Logout()
    {
        StopCurrentOperationCore(addTimelineEvent: false);
        ResetVoiceInteraction();
        if (IsOfflineMode) IsOfflineMode = false;
        _backendClient.Logout();
        ClearLocalAgentDevices();
        LocalAgentDeviceStatusText = "Sunucuya bağlı Vortex Yapay Zeka Asistanı cihazları için giriş yapılmalıdır.";
        IsAuthenticated = false;
        CurrentUserText = string.Empty;
        HermesStatusText = "Hermes profili bekleniyor";
        StatusText = "Oturum kapatıldı.";
        OrbState = "offline";
        Chats.Clear();
        ActiveChat = null;
        Messages.Clear();
        Messages.Add(new MessageViewModel("Vortex", "Oturum kapatıldı."));
    }


    [RelayCommand(CanExecute = nameof(CanInstallKokoro))]
    private async Task InstallKokoroAsync()
    {
        if (IsKokoroInstalling) return;

        _isKokoroInstallPromptDismissed = false;
        IsKokoroInstalling = true;
        IsKokoroInstallFailed = false;
        KokoroInstallLog = string.Empty;
        IsKokoroInstallPromptVisible = true;
        KokoroStatusText = "Kokoro kurulumu başlatıldı...";
        InstallKokoroCommand.NotifyCanExecuteChanged();
        ConsultKokoroInstallFailureCommand.NotifyCanExecuteChanged();

        var installer = new KokoroInstallService();
        var progress = new Progress<KokoroInstallProgress>(p =>
        {
            _ = RunOnUiThreadAsync(() =>
            {
                var prefix = p.IsError ? "[hata] " : string.Empty;
                KokoroInstallLog = string.IsNullOrEmpty(KokoroInstallLog)
                    ? prefix + p.Line
                    : KokoroInstallLog + Environment.NewLine + prefix + p.Line;
            });
        });

        try
        {
            var ok = await installer.InstallAsync(progress, CancellationToken.None);
            await RunOnUiThreadAsync(() =>
            {
                if (ok)
                {
                    IsKokoroInstallFailed = false;
                    KokoroStatusText = "Kokoro kuruldu.";
                    StatusText = "Kokoro kuruldu.";
                }
                else
                {
                    _isKokoroInstallPromptDismissed = false;
                    IsKokoroInstallFailed = true;
                    KokoroStatusText = "Kokoro yerel TTS kurulumu başarısız oldu. Ayrıntılar için kurulum günlüğüne bakın; Yapay zeka asistanına danışabilirsiniz.";
                    StatusText = "Kokoro yerel TTS kurulumu başarısız oldu.";
                }

                RefreshKokoroInstallPrompt();
            });
        }
        catch (Exception ex)
        {
            DesktopLogService.Error("Kokoro kurulumu hata verdi.", ex);
            await RunOnUiThreadAsync(() =>
            {
                _isKokoroInstallPromptDismissed = false;
                IsKokoroInstallFailed = true;
                KokoroInstallLog = string.IsNullOrEmpty(KokoroInstallLog)
                    ? "[hata] " + ex.Message
                    : KokoroInstallLog + Environment.NewLine + "[hata] " + ex.Message;
                KokoroStatusText = "Kokoro kurulumu hata verdi: " + ex.Message;
                StatusText = "Kokoro kurulumu hata verdi.";
                RefreshKokoroInstallPrompt();
            });
        }
        finally
        {
            await RunOnUiThreadAsync(() =>
            {
                IsKokoroInstalling = false;
                InstallKokoroCommand.NotifyCanExecuteChanged();
                ConsultKokoroInstallFailureCommand.NotifyCanExecuteChanged();
                RefreshKokoroInstallPrompt();
            });
        }
    }

    public string KokoroInstallActionText => IsKokoroInstalling ? "Kurulum sürüyor…" : "Kur / İndir";
    private bool CanInstallKokoro() => !IsKokoroInstalling;

    private bool CanConsultKokoroInstallFailure() => !string.IsNullOrWhiteSpace(KokoroInstallLog);

    partial void OnIsKokoroInstallingChanged(bool value)
    {
        InstallKokoroCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(KokoroInstallActionText));
        ConsultKokoroInstallFailureCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsKokoroInstallFailedChanged(bool value)
    {
        ConsultKokoroInstallFailureCommand.NotifyCanExecuteChanged();
    }

    partial void OnKokoroInstallLogChanged(string value)
    {
        ConsultKokoroInstallFailureCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Opens main chat and auto-sends the Kokoro install error log so the assistant can help.
    /// </summary>
    [ObservableProperty] private string whisperInstallLog = string.Empty;
    [ObservableProperty] private bool isWhisperInstalling;
    [ObservableProperty] private bool isWhisperInstallFailed;
    [ObservableProperty] private string whisperStatusText = "Whisper durumu izleniyor.";

    private bool CanConsultWhisperInstallFailure() => !string.IsNullOrWhiteSpace(WhisperInstallLog);

    partial void OnIsWhisperInstallingChanged(bool value)
    {
        ConsultWhisperInstallFailureCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsWhisperInstallFailedChanged(bool value)
    {
        ConsultWhisperInstallFailureCommand.NotifyCanExecuteChanged();
    }

    partial void OnWhisperInstallLogChanged(string value)
    {
        ConsultWhisperInstallFailureCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanConsultWhisperInstallFailure))]
    private async Task ConsultWhisperInstallFailureAsync()
    {
        if (!CanConsultWhisperInstallFailure()) return;

        var log = WhisperInstallLog ?? string.Empty;
        if (log.Length > 3000) log = log[^3000..];
        if (string.IsNullOrWhiteSpace(log))
        {
            log = "Whisper model indirme günlüğü boş geldi. İndirme başarısız ya da donmuş olarak işaretlendi ama ayrıntılı günlük yakalanamadı.";
        }

        var prompt =
            "Whisper.cpp model indirme işlemi başarısız oldu veya dondu. Lütfen nedeni analiz et; ağ, disk alanı, izinler ve model URL'si açısından kontrol et. Güvenli çözüm adımlarını ayrı kod blokları halinde ver.\n\n" +
            "İndirme günlüğü:\n---\n" + log + "\n---";

        const string helpTitle = "Whisper indirme yardımı";
        try
        {
            if (IsOfflineMode)
            {
                var id = Guid.NewGuid();
                await _localDb.CreateSessionAsync(id, helpTitle, CancellationToken.None);
                var vm = new ChatSessionViewModel(id, helpTitle, DateTimeOffset.Now);
                await RunOnUiThreadAsync(() =>
                {
                    Chats.Insert(0, vm);
                    foreach (var c in Chats) c.IsSelected = c.Id == vm.Id;
                    ActiveChat = vm;
                    Messages.Clear();
                    Messages.Add(new MessageViewModel("Vortex", "Whisper indirme hatasını paylaşıyorsunuz. Yardım yanıtı hazırlanıyor..."));
                });
            }
            else if (IsAuthenticated)
            {
                var createResult = await _backendClient.CreateChatAsync(helpTitle, CancellationToken.None);
                if (createResult.Ok && createResult.Chat is not null)
                {
                    var session = createResult.Chat;
                    var vm = new ChatSessionViewModel(session.Id, session.Title, session.UpdatedAt);
                    await RunOnUiThreadAsync(() =>
                    {
                        Chats.Insert(0, vm);
                        foreach (var c in Chats) c.IsSelected = c.Id == vm.Id;
                        ActiveChat = vm;
                        Messages.Clear();
                        Messages.Add(new MessageViewModel("Vortex", "Whisper indirme hatasını paylaşıyorsunuz. Yardım yanıtı hazırlanıyor..."));
                    });
                    await PersistActiveChatStateNonfatalAsync(vm.Id, "Whisper yardım sohbeti");
                }
            }
        }
        catch (Exception ex)
        {
            DesktopLogService.Error("Whisper yardım sohbeti oluşturulamadı.", ex);
        }

        await RunOnUiThreadAsync(() =>
        {
            InputText = prompt;
            StatusText = "Whisper indirme günlüğü asistanına gönderiliyor...";
        });

        await SendAsync();
    }

    [RelayCommand(CanExecute = nameof(CanConsultKokoroInstallFailure))]
    private async Task ConsultKokoroInstallFailureAsync()
    {
        if (!CanConsultKokoroInstallFailure()) return;

        var log = KokoroInstallLog ?? string.Empty;
        if (log.Length > 1200) log = log[^1200..];
        if (string.IsNullOrWhiteSpace(log)) log = "Kurulum günlüğü henüz oluşturulmadı.";

        var prompt =
            "Kokoro yerel TTS kurulumu için yardım istiyorum. Aşağıdaki kurulum günlüğünün son bölümünü analiz et. " +
            "Olası nedeni ve güvenli, uygulanabilir çözüm adımlarını kısa biçimde ver.\n\n" +
            "Kurulum günlüğünün son bölümü:\n---\n" + log + "\n---";

        _isKokoroInstallPromptDismissed = true;
        IsKokoroInstallPromptVisible = false;
        IsWelcomeVisible = false;
        const string helpTitle = "Kokoro kurulum yardımı";
        try
        {
            if (IsOfflineMode)
            {
                var id = Guid.NewGuid();
                await _localDb.CreateSessionAsync(id, helpTitle, CancellationToken.None);
                var vm = new ChatSessionViewModel(id, helpTitle, DateTimeOffset.Now);
                await RunOnUiThreadAsync(() =>
                {
                    Chats.Insert(0, vm);
                    foreach (var chat in Chats) chat.IsSelected = chat.Id == vm.Id;
                    ActiveChat = vm;
                    Messages.Clear();
                    Messages.Add(new MessageViewModel("Vortex", "Kokoro kurulum günlüğü asistanla paylaşılmaya hazırlanıyor..."));
                });
            }
            else if (IsAuthenticated)
            {
                var createResult = await _backendClient.CreateChatAsync(helpTitle, CancellationToken.None);
                if (createResult.Ok && createResult.Chat is not null)
                {
                    var session = createResult.Chat;
                    var vm = new ChatSessionViewModel(session.Id, session.Title, session.UpdatedAt);
                    await RunOnUiThreadAsync(() =>
                    {
                        Chats.Insert(0, vm);
                        foreach (var chat in Chats) chat.IsSelected = chat.Id == vm.Id;
                        ActiveChat = vm;
                        Messages.Clear();
                        Messages.Add(new MessageViewModel("Vortex", "Kokoro kurulum günlüğü asistanla paylaşılmaya hazırlanıyor..."));
                    });
                    await PersistActiveChatStateNonfatalAsync(vm.Id, "Kokoro yardım sohbeti");
                }
            }
            else
            {
                IsOfflineMode = true;
                var id = Guid.NewGuid();
                await _localDb.CreateSessionAsync(id, helpTitle, CancellationToken.None);
                var vm = new ChatSessionViewModel(id, helpTitle, DateTimeOffset.Now);
                await RunOnUiThreadAsync(() =>
                {
                    Chats.Insert(0, vm);
                    foreach (var chat in Chats) chat.IsSelected = chat.Id == vm.Id;
                    ActiveChat = vm;
                    Messages.Clear();
                    Messages.Add(new MessageViewModel("Vortex", "Kokoro kurulum günlüğü asistanla paylaşılmaya hazırlanıyor..."));
                });
            }
        }
        catch (Exception ex)
        {
            DesktopLogService.Error("Kokoro yardım sohbeti oluşturulamadı.", ex);
        }

        await RunOnUiThreadAsync(() =>
        {
            InputText = prompt;
            StatusText = "Kokoro kurulum özeti asistanına gönderiliyor...";
        });

        await SendAsync();
    }

    [RelayCommand]
    private void OpenKokoroDocs()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = KokoroInstallService.DocsUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            DesktopLogService.Error("Kokoro belgeler sayfası açılamadı.", ex);
            KokoroStatusText = "Belgeler açılamadı; " + KokoroInstallService.DocsUrl + " adresini elle açın.";
        }
    }

    [RelayCommand]
    private void DismissKokoroInstallPrompt()
    {
        _isKokoroInstallPromptDismissed = true;
        IsKokoroInstallPromptVisible = false;
    }

    private SettingsWindow? _settingsWindow;

    [RelayCommand]
    private void OpenSettings()
    {
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (_settingsWindow is not null)
            {
                _settingsWindow.Activate();
                return;
            }

            var owner = desktop.MainWindow;
            _settingsWindow = new SettingsWindow
            {
                DataContext = new SettingsWindowViewModel(_backendClient, this)
            };
            _settingsWindow.Closed += (s, e) => _settingsWindow = null;

            if (owner is not null)
            {
                _settingsWindow.Show(owner);
            }
            else
            {
                _settingsWindow.Show();
            }
        }
    }

    [RelayCommand]
    private async Task CopyMessageAsync(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                var clipboard = desktop.MainWindow?.Clipboard;
                if (clipboard is not null)
                {
                    await clipboard.SetTextAsync(text);
                }
            }
        }
        catch (Exception ex)
        {
            DesktopLogService.Error("Klipboard kopyalaması başarısız.", ex);
        }
    }

    [RelayCommand]
    private async Task OfferLocalAgentForCommandAsync(string? command)
    {
        var trimmed = (command ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return;

        AddTimelineEvent("local_agent_offer", trimmed.Length > 120 ? trimmed[..120] + "..." : trimmed);

        var intent = LocalAgentIntentRouter.TryMatch("run " + trimmed);
        if (!intent.IsSystemAction)
        {
            intent = new LocalAgentIntent(
                true,
                "run_cmd",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["command"] = trimmed },
                true,
                false,
                trimmed,
                "Komut bloğunu çalıştır");
        }

        await SendLocalAgentSystemActionAsync(trimmed, intent);
    }

    [RelayCommand]
    private async Task ApproveAssistantLocalAgentOfferAsync(MessageViewModel? message)
    {
        if (message is null || !message.HasLocalAgentOffer || string.IsNullOrWhiteSpace(message.LocalAgentOfferCommand)) return;
        if (IsOfflineMode || !IsAuthenticated)
        {
            LocalAgentDeviceStatusText = "Sunucuya bağlı Vortex Yapay Zeka Asistanı eylemi için giriş yapılmalıdır.";
            AddTimelineEvent("request_blocked", "Sunucuya bağlı Vortex Yapay Zeka Asistanı eylemi çevrimdışı modda kullanılamaz.");
            return;
        }

        var selectedDeviceId = SelectedLocalAgentDevice?.Id;
        await RefreshLocalAgentDevicesAsync();
        var device = SelectedLocalAgentDevice;
        if ((selectedDeviceId is Guid expectedId && device?.Id != expectedId) ||
            device is null ||
            !RegisteredLocalAgentDevices.Any(registered => registered.Id == device.Id))
        {
            AddTimelineEvent("no_device_available", LocalAgentDeviceStatusText);
            return;
        }

        var command = message.LocalAgentOfferCommand.Trim();
        var intent = LocalAgentIntentRouter.TryMatch(command);
        if (!intent.IsSystemAction)
        {
            intent = new LocalAgentIntent(
                true,
                "run_cmd",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["command"] = command },
                true,
                false,
                command,
                "Komut bloğunu çalıştır");
        }

        message.ClearLocalAgentOffer();
        AddTimelineEvent("local_agent_server_queue", $"device={device.DeviceName} tool={intent.ToolName}");
        var queued = await TryQueueLocalAgentToolAsync(
            device.Id,
            intent.ToolName,
            intent.Arguments,
            dryRun: false,
            fallbackCommand: intent.FallbackCommand);
        if (queued is not null)
        {
            StatusText = $"Vortex Yapay Zeka Asistanı eylemi {device.DeviceName} cihazı için kuyruğa alındı.";
        }
    }

    private static void AttachExplicitLocalAgentOffer(MessageViewModel assistant)
    {
        if (!assistant.IsAssistant || assistant.HasLocalAgentOffer) return;
        var proposals = assistant.CodeBlocks
            .Where(block => string.Equals(block.Language, "localagent", StringComparison.OrdinalIgnoreCase) || string.Equals(block.Language, "vortex-local-agent", StringComparison.OrdinalIgnoreCase))
            .Select(block => block.Content.Trim())
            .Where(command => !string.IsNullOrWhiteSpace(command))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (proposals.Count != 1) return;

        var command = proposals[0];
        var intent = LocalAgentIntentRouter.TryMatch(command);
        if (!intent.IsSystemAction)
        {
            assistant.SetLocalAgentOffer("Asistan yerel bir komut önerdi.", "Bu serbest komut yalnız açık izninizden sonra dry-run/onay akışına gönderilir.", command);
            return;
        }

        assistant.SetLocalAgentOffer(
            $"Asistan yerel eylem önerdi: {intent.Summary}",
            intent.RequiresConfirmation ? "Bu eylem çalışmadan önce dry-run ve ikinci onay gerektirir." : "Bu önceden tanımlı Vortex Yapay Zeka Asistanı eylemi yalnız izninizden sonra çalışır.",
            command);
    }

    private async Task AuthenticateAsync(bool preferRegister)
    {
        if (IsAuthenticating) return;

        if (preferRegister)
        {
            if (string.IsNullOrWhiteSpace(FirstName) || string.IsNullOrWhiteSpace(LastName) || string.IsNullOrWhiteSpace(DisplayName))
            {
                AuthFormErrorText = "Kayıt için ad, soyad ve görünen ad gerekli.";
                return;
            }
            if (string.IsNullOrWhiteSpace(Email))
            {
                AuthFormErrorText = "Kayıt için e-posta gerekli.";
                return;
            }
            if (string.IsNullOrWhiteSpace(Password))
            {
                AuthFormErrorText = "Kayıt için parola gerekli.";
                return;
            }
            if (string.IsNullOrWhiteSpace(BirthDate))
            {
                AuthFormErrorText = "Kayıt için doğum tarihi gerekli.";
                return;
            }
            if (Password != ConfirmPassword)
            {
                AuthFormErrorText = "Parolalar eşleşmiyor.";
                return;
            }
        }
        else if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
        {
            AuthFormErrorText = "Giriş için e-posta ve parola gerekli.";
            return;
        }

        _authCancellation = new CancellationTokenSource();
        IsAuthenticating = true;
        AuthFormErrorText = string.Empty;
        OrbState = "processing";
        StatusText = preferRegister ? "Kayit yapiliyor..." : "Giris yapiliyor...";
        try
        {
            var user = preferRegister
                ? await _authenticationService.RegisterAsync(Email.Trim(), Password, DisplayName.Trim(), FirstName.Trim(), LastName.Trim(), BirthDate.Trim(), string.IsNullOrWhiteSpace(PhoneNumber) ? null : PhoneNumber.Trim(), _authCancellation.Token)
                : await _authenticationService.SignInWithPasswordAsync(Email.Trim(), Password, _authCancellation.Token);
            if (user is null)
            {
                OrbState = "error";
                AuthFormErrorText = preferRegister ? "Kayit tamamlanamadi." : "Giris tamamlanamadi.";
                StatusText = AuthFormErrorText;
                return;
            }
            await ApplyAuthenticatedUserAsync(user, _authCancellation.Token);
        }
        catch (LoginFailureException ex) when (!preferRegister)
        {
            OrbState = "error";
            AuthFormErrorText = ex.Reason switch
            {
                "invalid_credentials" => "E-posta veya parola hatalı.",
                "server_error" => string.IsNullOrWhiteSpace(ex.CorrelationId)
                    ? "Giriş hizmeti şu anda kullanılamıyor."
                    : $"Giriş hizmeti şu anda kullanılamıyor. Başvuru kodu: {ex.CorrelationId}",
                _ => "Giriş işlemi şu anda tamamlanamıyor. Lütfen tekrar deneyin."
            };
            await RunOnUiThreadAsync(() => StatusText = AuthFormErrorText);
        }
        catch (Exception ex)
        {
            OrbState = "error";
            DesktopLogService.Error("AuthenticateAsync catch blogu exception ayrintisini yakaladi.", ex);
            AuthFormErrorText = preferRegister
                ? "Kayıt işlemi başarısız. Lütfen tekrar deneyin."
                : "Giriş işlemi şu anda tamamlanamıyor. Lütfen tekrar deneyin.";
            await RunOnUiThreadAsync(() => StatusText = AuthFormErrorText);
        }
        finally
        {
            IsAuthenticating = false;
        }
    }

    private async Task ApplyAuthenticatedUserAsync(UserProfileDto user, CancellationToken cancellationToken)
    {
        await RunOnUiThreadAsync(() =>
        {
            IsOfflineMode = false;
            IsAuthenticated = true;
            IsWelcomeVisible = false;
            CurrentUserText = $"{user.DisplayName} / {user.PlanName}";
            StatusText = CurrentUserText;
            HermesStatusText = "Hermes profili kontrol ediliyor...";
            OrbState = "processing";
            RefreshKokoroInstallPrompt();
        });

        DesktopLogService.Info("9. Avalonia ViewModel giriş durumuna geçirildi.");
        DesktopLogService.Info($"10. Karşılama ekranı kapandı ve ana sohbet ekranı açıldı. IsAuthenticated={IsAuthenticated}, IsWelcomeVisible={IsWelcomeVisible}.");

        try
        {
            var result = await _backendClient.GetAgentStatusDetailedAsync(cancellationToken);
            await RunOnUiThreadAsync(() =>
            {
                if (!result.Ok || result.Status is null)
                {
                    HermesStatusText = BackendClient.GetAgentStatusFailureMessageForUi(result.Reason);
                    OrbState = "error";
                    return;
                }

                HermesStatusText = result.Status.Profile is null
                    ? "Hermes profili yok"
                    : $"Hermes: {result.Status.Profile.Status} / Kalan agent hakki: {result.Status.RemainingRunsToday}";
                OrbState = "idle";
            });
        }
        catch
        {
            await RunOnUiThreadAsync(() =>
            {
                HermesStatusText = "Hermes durumu alinamadi";
                OrbState = "error";
            });
        }

        await RefreshLocalAgentDevicesAsync();
        await RefreshScheduledTasksAsync();
        await LoadChatsAsync(null);
        await RestoreActiveChatSelectionAsync(cancellationToken);
        await RefreshDirectChatCreditsAsync(cancellationToken);
    }

    private async Task RestoreActiveChatSelectionAsync(CancellationToken cancellationToken)
    {
        var state = await _backendClient.GetActiveChatStateAsync(cancellationToken);
        ChatSessionViewModel? target = null;

        if (state?.ActiveChatSessionId is Guid activeChatId)
        {
            target = Chats.FirstOrDefault(chat => chat.Id == activeChatId);
        }

        target ??= Chats.FirstOrDefault();

        if (target is not null)
        {
            await SelectChatAsync(target, syncServerState: true);
            return;
        }

        await _backendClient.SetActiveChatStateAsync(null, cancellationToken);
        await RunOnUiThreadAsync(() =>
        {
            foreach (var chat in Chats) chat.IsSelected = false;
            ActiveChat = null;
            Messages.Clear();
            Messages.Add(new MessageViewModel("Vortex", "Henüz sohbet yok."));
        });
    }

    private static async Task RunOnUiThreadAsync(Action action)
    {
        if (Application.Current is null || Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(action);
    }

    private static async Task RunOnUiThreadAsync(Func<Task> action)
    {
        if (Application.Current is null || Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            await action();
            return;
        }
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(action);
    }

    [RelayCommand]
    private async Task NewChatAsync()
    {
        if (IsOfflineMode)
        {
            ChatsStatusText = "Yerel sohbet olusturuluyor...";
            try
            {
                var id = Guid.NewGuid();
                var title = "Yerel Sohbet";
                await _localDb.CreateSessionAsync(id, title, CancellationToken.None);
                var vm = new ChatSessionViewModel(id, title, DateTimeOffset.Now);
                await RunOnUiThreadAsync(() =>
                {
                    Chats.Insert(0, vm);
                    foreach (var c in Chats) c.IsSelected = c.Id == vm.Id;
                    ActiveChat = vm;
                    Messages.Clear();
                    Messages.Add(new MessageViewModel("Vortex", "Yerel çevrimdışı sohbet hazır."));
                    OrbState = "idle";
                    ChatsStatusText = string.Empty;
                });
            }
            catch (Exception ex)
            {
                DesktopLogService.Error("Yerel sohbet olusturulamadi.", ex);
                ChatsStatusText = "Yerel sohbet olusturulamadi.";
            }
            return;
        }
        if (!IsAuthenticated)
        {
            Messages.Clear();
            Messages.Add(new MessageViewModel("Vortex", "Önce web üzerinden giriş yapın."));
            OrbState = "offline";
            return;
        }
        ChatsStatusText = "Yeni sohbet olusturuluyor...";
        var result = await _backendClient.CreateChatAsync(null, CancellationToken.None);
        if (!result.Ok || result.Chat is null)
        {
            ChatsStatusText = BackendClient.GetCreateChatFailureMessageForUi(result.Reason);
            DesktopLogService.Error($"Yeni sohbet olusturulamadi. reason={result.Reason}; status={(int?)result.StatusCode}");
            return;
        }

        var session = result.Chat;
        var onlineChat = new ChatSessionViewModel(session.Id, session.Title, session.UpdatedAt);
        await RunOnUiThreadAsync(() =>
        {
            Chats.Insert(0, onlineChat);
            foreach (var c in Chats) c.IsSelected = c.Id == onlineChat.Id;
            ActiveChat = onlineChat;
            Messages.Clear();
            Messages.Add(new MessageViewModel("Vortex", "Yeni sohbet hazır. Bir mesaj gönderdiğinde başlık otomatik oluşturulur."));
            OrbState = "idle";
            ChatsStatusText = string.Empty;
        });

        await PersistActiveChatStateNonfatalAsync(onlineChat.Id, "Yeni sohbet");
    }

    private async Task PersistActiveChatStateNonfatalAsync(Guid chatId, string operation)
    {
        try
        {
            if (!await _backendClient.SetActiveChatStateAsync(chatId, CancellationToken.None))
            {
                DesktopLogService.Error($"{operation} aktif durumu kaydedilemedi. reason=http_error");
            }
        }
        catch
        {
            DesktopLogService.Error($"{operation} aktif durumu kaydedilemedi. reason=transport_error");
        }
    }

    private async Task LoadChatsAsync(string? query, bool archived = false)
    {
        if (IsOfflineMode)
        {
            if (IsLoadingChats) return;
            IsLoadingChats = true;
            ChatsStatusText = archived ? "Arşivlenmiş sohbetler yükleniyor..." : "Sohbetler yükleniyor...";
            try
            {
                var sessions = archived ? await _localDb.ListArchivedSessionsAsync(CancellationToken.None) : await _localDb.ListSessionsAsync(CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(query))
                {
                    var filtered = new List<ChatSessionDto>();
                    foreach (var s in sessions)
                    {
                        if (s.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            filtered.Add(s);
                            continue;
                        }
                        var msgs = await _localDb.GetMessagesAsync(s.Id, CancellationToken.None);
                        if (msgs.Any(m => m.Content.Contains(query, StringComparison.OrdinalIgnoreCase)))
                        {
                            filtered.Add(s);
                        }
                    }
                    sessions = filtered;
                }
                await RunOnUiThreadAsync(() =>
                {
                    Chats.Clear();
                    foreach (var s in sessions)
                    {
                        var vm = new ChatSessionViewModel(s.Id, s.Title, s.UpdatedAt, s.IsFavorite);
                        if (ActiveChat is { } active && active.Id == vm.Id) vm.IsSelected = true;
                        Chats.Add(vm);
                    }
                    ChatsStatusText = Chats.Count == 0
                        ? (archived ? "Arşivlenmiş sohbet yok." : "Henüz sohbet yok.")
                        : string.Empty;
                });
            }
            catch (Exception ex)
            {
                DesktopLogService.Error("Yerel sohbet listesi yüklenemedi.", ex);
                await RunOnUiThreadAsync(() => ChatsStatusText = "Sohbet listesi yüklenemedi.");
            }
            finally
            {
                IsLoadingChats = false;
            }
            return;
        }
        if (!IsAuthenticated || IsLoadingChats) return;
        IsLoadingChats = true;
        ChatsStatusText = archived ? "Arşivlenmiş sohbetler yükleniyor..." : "Sohbetler yükleniyor...";
        try
        {
            var result = await _backendClient.ListChatsDetailedAsync(query, archived, CancellationToken.None);
            await RunOnUiThreadAsync(() =>
            {
                if (!result.Ok)
                {
                    ChatsStatusText = BackendClient.GetChatListFailureMessageForUi(result.Reason);
                    return;
                }

                Chats.Clear();
                foreach (var s in result.Chats!)
                {
                    var vm = new ChatSessionViewModel(s.Id, s.Title, s.UpdatedAt, s.IsFavorite);
                    vm.Update(s.Title, s.UpdatedAt);
                    if (ActiveChat is { } active && active.Id == vm.Id) vm.IsSelected = true;
                    Chats.Add(vm);
                }
                ChatsStatusText = Chats.Count == 0
                    ? (archived ? "Arşivlenmiş sohbet yok." : "Henüz sohbet yok.")
                    : string.Empty;
            });
        }
        catch (Exception ex)
        {
            DesktopLogService.Error("Sohbet listesi yüklenemedi.", ex);
            await RunOnUiThreadAsync(() => ChatsStatusText = "Sohbet listesi yüklenemedi.");
        }
        finally
        {
            IsLoadingChats = false;
        }
    }

    public async Task SelectChatAsync(ChatSessionViewModel chat, bool syncServerState = true)
    {
        if (chat is null) return;
        foreach (var c in Chats) c.IsSelected = c.Id == chat.Id;
        ActiveChat = chat;
        if (IsOfflineMode)
        {
            Messages.Clear();
            try
            {
                var messages = await _localDb.GetMessagesAsync(chat.Id, CancellationToken.None);
                if (messages is null || messages.Count == 0)
                {
                    Messages.Add(new MessageViewModel("Vortex", "Bu yerel sohbette henüz mesaj yok."));
                    return;
                }
                foreach (var m in messages)
                {
                    var role = m.Role switch
                    {
                        "user" => "Kullanıcı",
                        "assistant" => "Asistan",
                        "system" => "Sistem",
                        _ => m.Role
                    };
                    Messages.Add(new MessageViewModel(role, m.Content));
                }
            }
            catch (Exception ex)
            {
                DesktopLogService.Error("Yerel sohbet mesajları yüklenemedi.", ex);
                Messages.Add(new MessageViewModel("Vortex", "Yerel sohbet mesajları yüklenemedi."));
            }
            return;
        }
        if (syncServerState)
        {
            await _backendClient.SetActiveChatStateAsync(chat.Id, CancellationToken.None);
        }
        Messages.Clear();
        try
        {
            var messages = await _backendClient.GetChatMessagesAsync(chat.Id, CancellationToken.None);
            if (messages is null || messages.Count == 0)
            {
                Messages.Add(new MessageViewModel("Vortex", "Bu sohbette henüz mesaj yok."));
                return;
            }
            foreach (var m in messages)
            {
                var role = m.Role switch
                {
                    "user" => "Kullanıcı",
                    "assistant" => "Asistan",
                    "system" => "Sistem",
                    _ => m.Role
                };
                if (!string.IsNullOrWhiteSpace(m.ErrorMessage))
                {
                    Messages.Add(new MessageViewModel("Vortex", $"Hata: {m.ErrorMessage}"));
                }
                else if (!string.IsNullOrWhiteSpace(m.Content))
                {
                    Messages.Add(new MessageViewModel(role, m.Content));
                }
            }
        }
        catch (Exception ex)
        {
            DesktopLogService.Error("Sohbet mesajları yüklenemedi.", ex);
            Messages.Add(new MessageViewModel("Vortex", "Sohbet mesajları yüklenemedi."));
        }
    }

    public Task RefreshChatsAsync() => LoadChatsAsync(SearchText);

    [RelayCommand]
    private void SelectChat(ChatSessionViewModel? chat)
    {
        if (chat is null) return;
        _ = SelectChatAsync(chat);
    }

    [RelayCommand]
    private void StartRenameChat(ChatSessionViewModel? chat)
    {
        if (chat is null) return;
        chat.EditTitle = chat.Title;
        chat.IsEditing = true;
    }

    [RelayCommand]
    private void CancelRenameChat(ChatSessionViewModel? chat)
    {
        if (chat is null) return;
        chat.IsEditing = false;
    }

    [RelayCommand]
    private async Task CommitRenameChatAsync(ChatSessionViewModel? chat)
    {
        if (chat is null) return;
        var newTitle = chat.EditTitle?.Trim();
        if (string.IsNullOrWhiteSpace(newTitle))
        {
            chat.IsEditing = false;
            return;
        }
        if (IsOfflineMode)
        {
            try
            {
                await _localDb.RenameSessionAsync(chat.Id, newTitle, CancellationToken.None);
                chat.Title = newTitle;
            }
            catch (Exception ex)
            {
                DesktopLogService.Error("Yerel sohbet yeniden adlandirilamadi.", ex);
                ChatsStatusText = "Yerel sohbet yeniden adlandirilamadi.";
            }
        }
        else
        {
            var ok = await _backendClient.RenameChatAsync(chat.Id, newTitle, CancellationToken.None);
            if (ok)
            {
                chat.Title = newTitle;
            }
            else
            {
                ChatsStatusText = "Sohbet yeniden adlandirilamadi.";
            }
        }
        chat.IsEditing = false;
        await LoadChatsAsync(SearchText);
        await RestoreActiveChatSelectionAsync(CancellationToken.None);
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync(ChatSessionViewModel? chat)
    {
        if (chat is null) return;
        var newFavoriteState = !chat.IsFavorite;
        if (IsOfflineMode)
        {
            try
            {
                await _localDb.FavoriteSessionAsync(chat.Id, newFavoriteState, CancellationToken.None);
                chat.IsFavorite = newFavoriteState;
                await LoadChatsAsync(SearchText);
                await RestoreActiveChatSelectionAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                DesktopLogService.Error("Yerel sohbet favorilere eklenemedi.", ex);
                ChatsStatusText = "Yerel sohbet favorilere eklenemedi.";
            }
            return;
        }
        var ok = await _backendClient.FavoriteChatAsync(chat.Id, newFavoriteState, CancellationToken.None);
        if (ok)
        {
            chat.IsFavorite = newFavoriteState;
            await LoadChatsAsync(SearchText);
            await RestoreActiveChatSelectionAsync(CancellationToken.None);
        }
        else
        {
            ChatsStatusText = "Sohbet favorilere eklenemedi.";
        }
    }

    [RelayCommand]
    private async Task ArchiveChatAsync(ChatSessionViewModel? chat)
    {
        if (chat is null) return;
        if (IsOfflineMode)
        {
            try
            {
                await _localDb.ArchiveSessionAsync(chat.Id, true, CancellationToken.None);
                await RunOnUiThreadAsync(() =>
                {
                    Chats.Remove(chat);
                    if (ActiveChat is { } active && active.Id == chat.Id)
                    {
                        ActiveChat = null;
                        Messages.Clear();
                        Messages.Add(new MessageViewModel("Vortex", "Yerel sohbet arsivlendi."));
                    }
                });
                await LoadChatsAsync(SearchText);
                await RestoreActiveChatSelectionAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                DesktopLogService.Error("Yerel sohbet arsivlenemedi.", ex);
                ChatsStatusText = "Yerel sohbet arsivlenemedi.";
            }
            return;
        }
        var ok = await _backendClient.ArchiveChatAsync(chat.Id, true, CancellationToken.None);
        if (ok)
        {
            await RunOnUiThreadAsync(() =>
            {
                Chats.Remove(chat);
                if (ActiveChat is { } active && active.Id == chat.Id)
                {
                    ActiveChat = null;
                    Messages.Clear();
                    Messages.Add(new MessageViewModel("Vortex", "Sohbet arsivlendi. Ayarlar > Archived Chats altindan geri alabilirsiniz."));
                }
            });
            await LoadChatsAsync(SearchText);
            await RestoreActiveChatSelectionAsync(CancellationToken.None);
        }
        else
        {
            ChatsStatusText = "Sohbet arsivlenemedi.";
        }
    }

    [RelayCommand]
    private async Task UnarchiveChatAsync(ChatSessionViewModel? chat)
    {
        if (chat is null) return;
        if (IsOfflineMode)
        {
            try
            {
                await _localDb.ArchiveSessionAsync(chat.Id, false, CancellationToken.None);
                await LoadChatsAsync(SearchText, archived: false);
            }
            catch (Exception ex)
            {
                DesktopLogService.Error("Yerel sohbet arşivden çıkarılamadı.", ex);
            }
            return;
        }
        var ok = await _backendClient.ArchiveChatAsync(chat.Id, false, CancellationToken.None);
        if (ok)
        {
            await LoadChatsAsync(SearchText, archived: false);
        }
    }

    [RelayCommand]
    private async Task DeleteChatAsync(ChatSessionViewModel? chat)
    {
        if (chat is null) return;
        if (IsOfflineMode)
        {
            try
            {
                await _localDb.DeleteSessionAsync(chat.Id, CancellationToken.None);
                await RunOnUiThreadAsync(() =>
                {
                    Chats.Remove(chat);
                    if (ActiveChat is { } active && active.Id == chat.Id)
                    {
                        ActiveChat = null;
                        Messages.Clear();
                        Messages.Add(new MessageViewModel("Vortex", "Yerel sohbet silindi."));
                    }
                });
                await LoadChatsAsync(SearchText);
                await RestoreActiveChatSelectionAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                DesktopLogService.Error("Yerel sohbet silinemedi.", ex);
                ChatsStatusText = "Yerel sohbet silinemedi.";
            }
            return;
        }
        var ok = await _backendClient.DeleteChatAsync(chat.Id, CancellationToken.None);
        if (!ok)
        {
            ChatsStatusText = "Sohbet silinemedi.";
            return;
        }
        await RunOnUiThreadAsync(() =>
        {
            Chats.Remove(chat);
            if (ActiveChat is { } active && active.Id == chat.Id)
            {
                ActiveChat = null;
                Messages.Clear();
                Messages.Add(new MessageViewModel("Vortex", "Sohbet silindi."));
            }
        });
        await LoadChatsAsync(SearchText);
        await RestoreActiveChatSelectionAsync(CancellationToken.None);
    }

    [RelayCommand]
    public async Task SendAsync()
    {
        var text = InputText.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusText = "Mesaj boş.";
            return;
        }

        // LocalAgent sieve first. Online authenticated system actions use the selected owner-bound Server queue;
        // offline mode retains the separate direct localhost LocalAgent path.
        var intent = LocalAgentIntentRouter.TryMatch(text);
        if (intent.IsSystemAction)
        {
            if (!IsOfflineMode && IsAuthenticated)
            {
                await QueueServerLocalAgentSystemActionAsync(text, intent);
            }
            else
            {
                await SendLocalAgentSystemActionAsync(text, intent);
            }
            return;
        }

        if (IsOfflineMode)
        {
            var localChat = ActiveChat;
            if (localChat is null)
            {
                try
                {
                    var id = Guid.NewGuid();
                    var title = text.Length > 20 ? text[..20] + "..." : text;
                    await _localDb.CreateSessionAsync(id, title, CancellationToken.None);
                    localChat = new ChatSessionViewModel(id, title, DateTimeOffset.Now);
                    await RunOnUiThreadAsync(() =>
                    {
                        Chats.Insert(0, localChat!);
                        foreach (var c in Chats) c.IsSelected = c.Id == localChat!.Id;
                        ActiveChat = localChat;
                    });
                }
                catch (Exception ex)
                {
                    DesktopLogService.Error("Yerel sohbet olusturulamadi.", ex);
                    return;
                }
            }

            IsAgentBusy = true;
            CanStopCurrentOperation = true;
            OrbState = "processing";
            InputText = string.Empty;
            Messages.Add(new MessageViewModel("Kullanıcı", text));
            var localAssistant = new MessageViewModel("Asistan", "Düşünüyor...");
            Messages.Add(localAssistant);
            AddTimelineEvent("request_received", text);

            try
            {
                await _localDb.AppendMessageAsync(localChat.Id, "user", text, null, CancellationToken.None);

                string? offlineReply = null;
                string? offlineFailureReason = null;
                if (!string.IsNullOrWhiteSpace(LocalLlmBaseUrl))
                {
                    try
                    {
                        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                        if (!string.IsNullOrWhiteSpace(LocalLlmApiKey))
                        {
                            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalLlmApiKey);
                        }

                        var url = LocalLlmBaseUrl.TrimEnd('/');
                        var usesOllamaChat = false;
                        if (!url.Contains("/v1", StringComparison.OrdinalIgnoreCase) && !url.Contains("/api", StringComparison.OrdinalIgnoreCase))
                        {
                            usesOllamaChat = url.Contains(":11434", StringComparison.OrdinalIgnoreCase);
                            url += usesOllamaChat ? "/api/chat" : "/v1/chat/completions";
                        }
                        else if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                        {
                            url += "/chat/completions";
                        }
                        else if (url.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
                        {
                            usesOllamaChat = true;
                            url += "/chat";
                        }
                        else if (url.Contains("/api/chat", StringComparison.OrdinalIgnoreCase))
                        {
                            usesOllamaChat = true;
                        }

                        var history = await _localDb.GetReadableMessagesAsync(localChat.Id, CancellationToken.None);
                        if (history.SkippedUnreadableCount > 0)
                        {
                            StatusText = "Bu sohbette bazı eski şifreli mesajlar mevcut anahtarla okunamadı; silinmediler ve yeni istek okunabilen geçmişle gönderildi.";
                            AddTimelineEvent("local_history_partial", $"skipped={history.SkippedUnreadableCount}");
                        }
                        var localMessages = BuildLocalChatContext(history.Messages, text);

                        object requestBody = usesOllamaChat
                            ? new
                            {
                                model = string.IsNullOrWhiteSpace(LocalLlmModel) ? "llama3.1" : LocalLlmModel.Trim(),
                                stream = false,
                                messages = localMessages
                            }
                            : new
                            {
                                model = string.IsNullOrWhiteSpace(LocalLlmModel) ? "local-model" : LocalLlmModel.Trim(),
                                messages = localMessages
                            };

                        var response = await client.PostAsJsonAsync(url, requestBody, CancellationToken.None);
                        if (response.IsSuccessStatusCode)
                        {
                            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(CancellationToken.None), default, CancellationToken.None);
                            offlineReply = ExtractLocalLlmContent(doc.RootElement);
                            if (string.IsNullOrWhiteSpace(offlineReply) && !usesOllamaChat)
                            {
                                offlineReply = await SendOpenAiResponsesAsync(client, LocalLlmBaseUrl.TrimEnd('/'), FormatLocalChatContextForResponses(localMessages), CancellationToken.None);
                            }

                            if (string.IsNullOrWhiteSpace(offlineReply))
                            {
                                offlineFailureReason = $"Yapay Zeka API boş veya beklenmeyen yanıt döndürdü: {url}";
                            }
                        }
                        else
                        {
                            var errorBody = await ReadErrorBodyAsync(response, CancellationToken.None);
                            if (!usesOllamaChat)
                            {
                                offlineReply = await SendOpenAiResponsesAsync(client, LocalLlmBaseUrl.TrimEnd('/'), FormatLocalChatContextForResponses(localMessages), CancellationToken.None);
                            }

                            if (string.IsNullOrWhiteSpace(offlineReply))
                            {
                                offlineFailureReason = $"Yapay Zeka API HTTP {(int)response.StatusCode} döndürdü: {url}";
                                if (!string.IsNullOrWhiteSpace(errorBody)) offlineFailureReason += $" | {TrimForStatus(errorBody)}";
                            }
                            AddTimelineEvent("offline_llm_failed", $"HTTP {(int)response.StatusCode}: {url}");
                        }
                    }
                    catch (Exception ex)
                    {
                        offlineFailureReason = $"Yapay Zeka API isteği başarısız oldu ({LocalLlmBaseUrl.Trim()}): {ex.Message}";
                        DesktopLogService.Error("Yerel yapay zeka sağlayıcı isteği başarısız oldu.", ex);
                    }
                }

                if (string.IsNullOrWhiteSpace(offlineReply))
                {
                    offlineReply = string.IsNullOrWhiteSpace(offlineFailureReason)
                        ? "Yapay Zeka API’den yanıt gelmedi. API URL, model adı ve anahtar ayarlarını kontrol edin."
                        : offlineFailureReason;
                    StatusText = offlineReply;
                }

                localAssistant.Content = offlineReply;
                AttachExplicitLocalAgentOffer(localAssistant);

                await _localDb.AppendMessageAsync(localChat.Id, "assistant", offlineReply, "Local-Offline", CancellationToken.None);
                AddTimelineEvent("report_sent", string.IsNullOrWhiteSpace(offlineFailureReason) ? "Yapay Zeka API yanıtı yazıldı." : offlineFailureReason);
                SpeakIfEnabled(offlineReply);
                _ = LoadChatsAsync(SearchText);
            }
            catch (Exception ex)
            {
                localAssistant.Content = "Yerel veritabanina kaydedilirken hata olustu: " + ex.Message;
                AddTimelineEvent("request_failed", ex.Message);
                OrbState = "error";
            }
            finally
            {
                IsAgentBusy = false;
                CanStopCurrentOperation = false;
            }
            return;
        }

        if (!IsAuthenticated)
        {
            StatusText = "Önce web üzerinden giriş yapın.";
            OrbState = "offline";
            return;
        }

        // Aktif sohbet yoksa yeni sohbet olustur; ileride sunucu bu sohbete yazar.
        var currentChat = ActiveChat;
        if (currentChat is null)
        {
            var createResult = await _backendClient.CreateChatAsync(null, CancellationToken.None);
            if (!createResult.Ok || createResult.Chat is null)
            {
                StatusText = BackendClient.GetCreateChatFailureMessageForUi(createResult.Reason);
                DesktopLogService.Error($"Mesaj gönderilirken yeni sohbet oluşturulamadı. reason={createResult.Reason}; status={(int?)createResult.StatusCode}");
                return;
            }

            var session = createResult.Chat;
            currentChat = new ChatSessionViewModel(session.Id, session.Title, session.UpdatedAt);
            await RunOnUiThreadAsync(() =>
            {
                Chats.Insert(0, currentChat!);
                foreach (var c in Chats) c.IsSelected = c.Id == currentChat!.Id;
                ActiveChat = currentChat;
            });
            await PersistActiveChatStateNonfatalAsync(currentChat.Id, "Mesaj için yeni sohbet");
        }

        _operationCancellation?.Cancel();
        _operationCancellation = new CancellationTokenSource();
        IsAgentBusy = true;
        CanStopCurrentOperation = true;
        OrbState = "processing";
        InputText = string.Empty;
        Messages.Add(new MessageViewModel("Kullanıcı", text));
        var assistant = new MessageViewModel("Asistan", string.Empty);
        Messages.Add(assistant);
        AddTimelineEvent("request_received", text);

        try
        {
            var ct = _operationCancellation.Token;
            if (IsOfflineMode) { assistant.Content = "Çevrimdışı mod etkin; sohbet sunucuya gönderilmedi."; AddTimelineEvent("request_blocked", "Sunucu senkronu kapalı."); OrbState = "idle"; return; }

            var submission = await _backendClient.SendAgentChatAsync(text, currentChat?.Id, ct);
            if (submission is null)
            {
                assistant.Content = "Agent isteği gönderilemedi.";
                AddTimelineEvent("request_failed", "Sunucudan agent yanıtı alınamadı.");
                OrbState = "error";
                return;
            }

            if (submission.ImmediateResponse is { } immediate)
            {
                assistant.Content = immediate.Response;
                AddTimelineEvent("report_sent", "Agent isteği anında yanıtlandı.");
                SpeakIfEnabled(assistant.Content);
                _ = LoadChatsAsync(SearchText);
                return;
            }

            if (submission.QueuedJob is not { } queued)
            {
                assistant.Content = "Agent isteği geçersiz bir yanıt döndürdü.";
                AddTimelineEvent("request_failed", "Agent gönderim sonucu boştu.");
                OrbState = "error";
                return;
            }

            AddTimelineEvent("job_created", $"jobId={queued.JobId}");
            ActiveJob = new ActiveJobViewModel(queued.JobId) { Status = queued.Status.ToString() };
            await PollJobAsync(queued.JobId, assistant, ct);
        }
        catch (OperationCanceledException)
        {
            assistant.Content = string.IsNullOrWhiteSpace(assistant.Content) ? "Yanit durduruldu." : assistant.Content;
            AddTimelineEvent("request_cancelled", "Yerel istek takibi durduruldu.");
            OrbState = "idle";
        }
        catch (Exception ex)
        {
            assistant.Content = "Agent isteği başarısız: " + ex.Message;
            AddTimelineEvent("request_failed", ex.Message);
            OrbState = "error";
        }
        finally
        {
            IsAgentBusy = false;
            CanStopCurrentOperation = false;
            ActiveJob = null;
            if (OrbState is "processing" or "transcribing") OrbState = IsAuthenticated ? "idle" : "offline";
        }
    }

    private async Task QueueServerLocalAgentSystemActionAsync(string text, LocalAgentIntent intent)
    {
        if (IsOfflineMode || !IsAuthenticated)
        {
            AddTimelineEvent("request_blocked", "Sunucuya bağlı Vortex Yapay Zeka Asistanı kuyruğu için giriş yapılmalıdır.");
            StatusText = "Sunucuya bağlı Vortex Yapay Zeka Asistanı eylemi için giriş yapılmalıdır.";
            return;
        }

        var selectedDeviceId = SelectedLocalAgentDevice?.Id;
        await RefreshLocalAgentDevicesAsync();
        var device = SelectedLocalAgentDevice;
        if ((selectedDeviceId is Guid expectedId && device?.Id != expectedId) ||
            device is null ||
            !RegisteredLocalAgentDevices.Any(registered => registered.Id == device.Id))
        {
            AddTimelineEvent("no_device_available", LocalAgentDeviceStatusText);
            StatusText = LocalAgentDeviceStatusText;
            return;
        }

        AddTimelineEvent("local_agent_server_queue", $"device={device.DeviceName} tool={intent.ToolName}");
        var queued = await TryQueueLocalAgentToolAsync(
            device.Id,
            intent.ToolName,
            intent.Arguments,
            dryRun: false,
            fallbackCommand: intent.FallbackCommand);
        if (queued is not null)
        {
            StatusText = $"Vortex Yapay Zeka Asistanı eylemi {device.DeviceName} cihazı için kuyruğa alındı.";
        }
    }

    /// <summary>
    /// Direct LocalAgent path for offline system-control intents.
    /// It does not use the public Server queue or selected server device.
    /// </summary>
    private async Task SendLocalAgentSystemActionAsync(string text, LocalAgentIntent intent)
    {
        IsAgentBusy = true;
        CanStopCurrentOperation = true;
        OrbState = "processing";
        InputText = string.Empty;
        Messages.Add(new MessageViewModel("Kullanıcı", text));
        var assistant = new MessageViewModel("Asistan", string.Empty);
        Messages.Add(assistant);
        AddTimelineEvent("request_received", text);
        AddTimelineEvent(
            "local_agent_intent",
            intent.UsesPreparedTool
                ? $"tool={intent.ToolName} summary={intent.Summary}"
                : $"run_cmd summary={intent.Summary}");

        ChatSessionViewModel? offlineChat = null;
        if (IsOfflineMode)
        {
            offlineChat = ActiveChat;
            if (offlineChat is null)
            {
                try
                {
                    var id = Guid.NewGuid();
                    var title = text.Length > 20 ? text[..20] + "..." : text;
                    await _localDb.CreateSessionAsync(id, title, CancellationToken.None);
                    offlineChat = new ChatSessionViewModel(id, title, DateTimeOffset.Now);
                    await RunOnUiThreadAsync(() =>
                    {
                        Chats.Insert(0, offlineChat!);
                        foreach (var c in Chats) c.IsSelected = c.Id == offlineChat!.Id;
                        ActiveChat = offlineChat;
                    });
                }
                catch (Exception ex)
                {
                    DesktopLogService.Error("Yerel sohbet olusturulamadi (LocalAgent).", ex);
                    assistant.Content = "Yerel sohbet oluşturulamadı: " + ex.Message;
                    AddTimelineEvent("request_failed", ex.Message);
                    OrbState = "error";
                    IsAgentBusy = false;
                    CanStopCurrentOperation = false;
                    return;
                }
            }

            try
            {
                await _localDb.AppendMessageAsync(offlineChat.Id, "user", text, null, CancellationToken.None);
            }
            catch (Exception ex)
            {
                DesktopLogService.Error("Yerel kullanici mesaji yazilamadi (LocalAgent).", ex);
            }
        }

        try
        {
            var localRuntime = await _localAgentRuntime.EnsureReadyAsync(CancellationToken.None);
            if (!localRuntime.Ready)
            {
                assistant.Content = "Çevrimdışı Vortex Yapay Zeka Asistanı kullanılamıyor; sohbet özellikleri çalışmaya devam eder.";
                AddTimelineEvent("local_agent_failed", localRuntime.Reason);
                OrbState = "error";
                if (offlineChat is not null)
                {
                    try
                    {
                        await _localDb.AppendMessageAsync(offlineChat.Id, "assistant", assistant.Content, "Vortex Yapay Zeka Asistanı", CancellationToken.None);
                    }
                    catch { /* best-effort */ }
                }
                return;
            }

            var userConfirmed = false;
            if (intent.RequiresConfirmation)
            {
                var dryRunResult = await InvokeLocalAgentToolAsync(
                    intent.ToolName,
                    intent.Arguments,
                    userConfirmed: false,
                    dryRun: true,
                    cancellationToken: CancellationToken.None);

                var dryRunDetail = dryRunResult.Ok
                    ? $"Ön kontrol başarılı.\nPlan: {dryRunResult.Message}\n{dryRunResult.Output}".Trim()
                    : $"Ön kontrol başarısız: {dryRunResult.Message ?? dryRunResult.Reason}";

                var detail = intent.UsesPreparedTool
                    ? $"Riskli yerel araç: {intent.ToolName}\nİstek: {intent.Summary}\n\n{dryRunDetail}"
                    : $"Riskli serbest komut (run_cmd): {intent.FallbackCommand ?? intent.Arguments.GetValueOrDefault("command") ?? intent.ToolName}\n\n{dryRunDetail}";
                var approved = await RequestLocalAgentApproval("Vortex Yapay Zeka Asistanı onayı", detail);
                if (!approved)
                {
                    assistant.Content = "Vortex Yapay Zeka Asistanı eylemi kullanıcı tarafından reddedildi.";
                    AddTimelineEvent("approval_rejected", intent.ToolName);
                    OrbState = "idle";
                    if (offlineChat is not null)
                    {
                        try
                        {
                            await _localDb.AppendMessageAsync(offlineChat.Id, "assistant", assistant.Content, "LocalAgent", CancellationToken.None);
                        }
                        catch { /* best-effort */ }
                    }
                    return;
                }

                userConfirmed = true;
            }

            var result = await InvokeLocalAgentToolAsync(
                intent.ToolName,
                intent.Arguments,
                userConfirmed,
                dryRun: false,
                cancellationToken: CancellationToken.None);

            if (result.Ok)
            {
                var body = string.IsNullOrWhiteSpace(result.Message)
                    ? (string.IsNullOrWhiteSpace(result.Output) ? "Vortex Yapay Zeka Asistanı tamamlandı." : result.Output!)
                    : result.Message!;
                if (!string.IsNullOrWhiteSpace(result.Output) &&
                    !string.Equals(result.Message, result.Output, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(result.Message))
                {
                    body = $"{result.Message}\n{result.Output}";
                }

                assistant.Content = body;
                AddTimelineEvent("local_agent_ok", $"tool={intent.ToolName} reason={result.Reason}");
                SpeakIfEnabled(assistant.Content);
            }
            else
            {
                var failMsg = result.Reason switch
                {
                    "not_configured" => "Çevrimdışı cihaz eylemleri hazır değil. Ayarlar > Çevrimdışı Cihaz Eylemleri bölümünden Onar deyin.",
                    "not_authenticated" => "Çevrimdışı cihaz eylemi yetkilendirme başarısız (secret).",
                    "transport_error" => $"Çevrimdışı cihaz eylemi ulaşılamıyor: {result.Message ?? result.Reason}",
                    "invalid_request" => result.Message ?? "İstek reddedildi.",
                    _ => result.Message ?? $"İşlem başarısız: {result.Reason}"
                };
                assistant.Content = failMsg;
                AddTimelineEvent("local_agent_failed", $"tool={intent.ToolName} reason={result.Reason}");
                OrbState = "error";
            }

            if (offlineChat is not null)
            {
                try
                {
                    await _localDb.AppendMessageAsync(offlineChat.Id, "assistant", assistant.Content, "LocalAgent", CancellationToken.None);
                    _ = LoadChatsAsync(SearchText);
                }
                catch (Exception ex)
                {
                    DesktopLogService.Error("Yerel asistan yaniti yazilamadi (LocalAgent).", ex);
                }
            }
        }
        catch (Exception ex)
        {
            assistant.Content = "Vortex Yapay Zeka Asistanı hatası: " + ex.Message;
            AddTimelineEvent("local_agent_failed", ex.Message);
            OrbState = "error";
            DesktopLogService.Error("LocalAgent system action failed.", ex);
        }
        finally
        {
            IsAgentBusy = false;
            CanStopCurrentOperation = false;
            if (OrbState is "processing" or "transcribing") OrbState = IsAuthenticated ? "idle" : "offline";
        }
    }

    private async Task HandleHermesDeviceActionProposalAsync(HermesActionProposal proposal, MessageViewModel assistant)
    {
        assistant.Content = $"Hermes, {proposal.ToolName} eylemini önerdi. Seçtiğiniz cihazda çalıştırmak için onay gerekli.";
        AddTimelineEvent("hermes_action_proposed", proposal.ToolName);
        if (IsOfflineMode || !IsAuthenticated)
        {
            assistant.Content += " Çevrim içi ve giriş yapılmış bir oturum gereklidir.";
            return;
        }

        await RefreshLocalAgentDevicesAsync();
        var device = SelectedLocalAgentDevice;
        if (device is null || !_hasFreshLocalAgentDeviceList || !RegisteredLocalAgentDevices.Any(item => item.Id == device.Id))
        {
            assistant.Content += " Onaylanmış bir Vortex Yapay Zeka Asistanı cihazı seçilmedi.";
            AddTimelineEvent("no_device_available", LocalAgentDeviceStatusText);
            return;
        }

        // TryQueueLocalAgentToolAsync owns the single visible approval card and sends UserConfirmed only after approval.
        var queued = await TryQueueLocalAgentToolAsync(device.Id, proposal.ToolName, new Dictionary<string, string>(proposal.Arguments, StringComparer.Ordinal), false);
        assistant.Content = queued is null
            ? "Hermes eylem önerisi reddedildi veya cihaz kuyruğuna alınamadı."
            : $"Hermes eylemi {device.DeviceName} cihazı için kuyruğa alındı; gerçek sonuç bekleniyor.";
    }

    private static bool TryReadHermesDeviceActionProposal(string? result, out HermesActionProposal proposal)
    {
        proposal = default!;
        if (string.IsNullOrWhiteSpace(result)) return false;
        try
        {
            var parsed = JsonSerializer.Deserialize<HermesActionProposal>(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (parsed is null || parsed.Kind != "device_action_proposal" || parsed.Status != "approval_required") return false;
            if (parsed.ToolName is not ("jarvis_open_app" or "jarvis_open_file" or "jarvis_create_folder" or "jarvis_add_note" or "jarvis_lock_screen" or "jarvis_write_document" or "pardus_set_theme" or "pardus_set_wallpaper")) return false;
            if (parsed.Arguments.Count > 8 || parsed.Arguments.Any(pair => pair.Key.Length is 0 or > 64 || pair.Value.Length > 4096)) return false;
            if (parsed.Arguments.TryGetValue("mode", out var mode) && mode is not ("dark" or "light")) return false;
            if (parsed.Arguments.TryGetValue("color", out var color) && color is not ("black" or "#000000")) return false;
            proposal = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private async Task PollJobAsync(Guid jobId, MessageViewModel assistant, CancellationToken cancellationToken)
    {
        AgentJobStatus? lastStatus = null;
        using var streamTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        streamTimeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await foreach (var streamResult in _backendClient.StreamAgentJobEventsAsync(jobId, streamTimeout.Token))
            {
                if (!streamResult.Ok || streamResult.Job is null) break;

                var streamedJob = streamResult.Job;
                if (streamedJob.Status != lastStatus)
                {
                    lastStatus = streamedJob.Status;
                    AddTimelineEvent(streamedJob.Status.ToString(), $"jobId={jobId}");
                    if (ActiveJob is { } activeJobViewModel) activeJobViewModel.Status = streamedJob.Status.ToString();
                }

                if (streamedJob.Status is AgentJobStatus.Completed or AgentJobStatus.Failed or AgentJobStatus.Cancelled or AgentJobStatus.TimedOut)
                {
                    if (streamedJob.Status == AgentJobStatus.Completed && TryReadHermesDeviceActionProposal(streamedJob.Result, out var proposal))
                    {
                        await HandleHermesDeviceActionProposalAsync(proposal, assistant);
                    }
                    else if (!string.IsNullOrWhiteSpace(streamedJob.Result)) assistant.Content = streamedJob.Result;
                    AddTimelineEvent("report_sent", $"status={streamedJob.Status}");
                    ActiveJob = null;
                    if (streamedJob.Status == AgentJobStatus.Completed) SpeakIfEnabled(assistant.Content);
                    else OrbState = "error";
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AddTimelineEvent("stream_timeout", $"jobId={jobId}");
        }

        // SSE is preferred; retain bounded polling only after stream completion/failure.
        for (var attempt = 0; attempt < 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fetch = await _backendClient.GetAgentJobAsync(jobId, cancellationToken);
            if (!fetch.Ok || fetch.Job is null)
            {
                var reason = string.IsNullOrWhiteSpace(fetch.Reason) ? "job_unavailable" : fetch.Reason;
                AddTimelineEvent(reason, $"jobId={jobId}");
                if (string.IsNullOrWhiteSpace(assistant.Content))
                {
                    assistant.Content = reason switch
                    {
                        "not_authenticated" => "Oturum doğrulanamadı. Lütfen yeniden giriş yapın.",
                        "not_found" => "Agent görevi bulunamadı veya erişim yok.",
                        "transport_error" => "Sunucuya ulaşılamadı.",
                        "http_error" => "Agent görev durumu alınamadı.",
                        _ => "Agent görevi şu an kullanılamıyor."
                    };
                }
                ActiveJob = null;
                OrbState = "error";
                return;
            }

            var job = fetch.Job;
            if (job.Status != lastStatus)
            {
                lastStatus = job.Status;
                AddTimelineEvent(job.Status.ToString(), $"jobId={jobId}");
                if (ActiveJob is { } activeJobViewModel) activeJobViewModel.Status = job.Status.ToString();
            }

            if (job.Status is AgentJobStatus.Completed or AgentJobStatus.Failed or AgentJobStatus.Cancelled or AgentJobStatus.TimedOut)
            {
                if (job.Status == AgentJobStatus.Completed && TryReadHermesDeviceActionProposal(job.Result, out var proposal))
                {
                    await HandleHermesDeviceActionProposalAsync(proposal, assistant);
                }
                else if (!string.IsNullOrWhiteSpace(job.Result)) assistant.Content = job.Result;
                AddTimelineEvent("report_sent", $"status={job.Status}");
                ActiveJob = null;
                if (job.Status == AgentJobStatus.Completed) SpeakIfEnabled(assistant.Content);
                else OrbState = "error";
                return;
            }

            await Task.Delay(1000, cancellationToken);
        }

        AddTimelineEvent("polling_timeout", $"jobId={jobId}");
        ActiveJob = null;
        OrbState = "error";
        if (string.IsNullOrWhiteSpace(assistant.Content))
        {
            assistant.Content = "Agent işi henüz tamamlanmadı. Lütfen daha sonra tekrar kontrol edin.";
        }
    }

    public static string NormalizeSpokenResponse(string? text, int maxChars = 420, int maxSentences = 2)
    {
        if (string.IsNullOrWhiteSpace(text) || maxChars <= 0 || maxSentences <= 0) return string.Empty;

        var withoutCode = Regex.Replace(text, @"```.*?```", " Kod bloğu ekranda. ", RegexOptions.Singleline);
        var normalized = Regex.Replace(withoutCode.Replace("**", string.Empty).Replace("__", string.Empty).Replace("`", string.Empty), @"\s+", " ").Trim();
        if (normalized.Length == 0) return string.Empty;

        var sentences = Regex.Matches(normalized, @"[^.!?…]+[.!?…]?")
            .Select(match => match.Value.Trim())
            .Where(sentence => sentence.Length > 0)
            .Take(maxSentences)
            .ToList();
        var spoken = string.Join(" ", sentences);
        var shortened = sentences.Count < Regex.Matches(normalized, @"[^.!?…]+[.!?…]?").Count;
        if (spoken.Length > maxChars)
        {
            var cut = spoken[..maxChars].LastIndexOf(' ');
            spoken = (cut > 0 ? spoken[..cut] : spoken[..maxChars]).TrimEnd();
            shortened = true;
        }

        return shortened ? spoken + " Devamı ekranda." : spoken;
    }

    private void SpeakIfEnabled(string text)
    {
        if (!IsVoiceReplyEnabled)
        {
            OrbState = IsAuthenticated ? "idle" : "offline";
            return;
        }

        var spoken = NormalizeSpokenResponse(text);
        if (string.IsNullOrWhiteSpace(spoken))
        {
            OrbState = IsAuthenticated ? "idle" : "offline";
            return;
        }
        _voiceOutput.Speak(spoken);
    }

    private static string GetTtsStatusMessage(string reason) => reason switch
    {
        "provider_missing" => "TTS sağlayıcısı yapılandırılmamış; yerel fallback deneniyor.",
        "provider_failed" => "TTS sağlayıcısı isteği tamamlayamadı; yerel fallback deneniyor.",
        "quota_exceeded_daily" => "Günlük TTS limitiniz doldu; yerel fallback deneniyor.",
        "not_authenticated" => "TTS için yeniden giriş yapmanız gerekiyor; yerel fallback deneniyor.",
        "text_too_large" => "Seslendirilecek metin çok uzun; kısaltılmış metin deneniyor.",
        "text_required" or "unsupported_provider" => "TTS isteği geçersiz; yerel fallback deneniyor.",
        "playback_failed" => "Sesli yanıt oynatılamadı.",
        _ => "TTS isteği başarısız oldu; yerel fallback deneniyor."
    };

    private void AddTimelineEvent(string kind, string detail)
    {
        _ = RunOnUiThreadAsync(() =>
        {
            Timeline.Insert(0, new ActivityEventViewModel(kind, detail, DateTimeOffset.Now));
            while (Timeline.Count > 50) Timeline.RemoveAt(Timeline.Count - 1);
        });
    }

    private async Task StartClapListeningAsync()
    {
        if (!IsAuthenticated || _clapDetection is null) return;

        await _clapDetection.StartAsync(_voiceInput, CancellationToken.None);

        if (!IsRecording && !IsTranscribing && !IsAlwaysListening)
        {
            try
            {
                _recordingOwnedByAlwaysListening = false;
                IsRecording = true;
                InputAudioLevel = 0.02;
                OrbState = "recording";
                await _voiceInput.StartRecordingAsync();
                AddTimelineEvent("clap_listener_started", "Alkis tetikleyici yerel mikrofon seviyesini dinliyor.");
            }
            catch (Exception ex)
            {
                IsRecording = false;
                OrbState = "error";
                DesktopLogService.Error("Alkış tetikleyici için yerel dinleme başlatılamadı.", ex);
            }
        }
    }

    private void StopClapListening()
    {
        _clapDetection?.Stop();
    }

    private void OnClapDetected(object? sender, EventArgs e)
    {
        _ = ActivateAlwaysListeningFromClapAsync();
    }

    private async Task ActivateAlwaysListeningFromClapAsync()
    {
        if (!IsAuthenticated || !IsVoiceModeEnabled || IsTranscribing) return;

        await RunOnUiThreadAsync(async () =>
        {
            VoiceModeStatusText = "Alkış algılandı. Yerel transkript kapısı açık; 'Hey Vortex' ifadesi aranır.";

            if (IsRecording && !IsAlwaysListening)
            {
                try
                {
                    await _voiceInput.StopRecordingAsync();
                }
                catch (Exception ex)
                {
                    DesktopLogService.Error("Alkis sonrasi izleme tamponu temizlenemedi.", ex);
                }
                IsRecording = false;
            }

            if (!IsAlwaysListening)
            {
                IsAlwaysListening = true;
            }
            else if (!IsRecording)
            {
                await StartAlwaysListeningAsync();
            }
        });
    }

    [RelayCommand]
    private void ExitVoiceMode()
    {
        StopCurrentOperationCore(addTimelineEvent: false);
        IsVoiceModeEnabled = false;
        IsRecording = false;
        IsTranscribing = false;
        _recordingOwnedByAlwaysListening = false;
        IsTasksTabSelected = false;
        OrbState = IsAuthenticated ? "idle" : "offline";
        VoiceModeStatusText = string.Empty;
    }

    [RelayCommand]
    private async Task PushToTalkAsync()
    {
        if (IsTranscribing)
        {
            Messages.Add(new MessageViewModel("Ses", "Ses metne çevrilirken yeni kayıt başlatılamaz."));
            return;
        }

        if (IsRecording)
        {
            await StopRecordingAndTranscribeAndSendAsync(requireWakeWord: false);
        }
        else
        {
            IsVoiceModeEnabled = true;
        }
    }

    public async Task StartRecordingFromBridgeAsync()
    {
        if (!IsRecording) await StartRecordingAsync();
    }

    public async Task StopRecordingFromBridgeAsync()
    {
        if (IsRecording) await StopRecordingAndTranscribeAndSendAsync(requireWakeWord: false);
    }

    private async Task StartRecordingAsync(bool ownedByAlwaysListening = false)
    {
        if (!IsAuthenticated)
        {
            await RunOnUiThreadAsync(() =>
            {
                StatusText = "Önce web üzerinden giriş yapın.";
                OrbState = "offline";
            });
            return;
        }

        try
        {
            await RunOnUiThreadAsync(() =>
            {
                _recordingOwnedByAlwaysListening = ownedByAlwaysListening;
                IsRecording = true;
                InputAudioLevel = 0.02;
                OrbState = "recording";
                if (IsOfflineMode)
                {
                    StatusText = "Çevrimdışı modda yerel ses kaydı açık; Whisper modeliyle tanınacak.";
                }
            });
            await _voiceInput.StartRecordingAsync();
            AddTimelineEvent("voice_recording_started", "Kayit basladi");
        }
        catch (Exception ex)
        {
            await RunOnUiThreadAsync(() =>
            {
                _recordingOwnedByAlwaysListening = false;
                IsRecording = false;
                OrbState = "error";
                Messages.Add(new MessageViewModel("Ses", "Ses kaydi baslatilamadi: " + ex.Message));
            });
        }
    }

    private async Task StopRecordingAndTranscribeAsync()
    {
        bool alreadyTranscribing = false;
        await RunOnUiThreadAsync(() =>
        {
            if (IsTranscribing)
            {
                Messages.Add(new MessageViewModel("Ses", "Ses metne çevrilirken yeni kayıt başlatılamaz."));
                alreadyTranscribing = true;
            }
            else
            {
                IsRecording = false;
                IsTranscribing = true;
                OrbState = "transcribing";
            }
        });

        if (alreadyTranscribing) return;

        AddTimelineEvent("voice_recording_stopped", "Kayit durdu, taniniyor...");
        try
        {
            var audio = await _voiceInput.StopRecordingAsync();
            if (audio is null)
            {
                await RunOnUiThreadAsync(() =>
                {
                    OrbState = "error";
                    Messages.Add(new MessageViewModel("Ses", "Ses kaydi alinamadi."));
                });
                return;
            }
            var localText = await _voiceInput.TranscribeLocalAsync(audio, CancellationToken.None);
            if (string.IsNullOrWhiteSpace(localText))
            {
                await RunOnUiThreadAsync(() =>
                {
                    OrbState = "error";
                    AddTimelineEvent("voice_transcribe_failed", "Yerel whisper.cpp transkripsiyonu kullanılamıyor veya boş sonuç döndü.");
                    Messages.Add(new MessageViewModel("Ses", "Yerel whisper.cpp ses tanıma kullanılamıyor veya boş sonuç döndü. Lütfen whisper.cpp çalıştırılabilir dosyasını ve modeli yapılandırın."));
                });
                return;
            }

            await RunOnUiThreadAsync(() =>
            {
                InputText = localText.Trim();
                OrbState = "idle";
                AddTimelineEvent("voice_transcribed_local", InputText);
            });
        }
        catch (Exception ex)
        {
            await RunOnUiThreadAsync(() =>
            {
                OrbState = "error";
                Messages.Add(new MessageViewModel("Ses", "Ses tanıma hatası: " + ex.Message));
            });
            DesktopLogService.Error("StopRecordingAndTranscribeAsync exception ayrintisini yakaladi.", ex);
        }
        finally
        {
            await RunOnUiThreadAsync(() =>
            {
                _recordingOwnedByAlwaysListening = false;
                IsTranscribing = false;
            });
        }
    }

    private static string GetVoiceTranscribeMessage(VoiceTranscribeResult result) => result.Reason switch
    {
        "provider_missing" => "Ses tanima saglayicisi yapilandirilmamis.",
        "provider_failed" => "Ses tanima saglayicisi istegi tamamlayamadi.",
        "not_authenticated" => "Ses tanıma için yeniden giriş yapmanız gerekiyor.",
        "transport_error" => "Ses tanima sunucusuna ulasilamadi.",
        "payload_too_large" => "Ses dosyası çok büyük.",
        "quota_exceeded_daily" => "Günlük istek limitiniz doldu.",
        "http_error" => result.StatusCode is null
            ? "Ses tanima istegi basarisiz oldu."
            : $"Ses tanima istegi basarisiz oldu (HTTP {(int)result.StatusCode}).",
        _ => "Ses kapalı"
    };

    private void ResetVoiceInteraction()
    {
        StopClapListening();
        _voiceOutput.Stop();
        IsAlwaysListening = false;
        IsVoiceModeEnabled = false;
        IsRecording = false;
        IsTranscribing = false;
        _recordingOwnedByAlwaysListening = false;
        InputAudioLevel = 0;
        IsTasksTabSelected = false;
        IsKokoroInstallPromptVisible = false;
        CancelPendingLocalAgentApproval();
        IsKokoroInstallFailed = false;
        KokoroInstallLog = string.Empty;
        LocalAgentApprovalTitle = string.Empty;
        LocalAgentApprovalText = string.Empty;
        VoiceModeStatusText = string.Empty;
        OrbState = IsAuthenticated && !IsOfflineMode ? "idle" : "offline";
    }

    [RelayCommand]
    public void StopCurrentOperation()
    {
        StopCurrentOperationCore(addTimelineEvent: true);
    }

    private void StopCurrentOperationCore(bool addTimelineEvent)
    {
        _operationCancellation?.Cancel();
        CancelPendingLocalAgentApproval();
        _voiceOutput.Stop();
        ActiveJob = null;
        IsAgentBusy = false;
        CanStopCurrentOperation = false;
        OrbState = IsAuthenticated ? "idle" : "offline";
        if (addTimelineEvent) AddTimelineEvent("response_stopped", "Yerel yanit/ses durduruldu.");
    }

    private void CancelPendingLocalAgentApproval()
    {
        IsLocalAgentApprovalCardVisible = false;
        _pendingApprovalTcs?.TrySetResult(false);
        _pendingApprovalTcs = null;
    }

    [RelayCommand]
    private void ApproveLocalAgentAction()
    {
        IsLocalAgentApprovalCardVisible = false;
        AddTimelineEvent("approval_confirmed", "Yerel araç eylemi Kullanıcı tarafından onaylandı.");
        _pendingApprovalTcs?.TrySetResult(true);
        _pendingApprovalTcs = null;
    }

    [RelayCommand]
    private void RejectLocalAgentAction()
    {
        IsLocalAgentApprovalCardVisible = false;
        AddTimelineEvent("approval_rejected", "Yerel araç eylemi Kullanıcı tarafından reddedildi.");
        _pendingApprovalTcs?.TrySetResult(false);
        _pendingApprovalTcs = null;
    }

    public Task<bool> RequestLocalAgentApproval(string title, string detail)
    {
        if (_pendingApprovalTcs is not null)
        {
            _pendingApprovalTcs.TrySetResult(false);
            _pendingApprovalTcs = null;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingApprovalTcs = tcs;
        LocalAgentApprovalTitle = string.IsNullOrWhiteSpace(title) ? "Onay gerekli" : title;
        LocalAgentApprovalText = detail ?? string.Empty;
        IsLocalAgentApprovalCardVisible = true;
        AddTimelineEvent("approval_requested", LocalAgentApprovalTitle);
        return tcs.Task;
    }

    public async Task<LocalAgentQueuedResponse?> TryQueueLocalAgentToolAsync(
        Guid deviceId,
        string toolName,
        Dictionary<string, string>? arguments = null,
        bool dryRun = false,
        string? fallbackCommand = null,
        CancellationToken cancellationToken = default)
    {
        if (IsOfflineMode)
        {
            AddTimelineEvent("request_blocked", "Çevrimdışı modda sunucu kuyruğu kapalı.");
            return null;
        }
        if (!_hasFreshLocalAgentDeviceList || deviceId == Guid.Empty || !RegisteredLocalAgentDevices.Any(device => device.Id == deviceId))
        {
            AddTimelineEvent("no_device_available", _hasFreshLocalAgentDeviceList
                ? "Seçilen sunucuya bağlı Vortex Yapay Zeka Asistanı cihazı artık geçerli değil."
                : "Sunucuya bağlı Vortex Yapay Zeka Asistanı cihaz listesi doğrulanamadı.");
            return null;
        }

        var planResult = await _backendClient.PlanLocalAgentActionAsync(toolName, arguments, fallbackCommand, cancellationToken);
        if (!planResult.Ok || planResult.Plan is null)
        {
            var reason = string.IsNullOrWhiteSpace(planResult.Reason) ? "http_error" : planResult.Reason;
            AddTimelineEvent(
                reason == "not_authenticated" ? "not_authenticated" : "request_failed",
                reason switch
                {
                    "not_authenticated" => "Vortex Yapay Zeka Asistanı plan: oturum yok veya doğrulanamadı.",
                    "transport_error" => "Vortex Yapay Zeka Asistanı plan: sunucuya ulaşılamadı.",
                    _ => "Vortex Yapay Zeka Asistanı plan yanıtı alınamadı."
                });
            return null;
        }

        var plan = planResult.Plan;
        // Server-queued actions always require an explicit user confirmation, including low-risk prepared tools.
        var needsApproval = true;
        if (needsApproval)
        {
            var detail = plan.UsesPreparedTool
                ? $"Araç: {plan.ToolName}"
                : $"Serbest komut (run_cmd): {plan.FallbackCommand ?? fallbackCommand ?? toolName}";
            var approved = await RequestLocalAgentApproval("Vortex Yapay Zeka Asistanı onayı", detail);
            if (!approved)
            {
                AddTimelineEvent("approval_rejected", $"Vortex Yapay Zeka Asistanı eylemi reddedildi: {plan.ToolName}");
                return null;
            }
            if (SelectedLocalAgentDevice?.Id != deviceId || !RegisteredLocalAgentDevices.Any(device => device.Id == deviceId))
            {
                AddTimelineEvent("no_device_available", "Onay sırasında seçilen Vortex Yapay Zeka Asistanı cihazı değişti veya artık geçerli değil.");
                return null;
            }
        }

        var queueArgs = arguments is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(arguments, StringComparer.Ordinal);
        if (!plan.UsesPreparedTool)
        {
            var command = plan.FallbackCommand
                ?? fallbackCommand
                ?? (queueArgs.TryGetValue("command", out var existing) ? existing : null);
            if (string.IsNullOrWhiteSpace(command))
            {
                AddTimelineEvent("request_failed", "Serbest run_cmd için komut yok.");
                return null;
            }

            queueArgs["command"] = command;
        }

        var queueResult = await _backendClient.QueueLocalAgentActionAsync(
            deviceId,
            plan.UsesPreparedTool ? plan.ToolName : "run_cmd",
            queueArgs,
            userConfirmed: needsApproval || plan.RequiresUserConfirmation,
            dryRun,
            cancellationToken);

        if (!queueResult.Ok || queueResult.Queued is null)
        {
            var qReason = string.IsNullOrWhiteSpace(queueResult.Reason) ? "http_error" : queueResult.Reason;
            AddTimelineEvent(
                qReason switch
                {
                    "user_approval_required" => "user_approval_required",
                    "not_authenticated" => "not_authenticated",
                    "invalid_request" => "invalid_request",
                    "transport_error" => "transport_error",
                    _ => "request_failed"
                },
                qReason switch
                {
                    "user_approval_required" => "Vortex Yapay Zeka Asistanı eylemi için sunucu onayı gerekli (user_approval_required).",
                    "not_authenticated" => "Vortex Yapay Zeka Asistanı kuyruk: oturum yok veya doğrulanamadı.",
                    "invalid_request" => "Vortex Yapay Zeka Asistanı kuyruk: geçersiz istek veya argümanlar.",
                    "transport_error" => "Vortex Yapay Zeka Asistanı kuyruk: sunucuya ulaşılamadı.",
                    _ => "Vortex Yapay Zeka Asistanı eylemi kuyruğa alınamadı."
                });
            return null;
        }

        AddTimelineEvent("job_created", $"LocalAgent jobId={queueResult.Queued.JobId} tool={queueResult.Queued.Action}");
        if (!Guid.TryParse(queueResult.Queued.JobId, out var jobId))
        {
            StatusText = "Vortex Yapay Zeka Asistanı kuyruğa alındı ancak job durumu doğrulanamadı.";
            AddTimelineEvent("request_failed", "Vortex Yapay Zeka Asistanı iş kimliği geçersiz.");
            return queueResult.Queued;
        }

        await PollLocalAgentJobAsync(jobId, queueResult.Queued, cancellationToken);
        return queueResult.Queued;
    }

    private async Task PollLocalAgentJobAsync(Guid jobId, LocalAgentQueuedResponse queued, CancellationToken cancellationToken)
    {
        string? lastStatus = null;
        StatusText = "Vortex Yapay Zeka Asistanı eylemi kuyruğa alındı; cihaz bekleniyor.";
        for (var attempt = 0; attempt < 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fetch = await _backendClient.GetLocalAgentJobStatusAsync(jobId, cancellationToken);
            if (!fetch.Ok || fetch.Job is null)
            {
                var reason = string.IsNullOrWhiteSpace(fetch.Reason) ? "http_error" : fetch.Reason;
                AddTimelineEvent(reason == "not_found" ? "job_unavailable" : reason, $"LocalAgent jobId={jobId}");
                StatusText = reason switch
                {
                    "not_authenticated" => "Vortex Yapay Zeka Asistanı iş durumu için yeniden giriş yapın.",
                    "not_found" => "Vortex Yapay Zeka Asistanı işi bulunamadı veya erişim yok.",
                    "transport_error" => "Vortex Yapay Zeka Asistanı iş durumu için sunucuya ulaşılamadı.",
                    "cancelled" => "Vortex Yapay Zeka Asistanı iş durumu izleme iptal edildi.",
                    "invalid_response" => "Sunucu geçersiz bir Vortex Yapay Zeka Asistanı iş durumu döndürdü.",
                    _ => "Vortex Yapay Zeka Asistanı iş durumu doğrulanamadı."
                };
                OrbState = "error";
                return;
            }

            var job = fetch.Job;
            if (!string.Equals(job.Status, lastStatus, StringComparison.OrdinalIgnoreCase))
            {
                lastStatus = job.Status;
                AddTimelineEvent(job.Status, $"LocalAgent jobId={job.JobId} tool={job.ToolName}");
            }

            if (string.Equals(job.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                var detail = string.IsNullOrWhiteSpace(job.Message) ? job.Code : $"{job.Code}: {job.Message}";
                if (job.DryRun && string.Equals(job.Code, "DRY_RUN", StringComparison.OrdinalIgnoreCase) && job.Success == true)
                {
                    StatusText = $"Dry-run tamamlandı; komut çalıştırılmadı. {detail}".Trim();
                    AddTimelineEvent("dry_run_completed", $"LocalAgent jobId={job.JobId} {detail}");
                    return;
                }

                if (job.Success == true)
                {
                    StatusText = $"Vortex Yapay Zeka Asistanı eylemi tamamlandı. {detail}".Trim();
                    AddTimelineEvent("local_agent_completed", $"LocalAgent jobId={job.JobId} {detail}");
                    return;
                }

                StatusText = $"Vortex Yapay Zeka Asistanı eylemi tamamlanamadı. {detail}".Trim();
                AddTimelineEvent("local_agent_failed", $"LocalAgent jobId={job.JobId} {detail}");
                OrbState = "error";
                return;
            }

            StatusText = string.Equals(job.Status, "claimed", StringComparison.OrdinalIgnoreCase)
                ? "Vortex Yapay Zeka Asistanı işi aldı; sonuç bekleniyor."
                : "Vortex Yapay Zeka Asistanı eylemi kuyrukta bekliyor.";
            await Task.Delay(1000, cancellationToken);
        }

        StatusText = "Vortex Yapay Zeka Asistanı işi henüz tamamlandığı doğrulanmadı; daha sonra tekrar kontrol edin.";
        AddTimelineEvent("polling_timeout", $"LocalAgent jobId={jobId}");
    }
    [RelayCommand]
    private async Task IncreaseMainScaleAsync()
    {
        MainUiScale = Math.Clamp(MainUiScale + 0.05, 0.85, 1.25);
        await SaveSettingsAsync();
    }

    [RelayCommand]
    private async Task DecreaseMainScaleAsync()
    {
        MainUiScale = Math.Clamp(MainUiScale - 0.05, 0.85, 1.25);
        await SaveSettingsAsync();
    }

    [RelayCommand]
    private async Task IncreaseCompactScaleAsync()
    {
        CompactUiScale = Math.Clamp(CompactUiScale + 0.05, 0.85, 1.35);
        await SaveSettingsAsync();
    }

    [RelayCommand]
    private async Task DecreaseCompactScaleAsync()
    {
        CompactUiScale = Math.Clamp(CompactUiScale - 0.05, 0.85, 1.35);
        await SaveSettingsAsync();
    }

    private DateTime _lastVoiceActivityTime = DateTime.MinValue;
    private bool _isUserSpeaking;
    private System.Timers.Timer? _vadTimer;
    private readonly ObservableCollection<MessageViewModel> _tempVoiceMessages = new();

    private async Task StartAlwaysListeningAsync()
    {
        if (IsOfflineMode)
        {
            await RunOnUiThreadAsync(() =>
            {
                StatusText = "Çevrimdışı mod etkin; sunucu işlemleri kapalı.";
                OrbState = "offline";
            });
            return;
        }
        if (!IsAuthenticated) return;
        try
        {
            await StartRecordingAsync(ownedByAlwaysListening: true);
            _isUserSpeaking = false;
            _lastVoiceActivityTime = DateTime.Now;

            if (_vadTimer is null)
            {
                _vadTimer = new System.Timers.Timer(300);
                _vadTimer.Elapsed += async (s, e) => await CheckVoiceActivityTimeoutAsync();
            }
            _vadTimer.Start();
        }
        catch (Exception ex)
        {
            DesktopLogService.Error("Sürekli dinleme başlatılamadı.", ex);
            await RunOnUiThreadAsync(() => IsAlwaysListening = false);
        }
    }

    private void EvaluateVoiceActivity(double level)
    {
        if (!IsAlwaysListening) return;

        // Ses düzeyi 0.08 üzerindeyse ses faaliyeti var kabul edilir
        if (level > 0.08)
        {
            _lastVoiceActivityTime = DateTime.Now;
            if (!_isUserSpeaking)
            {
                _isUserSpeaking = true;
                _ = RunOnUiThreadAsync(() =>
                {
                    OrbState = "recording"; // Orb dinleme durumuna geçer
                    AddTimelineEvent("voice_activity_detected", "Ses faaliyeti algılandı.");
                });
            }
        }
    }

    private async Task CheckVoiceActivityTimeoutAsync()
    {
        if (!IsAlwaysListening || !IsRecording) return;

        var silenceDuration = DateTime.Now - _lastVoiceActivityTime;
        // Kullanıcı konusmaya basladiktan sonra 1.5 saniye boyunca sessizlik olduysa kaydi otomatik bitir
        if (_isUserSpeaking && silenceDuration > TimeSpan.FromMilliseconds(1500))
        {
            _vadTimer?.Stop();
            _isUserSpeaking = false;
            await RunOnUiThreadAsync(async () =>
            {
                AddTimelineEvent("voice_silence_timeout", "Sessizlik süresi doldu, kayıt işleniyor.");
                await StopRecordingAndTranscribeAndSendAsync();
            });
        }
        // Hiç konuşulmadıysa ve 5 saniye geçtiyse kaydı yenile (buffer şişmesin)
        else if (!_isUserSpeaking && silenceDuration > TimeSpan.FromSeconds(5))
        {
            _lastVoiceActivityTime = DateTime.Now;
            await RunOnUiThreadAsync(async () =>
            {
                await _voiceInput.StopRecordingAsync();
                await _voiceInput.StartRecordingAsync();
            });
        }
    }

    private async Task StopRecordingAndTranscribeAndSendAsync(bool requireWakeWord = true)
    {
        await RunOnUiThreadAsync(() =>
        {
            IsRecording = false;
            _recordingOwnedByAlwaysListening = false;
            IsTranscribing = true;
            OrbState = "transcribing";
        });
        AddTimelineEvent("voice_recording_stopped", "Ses kaydı tamamlandı.");
        try
        {
            var audio = await _voiceInput.StopRecordingAsync();
            if (audio is null)
            {
                await RunOnUiThreadAsync(() => OrbState = "error");
                return;
            }
            var localText = await _voiceInput.TranscribeLocalAsync(audio, CancellationToken.None);
            if (string.IsNullOrWhiteSpace(localText))
            {
                await RunOnUiThreadAsync(() =>
                {
                    OrbState = "error";
                    AddTimelineEvent("voice_transcribe_failed", "Yerel whisper.cpp transkripsiyonu kullanılamıyor veya boş sonuç döndü.");
                });
                if (IsAlwaysListening)
                {
                    await Task.Delay(1000);
                    _ = StartAlwaysListeningAsync();
                }
                return;
            }

            var text = localText.Trim();
            var commandText = text;
            if (requireWakeWord)
            {
                var wakeCommand = ExtractWakeWordCommand(text);
                if (!wakeCommand.WakeWordDetected)
                {
                    await RunOnUiThreadAsync(() =>
                    {
                        OrbState = "idle";
                        VoiceModeStatusText = "Sürekli dinleme açık: 'Hey Vortex' bekleniyor.";
                        AddTimelineEvent("wake_word_not_detected", "Yerel transkriptte 'Hey Vortex' bulunamadı; komut gönderilmedi.");
                    });
                    if (IsAlwaysListening)
                    {
                        await Task.Delay(500);
                        _ = StartAlwaysListeningAsync();
                    }
                    return;
                }

                if (string.IsNullOrWhiteSpace(wakeCommand.Command))
                {
                    await RunOnUiThreadAsync(() =>
                    {
                        OrbState = "idle";
                        VoiceModeStatusText = "Yerel transkript kapisi etkinlesti; komut bekleniyor.";
                        AddTimelineEvent("wake_word_without_command", "Yerel transkript kapısı etkinleşti ancak gönderilecek komut yok.");
                    });
                    if (IsAlwaysListening)
                    {
                        await Task.Delay(500);
                        _ = StartAlwaysListeningAsync();
                    }
                    return;
                }

                commandText = wakeCommand.Command;
                AddTimelineEvent("wake_word_detected", "Yerel transkript kapısı komut gönderiyor.");
            }
            else
            {
                AddTimelineEvent("voice_transcribed_local", "Manuel ses gönderimi: " + commandText);
            }

            await RunOnUiThreadAsync(async () =>
            {
                // Ses modundaki mesajları geçici listede biriktiriyoruz
                InputText = commandText;
                await SendAsync();

                // Asistan yanıtını da ses modundaki geçici listeye ekliyoruz
                if (Messages.Count > 0 && Messages[^1].IsAssistant)
                {
                    _tempVoiceMessages.Add(Messages[^1]);
                }

                // Eğer sürekli dinleme açıksa, bir sonraki ses döngüsünü başlat
                if (IsAlwaysListening)
                {
                    await Task.Delay(1000); // Küçük bir nefes payı
                    _ = StartAlwaysListeningAsync();
                }
            });
        }
        catch (Exception ex)
        {
            await RunOnUiThreadAsync(() => OrbState = "error");
            DesktopLogService.Error("Sürekli dinleme transkripsiyon hatası.", ex);
        }
        finally
        {
            await RunOnUiThreadAsync(() => IsTranscribing = false);
        }
    }

    public static WakeWordCommandResult ExtractWakeWordCommand(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return new WakeWordCommandResult(false, string.Empty, string.Empty);
        }

        var match = WakeWordRegex().Match(transcript);
        if (!match.Success)
        {
            return new WakeWordCommandResult(false, NormalizeWakeWordText(transcript), string.Empty);
        }

        var before = transcript[..match.Index];
        var after = transcript[(match.Index + match.Length)..];
        var command = CleanWakeCommand(before + " " + after);
        return new WakeWordCommandResult(true, NormalizeWakeWordText(transcript), command);
    }

    public static string NormalizeWakeWordText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var lowered = value.Trim().ToLowerInvariant();
        var alphanumericSpacing = Regex.Replace(lowered, @"[^\p{L}\p{Nd}]+", " ");
        return Regex.Replace(alphanumericSpacing, @"\s+", " ").Trim();
    }

    private static string CleanWakeCommand(string value)
    {
        var cleaned = Regex.Replace(value, @"^[\s\p{P}\p{S}]+|[\s\p{P}\p{S}]+$", string.Empty);
        return Regex.Replace(cleaned, @"\s+", " ").Trim();
    }

    [GeneratedRegex(@"(?i)(?<![\p{L}\p{Nd}])hey\s*[^\p{L}\p{Nd}\s]*\s*vortex(?![\p{L}\p{Nd}])", RegexOptions.CultureInvariant)]
    private static partial Regex WakeWordRegex();

    private void CommitVoiceModeTranscriptToActiveChat()
    {
        // Ses modunda toplanan tüm mesajları aktif sohbete kalıcı olarak kaydet
        if (_tempVoiceMessages.Count > 0)
        {
            _tempVoiceMessages.Clear();
            AddTimelineEvent("voice_transcript_committed", "Konuşma geçmişi sohbete aktarıldı.");
        }
    }

    private static List<LocalChatApiMessage> BuildLocalChatContext(IReadOnlyList<ChatMessageDto> history, string currentText)
    {
        var context = new List<LocalChatApiMessage>
        {
            new("system", "Bu uygulama Pardus Linux üzerinde çalışır. İşletim sistemiyle ilgili sorularda Pardus'un Debian tabanlı olduğunu dikkate al; emin olmadığın sürüm, kurulu paket veya cihaz durumunu varsayma. Sistem üzerinde işlem yaptığını veya dosya/ayar incelediğini iddia etme. Yanıtların yalnızca öneridir: komutlar kullanıcı tarafından gözden geçirilip açıkça onaylanmadıkça çalıştırılmaz. Silme, servis değiştirme, paket kaldırma veya sistem ayarı değiştirme gibi etkili işlemlerde riski belirt ve önce onay iste. Gizli anahtar, parola, erişim belirteci veya özel yapılandırma isteme ya da tekrar etme. Önceki sohbet bağlamını dikkate al. Bilmediğin bilgiyi hatırlıyormuş gibi uydurma. Türkçe, doğrudan ve yardımcı yanıt ver.")
        };

        foreach (var message in history.TakeLast(20))
        {
            var role = message.Role switch
            {
                "assistant" => "assistant",
                "system" => "system",
                _ => "user"
            };

            if (!string.IsNullOrWhiteSpace(message.Content)) context.Add(new LocalChatApiMessage(role, message.Content));
        }

        if (history.Count == 0 || !string.Equals(history[^1].Role, "user", StringComparison.OrdinalIgnoreCase) || !string.Equals(history[^1].Content, currentText, StringComparison.Ordinal))
        {
            context.Add(new LocalChatApiMessage("user", currentText));
        }

        return context;
    }

    private static string FormatLocalChatContextForResponses(IEnumerable<LocalChatApiMessage> messages) =>
        string.Join("\n\n", messages.Select(message => $"{message.role}: {message.content}"));
}

public sealed record WakeWordCommandResult(bool WakeWordDetected, string NormalizedTranscript, string Command);

public sealed record LocalChatApiMessage(string role, string content);

public sealed partial class MessageViewModel : ObservableObject
{
    [ObservableProperty] private string role;
    [ObservableProperty] private string content;
    [ObservableProperty] private string displayContent = string.Empty;
    [ObservableProperty] private bool hasLocalAgentOffer;
    [ObservableProperty] private string localAgentOfferText = string.Empty;
    [ObservableProperty] private string localAgentOfferRiskText = string.Empty;
    [ObservableProperty] private string localAgentOfferCommand = string.Empty;

    public ObservableCollection<CodeBlockViewModel> CodeBlocks { get; } = new();

    public bool IsUser => string.Equals(Role, "Kullanıcı", StringComparison.OrdinalIgnoreCase);
    public bool IsAssistant => string.Equals(Role, "Asistan", StringComparison.OrdinalIgnoreCase) || string.Equals(Role, "Vortex", StringComparison.OrdinalIgnoreCase);
    public string DisplayRole => IsUser ? "Sen" : IsAssistant ? "Vortex" : Role;
    public string AccentBrush => IsUser ? "#7C3AED" : "#14B8A6";
    public string BubbleBrush => IsUser ? "#2A1F4F" : "#17253D";

    public MessageViewModel(string role, string content)
    {
        this.role = role;
        this.content = content;
        RefreshPresentation(content);
    }

    public void SetLocalAgentOffer(string text, string riskText, string command)
    {
        HasLocalAgentOffer = true;
        LocalAgentOfferText = text;
        LocalAgentOfferRiskText = riskText;
        LocalAgentOfferCommand = command;
    }

    public void ClearLocalAgentOffer()
    {
        HasLocalAgentOffer = false;
        LocalAgentOfferText = string.Empty;
        LocalAgentOfferRiskText = string.Empty;
        LocalAgentOfferCommand = string.Empty;
    }

    partial void OnContentChanged(string value) => RefreshPresentation(value);

    private void RefreshPresentation(string? value)
    {
        CodeBlocks.Clear();
        var text = value ?? string.Empty;
        var output = new StringBuilder();
        var code = new StringBuilder();
        var language = string.Empty;
        var inCodeBlock = false;

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inCodeBlock)
                {
                    CodeBlocks.Add(new CodeBlockViewModel(language, code.ToString().TrimEnd()));
                    code.Clear();
                    language = string.Empty;
                    inCodeBlock = false;
                }
                else
                {
                    language = line.Trim()[3..].Trim();
                    inCodeBlock = true;
                }
                continue;
            }

            if (inCodeBlock)
            {
                code.AppendLine(line);
                continue;
            }

            var clean = Regex.Replace(line, @"^\s{0,3}#{1,6}\s+", string.Empty);
            clean = clean.Replace("**", string.Empty).Replace("__", string.Empty);
            output.AppendLine(clean);
        }

        if (inCodeBlock && code.Length > 0)
        {
            CodeBlocks.Add(new CodeBlockViewModel(language, code.ToString().TrimEnd()));
        }

        DisplayContent = output.ToString().Trim();
    }
}

public sealed record CodeBlockViewModel(string Language, string Content)
{
    public string Label => string.IsNullOrWhiteSpace(Language) ? "Kod / komut" : Language;
}

public sealed record ActivityEventViewModel(string Kind, string Detail, DateTimeOffset Timestamp)
{
    public string DisplayKind => Kind.ToLowerInvariant() switch
    {
        "request_received" => "Istek alindi",
        "job_created" => "Is olusturuldu",
        "pending" => "Kuyrukta bekliyor",
        "queued" => "Kuyruga alindi",
        "claimed" => "Worker aldi",
        "running" => "Çalışıyor",
        "completed" => "Tamamlandi",
        "failed" => "Basarisiz",
        "cancelled" => "Iptal edildi",
        "timedout" => "Süre doldu",
        "report_sent" => "Sonuç alındı",
        "request_failed" => "Istek basarisiz",
        "request_cancelled" => "Istek durduruldu",
        "job_unavailable" => "Is okunamadi",
        "polling_timeout" => "Takip süresi doldu",
        "voice_recording_started" => "Ses kaydi basladi",
        "voice_recording_stopped" => "Ses kaydi durdu",
        "voice_transcribed" or "voice_transcribed_local" => "Ses metne çevrildi",
        "voice_transcribe_failed" => "Ses tanima basarisiz",
        "response_stopped" => "Yanit durduruldu",
        _ => Kind.Replace('_', ' ')
    };
    public string AccentBrush => Kind.ToLowerInvariant() switch
    {
        "request_failed" or "job_unavailable" or "polling_timeout" or "failed" or "cancelled" or "timedout" or "error" or "voice_transcribe_failed" => "#F87171",
        "completed" or "report_sent" or "voice_transcribed" or "voice_transcribed_local" => "#34D399",
        "running" or "claimed" or "processing" or "pending" or "queued" => "#38BDF8",
        _ => "#94A3B8"
    };
    public string DisplayText => $"{Timestamp:HH:mm:ss} • {DisplayKind} • {Detail}";
}

public sealed partial class ActiveJobViewModel : ObservableObject
{
    [ObservableProperty] private string status = "Pending";

    public Guid JobId { get; }
    public string ShortJobId => JobId.ToString("N")[..8];
    public string DisplayJobId => $"Is #{ShortJobId}";
    public string DisplayStatus => Status switch
    {
        "Pending" => "Kuyrukta bekliyor",
        "Queued" => "Kuyruga alindi",
        "Claimed" => "Worker aldi",
        "Running" => "Çalışıyor",
        "Completed" => "Tamamlandi",
        "Failed" => "Basarisiz",
        "Cancelled" => "Iptal edildi",
        "TimedOut" => "Süre doldu",
        _ => Status
    };

    partial void OnStatusChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayStatus));
    }

    public ActiveJobViewModel(Guid jobId)
    {
        JobId = jobId;
    }
}

public sealed partial class ChatSessionViewModel : ObservableObject
{
    [ObservableProperty] private string title;
    [ObservableProperty] private bool isFavorite;
    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private bool isEditing;
    [ObservableProperty] private string editTitle = string.Empty;

    public Guid Id { get; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public string BackgroundBrush => IsSelected ? "#17253D" : "#00000000";
    public string BorderBrush => IsSelected ? "#243B55" : "#00000000";
    public string FavoriteColor => IsFavorite ? "#FBBF24" : "#475569";

    public ChatSessionViewModel(Guid id, string title, DateTimeOffset updatedAt, bool isFavorite = false)
    {
        Id = id;
        this.title = title;
        UpdatedAt = updatedAt;
        this.isFavorite = isFavorite;
    }

    public void Update(string newTitle, DateTimeOffset updatedAt)
    {
        Title = newTitle;
        UpdatedAt = updatedAt;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(BackgroundBrush));
        OnPropertyChanged(nameof(BorderBrush));
    }

    partial void OnIsFavoriteChanged(bool value)
    {
        OnPropertyChanged(nameof(FavoriteColor));
    }
}
