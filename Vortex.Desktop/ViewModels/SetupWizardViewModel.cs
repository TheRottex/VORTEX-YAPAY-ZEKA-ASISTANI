using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vortex.Desktop.Services;

namespace Vortex.Desktop.ViewModels;

public sealed partial class SetupWizardViewModel : ObservableObject
{
    public const int CurrentVersion = 2;
    private readonly DesktopSettingsService _settingsService;
    private readonly DesktopSetupReadinessService _readinessService;
    private readonly Func<DesktopSettings, CancellationToken, Task<DesktopNetworkDiagnosticsReport>> _runDiagnostics;
    private readonly Func<CancellationToken, Task<LocalAgentRuntimeResult>> _ensureLocalAgentReady;
    private readonly bool _isRerun;
    private DesktopSettings _originalSettings;
    private bool _localAgentResolved;

    [ObservableProperty] private int stepIndex;
    [ObservableProperty] private string proxyMode = "SystemDefault";
    [ObservableProperty] private string manualHttpProxyUrl = string.Empty;
    [ObservableProperty] private string manualHttpsProxyUrl = string.Empty;
    [ObservableProperty] private string proxyUsername = string.Empty;
    [ObservableProperty] private string proxyPassword = string.Empty;
    [ObservableProperty] private string networkStatus = "Ağ tanılaması çalıştırılmadı.";
    [ObservableProperty] private string accountStatus = "Hesap oturumu ana pencerede açılır.";
    [ObservableProperty] private string localAgentStatus = "Vortex yerel yardımcısı henüz doğrulanmadı.";
    [ObservableProperty] private string finishStatus = string.Empty;
    [ObservableProperty] private bool acceptLicense;

    public ObservableCollection<string> ProxyModes { get; } = new() { "SystemDefault", "NoProxy", "Manual" };
    public ObservableCollection<SetupReadinessItem> VoiceReadiness { get; } = new();
    public event EventHandler<DesktopSettings>? Finished;

    public bool CanGoBack => StepIndex > 0;
    public bool IsLastStep => StepIndex == 5;
    public bool IsNotLastStep => !IsLastStep;
    public bool IsWelcomeStep => StepIndex == 0;
    public bool IsNetworkStep => StepIndex == 1;
    public bool IsAccountStep => StepIndex == 2;
    public bool IsLocalAgentStep => StepIndex == 3;
    public bool IsVoiceStep => StepIndex == 4;
    public bool IsSummaryStep => StepIndex == 5;

    public SetupWizardViewModel(
        DesktopSettingsService settingsService,
        DesktopSettings settings,
        DesktopSetupReadinessService readinessService,
        Func<DesktopSettings, CancellationToken, Task<DesktopNetworkDiagnosticsReport>> runDiagnostics,
        Func<CancellationToken, Task<LocalAgentRuntimeResult>> ensureLocalAgentReady,
        bool isRerun)
    {
        _settingsService = settingsService;
        _originalSettings = settings;
        _readinessService = readinessService;
        _runDiagnostics = runDiagnostics;
        _ensureLocalAgentReady = ensureLocalAgentReady;
        _isRerun = isRerun;
        ProxyMode = settings.ProxyMode;
        ManualHttpProxyUrl = settings.ManualHttpProxyUrl;
        ManualHttpsProxyUrl = settings.ManualHttpsProxyUrl;
        ProxyUsername = settings.ProxyUsername;
        ProxyPassword = settings.ProxyPassword;
        RefreshVoiceReadiness();
    }

    [RelayCommand]
    private void Next()
    {
        if (StepIndex == 0 && !AcceptLicense)
        {
            FinishStatus = "Devam etmek için MIT lisans bildirimini kabul edin.";
            return;
        }
        if (StepIndex < 5) StepIndex++;
    }

    [RelayCommand]
    private void Back()
    {
        if (StepIndex > 0) StepIndex--;
    }

    [RelayCommand]
    private async Task RunNetworkDiagnosticsAsync()
    {
        var settings = BuildSettings();
        try
        {
            await _settingsService.SaveAsync(settings, CancellationToken.None);
            var report = await _runDiagnostics(settings, CancellationToken.None);
            NetworkStatus = report.Succeeded
                ? "DNS/TCP/TLS/health tanılaması başarılı."
                : $"Tanılama hazır değil: {report.Results.LastOrDefault()?.Reason ?? "unknown"}.";
        }
        catch
        {
            NetworkStatus = "Proxy ayarı veya ağ tanılaması başarısız.";
        }
    }

    [RelayCommand]
    private async Task TestLocalAgentAsync()
    {
        var result = await _ensureLocalAgentReady(CancellationToken.None);
        _localAgentResolved = result.Ready;
        LocalAgentStatus = result.Ready
            ? "Vortex yerel yardımcısı hazır."
            : $"Vortex yerel yardımcısı hazır değil: {result.Reason}. Yeniden deneyin veya açıkça atlayın.";
    }

    [RelayCommand]
    private void SkipLocalAgent()
    {
        _localAgentResolved = true;
        LocalAgentStatus = "Vortex yerel yardımcısı açıkça atlandı; hazır olarak işaretlenmedi.";
    }

    [RelayCommand]
    private void RefreshVoiceReadiness()
    {
        VoiceReadiness.Clear();
        foreach (var item in _readinessService.Inspect().Items) VoiceReadiness.Add(item);
    }

    [RelayCommand]
    private async Task FinishAsync()
    {
        if (!AcceptLicense)
        {
            FinishStatus = "Kurulumu tamamlamak için MIT lisans bildirimini kabul edin.";
            return;
        }

        if (!IsSummaryStep)
        {
            FinishStatus = "Kurulumu tamamlamak için özet adımına ilerleyin.";
            return;
        }

        if (!_localAgentResolved)
        {
            FinishStatus = "Yerel yardımcıyı test edin veya açıkça atlayın.";
            return;
        }

        try
        {
            var settings = BuildSettings() with { SetupWizardVersion = CurrentVersion };
            await _settingsService.SaveAsync(settings, CancellationToken.None).ConfigureAwait(false);
            if (_isRerun && ProxyChanged(settings))
            {
                FinishStatus = "Proxy ayarı değişti. Değişikliğin uygulanması için uygulamayı yeniden başlatın ve yeniden giriş yapın.";
            }
            Finished?.Invoke(this, settings);
        }
        catch (Exception ex)
        {
            DesktopLogService.Info($"Kurulum ayarları kaydedilemedi. type={ex.GetType().Name}");
            FinishStatus = "Kurulum ayarları kaydedilemedi. Lütfen tekrar deneyin.";
        }
    }

    private DesktopSettings BuildSettings() => _originalSettings with
    {
        ProxyMode = ProxyMode,
        ManualHttpProxyUrl = ManualHttpProxyUrl,
        ManualHttpsProxyUrl = ManualHttpsProxyUrl,
        ProxyUsername = ProxyUsername,
        ProxyPassword = ProxyPassword
    };

    private bool ProxyChanged(DesktopSettings settings) =>
        !string.Equals(_originalSettings.ProxyMode, settings.ProxyMode, StringComparison.Ordinal) ||
        !string.Equals(_originalSettings.ManualHttpProxyUrl, settings.ManualHttpProxyUrl, StringComparison.Ordinal) ||
        !string.Equals(_originalSettings.ManualHttpsProxyUrl, settings.ManualHttpsProxyUrl, StringComparison.Ordinal) ||
        !string.Equals(_originalSettings.ProxyUsername, settings.ProxyUsername, StringComparison.Ordinal) ||
        !string.Equals(_originalSettings.ProxyPassword, settings.ProxyPassword, StringComparison.Ordinal);

    partial void OnStepIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(IsNotLastStep));
        OnPropertyChanged(nameof(IsWelcomeStep));
        OnPropertyChanged(nameof(IsNetworkStep));
        OnPropertyChanged(nameof(IsAccountStep));
        OnPropertyChanged(nameof(IsLocalAgentStep));
        OnPropertyChanged(nameof(IsVoiceStep));
        OnPropertyChanged(nameof(IsSummaryStep));
    }
}
