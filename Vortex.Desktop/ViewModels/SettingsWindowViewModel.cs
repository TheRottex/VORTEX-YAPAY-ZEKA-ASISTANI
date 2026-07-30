using System.Diagnostics;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vortex.Desktop.Services;
using Vortex.Shared;
using System.Linq;

namespace Vortex.Desktop.ViewModels;

public sealed partial class SettingsWindowViewModel : ObservableObject
{
    private readonly BackendClient _backendClient;
    private readonly MainWindowViewModel _mainViewModel;
    private readonly WhisperModelService _whisperModels = new();
    private bool _syncingWhisperModel;

    [ObservableProperty] private string selectedCategory = "General";
    [ObservableProperty] private bool rememberMe;
    [ObservableProperty] private bool isOfflineMode;
    [ObservableProperty] private long storageUsedBytes;
    [ObservableProperty] private string storageQuotaText = "Depolama bilgisi için oturum açın";
    [ObservableProperty] private bool isAlwaysListening;
    [ObservableProperty] private bool pushToTalkEnabled;
    [ObservableProperty] private double vadThreshold = 0.08;
    [ObservableProperty] private string archivedSearchText = string.Empty;
    [ObservableProperty] private string archivedChatsStatusText = string.Empty;
    [ObservableProperty] private bool elevenLabsTtsEnabled;
    [ObservableProperty] private string elevenLabsApiKey = string.Empty;
    [ObservableProperty] private string elevenLabsVoiceId = string.Empty;
    [ObservableProperty] private bool minMaxTtsEnabled;
    [ObservableProperty] private string minMaxApiKey = string.Empty;
    [ObservableProperty] private string minMaxTtsVoiceId = string.Empty;
    [ObservableProperty] private string minMaxTtsModelId = "speech-01-turbo";
    [ObservableProperty] private string whisperModelId = "small";
    [ObservableProperty] private int whisperModelIndex = 2;
    [ObservableProperty] private string whisperInstallLog = string.Empty;
    [ObservableProperty] private bool isWhisperInstalling;
    [ObservableProperty] private bool isWhisperInstallFailed;
    [ObservableProperty] private string whisperStatusText = "Whisper durumu izleniyor.";
    [ObservableProperty] private string whisperModelSizeText = string.Empty;
    [ObservableProperty] private string wakeWordEngineStatusText = WakeWordEngineStatusService.GetCurrent().Detail;
    [ObservableProperty] private string kokoroStatusText = string.Empty;
    [ObservableProperty] private string kokoroInstallLog = string.Empty;
    [ObservableProperty] private bool isKokoroInstalling;
    [ObservableProperty] private bool isKokoroInstallFailed;
    [ObservableProperty] private string localAgentBaseUrl = "http://127.0.0.1:47891";
    [ObservableProperty] private string localAgentSecret = string.Empty;
    [ObservableProperty] private string localAgentStatusText = "LocalAgent bağlantısı test edilmedi.";
    [ObservableProperty] private LocalAgentDeviceDto? selectedLocalAgentDevice;
    [ObservableProperty] private string localAgentDeviceStatusText = string.Empty;
    [ObservableProperty] private bool isLocalAgentDeviceLoading;
    [ObservableProperty] private string localLlmBaseUrl = string.Empty;
    [ObservableProperty] private string localLlmApiKey = string.Empty;
    [ObservableProperty] private string localLlmModel = string.Empty;
    [ObservableProperty] private string localLlmStatusText = "Yerel LLM bağlantısı test edilmedi.";

    [ObservableProperty] private ObservableCollection<string> audioInputDevices = new();
    [ObservableProperty] private ObservableCollection<string> audioOutputDevices = new();
    [ObservableProperty] private string selectedInputDevice = "Varsayılan Giriş Aygıtı";
    [ObservableProperty] private string selectedOutputDevice = "Varsayılan Çıkış Aygıtı";
    [ObservableProperty] private double inputVolume = 1.0;
    [ObservableProperty] private double outputVolume = 1.0;
    [ObservableProperty] private bool noiseCancellationEnabled = true;
    [ObservableProperty] private bool autoVoiceReplyEnabled;
    [ObservableProperty] private double speechSensitivity = 0.5;
    [ObservableProperty] private ObservableCollection<string> mainUiScales = new() { "80%", "90%", "100%", "110%", "125%", "150%" };
    [ObservableProperty] private ObservableCollection<string> compactUiScales = new() { "80%", "90%", "100%", "110%", "125%", "150%" };
    [ObservableProperty] private string selectedMainUiScale = "100%";
    [ObservableProperty] private string selectedCompactUiScale = "100%";
    public ObservableCollection<string> ThemePreferenceOptions { get; } = new()
    {
        "Sistem teması (önerilen)",
        "Koyu Tema",
        "Açık Tema"
    };
    [ObservableProperty] private string selectedThemePreference = "Sistem teması (önerilen)";
    private bool _suppressThemePersist;

    private void PopulateAudioDevices()
    {
        AudioInputDevices.Clear();
        AudioOutputDevices.Clear();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var naudioAssembly = typeof(NAudio.Wave.WaveInEvent).Assembly;

                var waveInType = naudioAssembly.GetType("NAudio.Wave.WaveInEvent");
                if (waveInType != null)
                {
                    var deviceCountProp = waveInType.GetProperty("DeviceCount", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    var getCapabilitiesMethod = waveInType.GetMethod("GetCapabilities", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (deviceCountProp != null && getCapabilitiesMethod != null)
                    {
                        var count = (int)deviceCountProp.GetValue(null)!;
                        for (int i = 0; i < count; i++)
                        {
                            var caps = getCapabilitiesMethod.Invoke(null, new object[] { i });
                            if (caps != null)
                            {
                                var nameProp = caps.GetType().GetProperty("ProductName");
                                if (nameProp != null)
                                {
                                    var name = nameProp.GetValue(caps) as string;
                                    if (!string.IsNullOrEmpty(name)) AudioInputDevices.Add(name);
                                }
                            }
                        }
                    }
                }

                var waveOutType = naudioAssembly.GetType("NAudio.Wave.WaveOut");
                if (waveOutType == null)
                {
                    waveOutType = naudioAssembly.GetType("NAudio.Wave.WaveOutEvent");
                }
                if (waveOutType != null)
                {
                    var deviceCountProp = waveOutType.GetProperty("DeviceCount", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    var getCapabilitiesMethod = waveOutType.GetMethod("GetCapabilities", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (deviceCountProp != null && getCapabilitiesMethod != null)
                    {
                        var count = (int)deviceCountProp.GetValue(null)!;
                        for (int i = 0; i < count; i++)
                        {
                            var caps = getCapabilitiesMethod.Invoke(null, new object[] { i });
                            if (caps != null)
                            {
                                var nameProp = caps.GetType().GetProperty("ProductName");
                                if (nameProp != null)
                                {
                                    var name = nameProp.GetValue(caps) as string;
                                    if (!string.IsNullOrEmpty(name)) AudioOutputDevices.Add(name);
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
        }
        if (AudioInputDevices.Count == 0) AudioInputDevices.Add("Varsayılan Giriş Aygıtı");
        if (AudioOutputDevices.Count == 0) AudioOutputDevices.Add("Varsayılan Çıkış Aygıtı");
        SelectedInputDevice = AudioInputDevices[0];
        SelectedOutputDevice = AudioOutputDevices[0];
    }

    public ObservableCollection<string> Categories { get; } = new()
    {
        "General", "Appearance", "Voice", "AI Models", "Providers", "Workspace", "Notifications", "Advanced", "About", "Archived Chats"
    };

    public ObservableCollection<ChatSessionViewModel> ArchivedChats { get; } = new();
    public ObservableCollection<LocalAgentDeviceDto> RegisteredLocalAgentDevices => _mainViewModel.RegisteredLocalAgentDevices;
    public string SelectedLocalAgentDeviceDetail => _mainViewModel.SelectedLocalAgentDeviceDetail;

    public bool IsGeneralSelected => SelectedCategory == "General";
    public bool IsAppearanceSelected => SelectedCategory == "Appearance";
    public bool IsVoiceSelected => SelectedCategory == "Voice";
    public bool IsAiModelsSelected => SelectedCategory == "AI Models";
    public bool IsProvidersSelected => SelectedCategory == "Providers";
    public bool IsWorkspaceSelected => SelectedCategory == "Workspace";
    public bool IsNotificationsSelected => SelectedCategory == "Notifications";
    public bool IsAdvancedSelected => SelectedCategory == "Advanced";
    public bool IsAboutSelected => SelectedCategory == "About";
    public bool IsArchivedChatsSelected => SelectedCategory == "Archived Chats";

    public SettingsWindowViewModel(BackendClient backendClient, MainWindowViewModel mainViewModel)
    {
        _backendClient = backendClient;
        _mainViewModel = mainViewModel;

        // Sync initial state from MainViewModel
        IsAlwaysListening = _mainViewModel.IsAlwaysListening;
        RememberMe = _mainViewModel.RememberMe;
        IsOfflineMode = _mainViewModel.IsOfflineMode;
        AutoVoiceReplyEnabled = _mainViewModel.IsVoiceReplyEnabled;
        KokoroStatusText = _mainViewModel.KokoroStatusText;
        KokoroInstallLog = _mainViewModel.KokoroInstallLog;
        IsKokoroInstalling = _mainViewModel.IsKokoroInstalling;
        IsKokoroInstallFailed = _mainViewModel.IsKokoroInstallFailed;
        ElevenLabsTtsEnabled = _mainViewModel.ElevenLabsTtsEnabled;
        ElevenLabsApiKey = _mainViewModel.ElevenLabsApiKey;
        ElevenLabsVoiceId = _mainViewModel.ElevenLabsVoiceId;
        MinMaxTtsEnabled = _mainViewModel.MinMaxTtsEnabled;
        MinMaxApiKey = _mainViewModel.MinMaxApiKey;
        MinMaxTtsVoiceId = _mainViewModel.MinMaxTtsVoiceId;
        MinMaxTtsModelId = _mainViewModel.MinMaxTtsModelId;
        WhisperModelId = _mainViewModel.WhisperModelId;
        UpdateWhisperModelSize();
        LocalAgentBaseUrl = _mainViewModel.LocalAgentBaseUrl;
        LocalAgentSecret = _mainViewModel.LocalAgentSecret;
        LocalAgentStatusText = _mainViewModel.LocalAgentStatusText;
        SelectedLocalAgentDevice = _mainViewModel.SelectedLocalAgentDevice;
        LocalAgentDeviceStatusText = _mainViewModel.LocalAgentDeviceStatusText;
        IsLocalAgentDeviceLoading = _mainViewModel.IsLocalAgentDeviceLoading;
        LocalLlmBaseUrl = _mainViewModel.LocalLlmBaseUrl;
        LocalLlmApiKey = _mainViewModel.LocalLlmApiKey;
        LocalLlmModel = _mainViewModel.LocalLlmModel;
        LocalLlmStatusText = _mainViewModel.LocalLlmStatusText;
        _suppressThemePersist = true;
        SelectedThemePreference = ToThemeDisplay(_mainViewModel.ThemePreference);
        _suppressThemePersist = false;

        _mainViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.IsAlwaysListening))
            {
                IsAlwaysListening = _mainViewModel.IsAlwaysListening;
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.RememberMe))
            {
                RememberMe = _mainViewModel.RememberMe;
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.IsOfflineMode))
            {
                IsOfflineMode = _mainViewModel.IsOfflineMode;
                KokoroStatusText = _mainViewModel.KokoroStatusText;
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.IsVoiceReplyEnabled))
            {
                AutoVoiceReplyEnabled = _mainViewModel.IsVoiceReplyEnabled;
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.KokoroStatusText))
            {
                KokoroStatusText = _mainViewModel.KokoroStatusText;
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.KokoroInstallLog))
            {
                KokoroInstallLog = _mainViewModel.KokoroInstallLog;
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.IsKokoroInstalling))
            {
                IsKokoroInstalling = _mainViewModel.IsKokoroInstalling;
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.IsKokoroInstallFailed))
            {
                IsKokoroInstallFailed = _mainViewModel.IsKokoroInstallFailed;
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.ElevenLabsTtsEnabled))
            {
                ElevenLabsTtsEnabled = _mainViewModel.ElevenLabsTtsEnabled;
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.ElevenLabsApiKey))
            {
                ElevenLabsApiKey = _mainViewModel.ElevenLabsApiKey;
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.ElevenLabsVoiceId))
            {
                ElevenLabsVoiceId = _mainViewModel.ElevenLabsVoiceId;
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.MinMaxTtsEnabled))
            {
                MinMaxTtsEnabled = _mainViewModel.MinMaxTtsEnabled;
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.MinMaxApiKey))
            {
                MinMaxApiKey = _mainViewModel.MinMaxApiKey;
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.MinMaxTtsVoiceId))
            {
                MinMaxTtsVoiceId = _mainViewModel.MinMaxTtsVoiceId;
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.MinMaxTtsModelId))
            {
                MinMaxTtsModelId = _mainViewModel.MinMaxTtsModelId;
                WhisperModelId = _mainViewModel.WhisperModelId;
                UpdateWhisperModelSize();
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.LocalAgentBaseUrl))
            {
                LocalAgentBaseUrl = _mainViewModel.LocalAgentBaseUrl;
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.LocalAgentSecret))
            {
                LocalAgentSecret = _mainViewModel.LocalAgentSecret;
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.LocalAgentStatusText))
            {
                LocalAgentStatusText = _mainViewModel.LocalAgentStatusText;
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.SelectedLocalAgentDevice))
            {
                SelectedLocalAgentDevice = _mainViewModel.SelectedLocalAgentDevice;
                OnPropertyChanged(nameof(SelectedLocalAgentDeviceDetail));
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.LocalAgentDeviceStatusText))
            {
                LocalAgentDeviceStatusText = _mainViewModel.LocalAgentDeviceStatusText;
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.IsLocalAgentDeviceLoading))
            {
                IsLocalAgentDeviceLoading = _mainViewModel.IsLocalAgentDeviceLoading;
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.LocalLlmBaseUrl))
            {
                LocalLlmBaseUrl = _mainViewModel.LocalLlmBaseUrl;
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.LocalLlmApiKey))
            {
                LocalLlmApiKey = _mainViewModel.LocalLlmApiKey;
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.LocalLlmModel))
            {
                LocalLlmModel = _mainViewModel.LocalLlmModel;
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.LocalLlmStatusText))
            {
                LocalLlmStatusText = _mainViewModel.LocalLlmStatusText;
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.ThemePreference))
            {
                _suppressThemePersist = true;
                SelectedThemePreference = ToThemeDisplay(_mainViewModel.ThemePreference);
                _suppressThemePersist = false;
            }
        };

        // Load storage usage info
        _ = LoadStorageInfoAsync();
        _ = LoadRememberMeAsync();
        PopulateAudioDevices();
        SelectedMainUiScale = $"{(int)(_mainViewModel.MainUiScale * 100)}%";
        SelectedCompactUiScale = $"{(int)(_mainViewModel.CompactUiScale * 100)}%";
    }

    partial void OnSelectedThemePreferenceChanged(string value)
    {
        if (_suppressThemePersist || string.IsNullOrWhiteSpace(value)) return;
        _mainViewModel.ThemePreference = FromThemeDisplay(value);
    }

    private static string ToThemeDisplay(string? preference)
    {
        var p = MainWindowViewModel.NormalizeThemePreference(preference);
        return p switch
        {
            "Light" => "Açık Tema",
            "Dark" => "Koyu Tema",
            _ => "Sistem teması (önerilen)"
        };
    }

    private static string FromThemeDisplay(string display) => display switch
    {
        "Açık Tema" => "Light",
        "Koyu Tema" => "Dark",
        _ => "System"
    };

    partial void OnSelectedMainUiScaleChanged(string value)
    {
        if (value != null && double.TryParse(value.Replace("%", ""), out var pct))
        {
            _mainViewModel.MainUiScale = pct / 100.0;
        }
    }

    partial void OnSelectedCompactUiScaleChanged(string value)
    {
        if (value != null && double.TryParse(value.Replace("%", ""), out var pct))
        {
            _mainViewModel.CompactUiScale = pct / 100.0;
        }
    }

    private bool _suppressRememberMePersist;

    private async Task LoadRememberMeAsync()
    {
        try
        {
            var settings = await _backendClient.GetSettingsAsync(CancellationToken.None);
            _suppressRememberMePersist = true;
            RememberMe = settings.RememberMe;
        }
        catch (Exception ex)
        {
            DesktopLogService.Error("Beni hatırla tercihi yüklenemedi.", ex);
        }
        finally
        {
            _suppressRememberMePersist = false;
        }
    }

    partial void OnIsOfflineModeChanged(bool value)
    {
        _mainViewModel.IsOfflineMode = value;
    }

    partial void OnAutoVoiceReplyEnabledChanged(bool value)
    {
        _mainViewModel.IsVoiceReplyEnabled = value;
    }

    partial void OnRememberMeChanged(bool value)
    {
        if (_suppressRememberMePersist) return;
        _mainViewModel.RememberMe = value;
        _ = _backendClient.SetRememberMeAsync(value, CancellationToken.None);
    }

    private async Task LoadStorageInfoAsync()
    {
        try
        {
            var me = await _backendClient.GetMeAsync(CancellationToken.None);
            if (me is not null)
            {
                StorageUsedBytes = me.StorageUsedBytes;
                var usedGb = me.StorageUsedBytes / 1024.0 / 1024.0 / 1024.0;
                var quotaGb = me.StorageQuotaBytes / 1024.0 / 1024.0 / 1024.0;
                StorageQuotaText = $"{usedGb:F2} GB / {quotaGb:F2} GB kullanılıyor";
            }
        }
        catch (Exception ex)
        {
            DesktopLogService.Error("Depolama kullanım bilgisi yüklenemedi.", ex);
        }
    }

    partial void OnSelectedCategoryChanged(string value)
    {
        OnPropertyChanged(nameof(IsGeneralSelected));
        OnPropertyChanged(nameof(IsAppearanceSelected));
        OnPropertyChanged(nameof(IsVoiceSelected));
        OnPropertyChanged(nameof(IsAiModelsSelected));
        OnPropertyChanged(nameof(IsProvidersSelected));
        OnPropertyChanged(nameof(IsWorkspaceSelected));
        OnPropertyChanged(nameof(IsNotificationsSelected));
        OnPropertyChanged(nameof(IsAdvancedSelected));
        OnPropertyChanged(nameof(IsAboutSelected));
        OnPropertyChanged(nameof(IsArchivedChatsSelected));

        if (value == "Archived Chats")
        {
            _ = LoadArchivedChatsAsync(ArchivedSearchText);
        }
    }

    partial void OnArchivedSearchTextChanged(string value)
    {
        _ = LoadArchivedChatsAsync(value);
    }

    partial void OnIsAlwaysListeningChanged(bool value)
    {
        _mainViewModel.IsAlwaysListening = value;
    }

    partial void OnElevenLabsTtsEnabledChanged(bool value)
    {
        _mainViewModel.ElevenLabsTtsEnabled = value;
    }

    partial void OnElevenLabsApiKeyChanged(string value)
    {
        _mainViewModel.ElevenLabsApiKey = value;
    }

    partial void OnElevenLabsVoiceIdChanged(string value)
    {
        _mainViewModel.ElevenLabsVoiceId = value;
    }

    partial void OnMinMaxTtsEnabledChanged(bool value)
    {
        _mainViewModel.MinMaxTtsEnabled = value;
    }

    partial void OnMinMaxApiKeyChanged(string value)
    {
        _mainViewModel.MinMaxApiKey = value;
    }

    partial void OnMinMaxTtsVoiceIdChanged(string value)
    {
        _mainViewModel.MinMaxTtsVoiceId = value;
    }

    partial void OnMinMaxTtsModelIdChanged(string value)
    {
        _mainViewModel.MinMaxTtsModelId = value;
    }

    partial void OnWhisperModelIdChanged(string value)
    {
        if (_syncingWhisperModel) return;
        _syncingWhisperModel = true;
        try
        {
            _mainViewModel.WhisperModelId = value;
            var index = WhisperModelService.AvailableModels.ToList().FindIndex(model => string.Equals(model.Id, value, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) WhisperModelIndex = index;
            UpdateWhisperModelSize();
        }
        finally
        {
            _syncingWhisperModel = false;
        }
    }


    partial void OnLocalAgentBaseUrlChanged(string value)
    {
        _mainViewModel.LocalAgentBaseUrl = value;
    }

    partial void OnLocalAgentSecretChanged(string value)
    {
        _mainViewModel.LocalAgentSecret = value;
    }

    partial void OnSelectedLocalAgentDeviceChanged(LocalAgentDeviceDto? value)
    {
        if (_mainViewModel.SelectedLocalAgentDevice?.Id != value?.Id)
        {
            _mainViewModel.SelectedLocalAgentDevice = value;
        }
        OnPropertyChanged(nameof(SelectedLocalAgentDeviceDetail));
    }

    [RelayCommand]
    private async Task RefreshLocalAgentDevicesAsync()
    {
        await _mainViewModel.RefreshLocalAgentDevicesAsync();
        SelectedLocalAgentDevice = _mainViewModel.SelectedLocalAgentDevice;
        LocalAgentDeviceStatusText = _mainViewModel.LocalAgentDeviceStatusText;
        IsLocalAgentDeviceLoading = _mainViewModel.IsLocalAgentDeviceLoading;
        OnPropertyChanged(nameof(RegisteredLocalAgentDevices));
        OnPropertyChanged(nameof(SelectedLocalAgentDeviceDetail));
    }

    partial void OnLocalLlmBaseUrlChanged(string value)
    {
        _mainViewModel.LocalLlmBaseUrl = value;
    }

    partial void OnLocalLlmApiKeyChanged(string value)
    {
        _mainViewModel.LocalLlmApiKey = value;
    }

    partial void OnLocalLlmModelChanged(string value)
    {
        _mainViewModel.LocalLlmModel = value;
    }

    [RelayCommand]
    private async Task TestLocalLlmConnectionAsync()
    {
        await _mainViewModel.TestLocalLlmConnectionAsync();
        LocalLlmStatusText = _mainViewModel.LocalLlmStatusText;
    }

    [RelayCommand]
    private async Task TestLocalAgentConnectionAsync()
    {
        // Direct LocalAgent path — not blocked by offline mode or public Server.
        await _mainViewModel.TestLocalAgentConnectionAsync();
        LocalAgentStatusText = _mainViewModel.LocalAgentStatusText;
    }

    [RelayCommand]
    private async Task RerunSetupWizardAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop) return;
        var settingsService = new DesktopSettingsService();
        var loaded = await settingsService.LoadAsync(CancellationToken.None);
        var wizard = App.CreateSetupWizard(settingsService, loaded, isRerun: true);
        if (desktop.MainWindow is not null) wizard.Show(desktop.MainWindow);
        else wizard.Show();
    }

    [RelayCommand]
    private void OpenWhisperModelsPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://huggingface.co/ggerganov/whisper.cpp/tree/main",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            WhisperModelSizeText = $"Whisper model sayfası açılamadı: {ex.Message}";
        }
    }

    partial void OnWhisperModelIndexChanged(int value)
    {
        if (_syncingWhisperModel) return;
        var models = WhisperModelService.AvailableModels;
        if (value < 0 || value >= models.Count) return;
        _syncingWhisperModel = true;
        try
        {
            WhisperModelId = models[value].Id;
        }
        finally
        {
            _syncingWhisperModel = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConsultWhisperInstallFailure))]
    private async Task ConsultWhisperInstallFailureAsync()
    {
        if (!CanConsultWhisperInstallFailure()) return;

        _mainViewModel.WhisperInstallLog = WhisperInstallLog;
        _mainViewModel.IsWhisperInstallFailed = true;
        await _mainViewModel.ConsultWhisperInstallFailureCommand.ExecuteAsync(null);
    }

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

    private void UpdateWhisperModelSize()
    {
        var model = WhisperModelService.AvailableModels.FirstOrDefault(m => string.Equals(m.Id, WhisperModelId, StringComparison.OrdinalIgnoreCase)) ?? WhisperModelService.AvailableModels.First(model => model.Id == "small");
        WhisperModelSizeText = $"{model.DisplayName} — {model.SizeLabel}";
    }

    [RelayCommand]
    private async Task DownloadWhisperModelAsync()
    {
        var model = WhisperModelService.AvailableModels.FirstOrDefault(m => string.Equals(m.Id, WhisperModelId, StringComparison.OrdinalIgnoreCase)) ?? WhisperModelService.AvailableModels.First();
        IsWhisperInstalling = true;
        IsWhisperInstallFailed = false;
        WhisperInstallLog = string.Empty;
        WhisperStatusText = $"İndiriliyor: {model.DisplayName} ({model.SizeLabel})";
        WhisperModelSizeText = WhisperStatusText;

        var lastProgress = DateTimeOffset.UtcNow;
        var stalled = false;
        var watchdog = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var stallDetector = new System.Timers.Timer(15_000) { AutoReset = true, Enabled = true };
        stallDetector.Elapsed += (_, _) =>
        {
            if ((DateTimeOffset.UtcNow - lastProgress) > TimeSpan.FromSeconds(60))
            {
                stalled = true;
                watchdog.Cancel();
                stallDetector.Stop();
            }
        };

        var progress = new Progress<double>(p =>
        {
            lastProgress = DateTimeOffset.UtcNow;
            var percent = (int)(p * 100);
            WhisperModelSizeText = $"İndiriliyor %{percent} — {model.DisplayName} ({model.SizeLabel})";
            WhisperInstallLog = WhisperInstallLog.Length == 0 ? $"%{percent}" : WhisperInstallLog + Environment.NewLine + $"%{percent}";
        });

        try
        {
            await _whisperModels.DownloadAsync(model, progress, watchdog.Token);
            WhisperModelSizeText = $"İndirildi: {model.DisplayName} ({model.SizeLabel})";
            WhisperStatusText = WhisperModelSizeText;
            WhisperInstallLog = WhisperInstallLog.Length == 0 ? "İndirme tamamlandı." : WhisperInstallLog + Environment.NewLine + "İndirme tamamlandı.";
        }
        catch (Exception ex)
        {
            IsWhisperInstallFailed = true;
            var detail = stalled ? "60 saniye boyunca indirme ilerlemesi alınamadı; işlem durduruldu." : ex.Message;
            WhisperModelSizeText = $"İndirme başarısız: {detail}";
            WhisperStatusText = "Whisper indirme başarısız veya dondu. Ayrıntı için günlüğü kontrol edin; Yapay zekâ asistanına danışabilirsiniz.";
            WhisperInstallLog = WhisperInstallLog.Length == 0 ? "[hata] " + detail : WhisperInstallLog + Environment.NewLine + "[hata] " + detail;
            DesktopLogService.Error("Whisper model indirme başarısız.", ex);
        }
        finally
        {
            stallDetector.Dispose();
            watchdog.Dispose();
            IsWhisperInstalling = false;
        }
    }



    [RelayCommand]
    private async Task InstallKokoroAsync()
    {
        if (_mainViewModel.InstallKokoroCommand.CanExecute(null))
        {
            await _mainViewModel.InstallKokoroCommand.ExecuteAsync(null);
        }

        KokoroStatusText = _mainViewModel.KokoroStatusText;
        KokoroInstallLog = _mainViewModel.KokoroInstallLog;
        IsKokoroInstalling = _mainViewModel.IsKokoroInstalling;
        IsKokoroInstallFailed = _mainViewModel.IsKokoroInstallFailed;
    }

    [RelayCommand]
    private void OpenKokoroDocs()
    {
        if (_mainViewModel.OpenKokoroDocsCommand.CanExecute(null))
        {
            _mainViewModel.OpenKokoroDocsCommand.Execute(null);
        }
    }

    [RelayCommand]
    private async Task ConsultKokoroInstallFailureAsync()
    {
        if (_mainViewModel.ConsultKokoroInstallFailureCommand.CanExecute(null))
        {
            await _mainViewModel.ConsultKokoroInstallFailureCommand.ExecuteAsync(null);
        }

        IsKokoroInstallFailed = _mainViewModel.IsKokoroInstallFailed;
        KokoroInstallLog = _mainViewModel.KokoroInstallLog;
        KokoroStatusText = _mainViewModel.KokoroStatusText;
    }

    private async Task LoadArchivedChatsAsync(string query)
    {
        ArchivedChatsStatusText = "Yükleniyor...";
        try
        {
            var result = await _backendClient.ListChatsDetailedAsync(query, true, CancellationToken.None);
            if (!result.Ok)
            {
                ArchivedChatsStatusText = BackendClient.GetChatListFailureMessageForUi(result.Reason);
                return;
            }

            ArchivedChats.Clear();
            foreach (var s in result.Chats!)
            {
                ArchivedChats.Add(new ChatSessionViewModel(s.Id, s.Title, s.UpdatedAt, s.IsFavorite));
            }
            ArchivedChatsStatusText = ArchivedChats.Count == 0 ? "Arşivlenmiş sohbet bulunamadı" : string.Empty;
        }
        catch (Exception ex)
        {
            DesktopLogService.Error("Arşivlenmiş sohbetler yüklenemedi.", ex);
            ArchivedChatsStatusText = "Hata olustu.";
        }
    }

    [RelayCommand]
    private async Task UnarchiveChatAsync(ChatSessionViewModel? chat)
    {
        if (chat is null) return;
        var ok = await _backendClient.ArchiveChatAsync(chat.Id, false, CancellationToken.None);
        if (ok)
        {
            ArchivedChats.Remove(chat);
            // Ana View'daki listeyi güncelle
            await _mainViewModel.RefreshChatsAsync();
        }
    }

    [RelayCommand]
    private async Task DeleteChatAsync(ChatSessionViewModel? chat)
    {
        if (chat is null) return;
        var ok = await _backendClient.DeleteChatAsync(chat.Id, CancellationToken.None);
        if (ok)
        {
            ArchivedChats.Remove(chat);
        }
    }
}














