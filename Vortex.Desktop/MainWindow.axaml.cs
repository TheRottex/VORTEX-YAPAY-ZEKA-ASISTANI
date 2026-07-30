using System.ComponentModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Vortex.Desktop.Services;
using Vortex.Desktop.ViewModels;

namespace Vortex.Desktop;

public sealed partial class MainWindow : Window
{
    private NativeWebView? _webView;
    private CompactVoiceWindow? _compactWindow;
    private MainWindowViewModel? _viewModel;
    private bool _isClosing;
    private bool _switchingToCompact;
    private bool _hasOpened;
    private bool _wasHiddenForCompact;
    private WindowState _windowStateBeforeCompact = WindowState.Normal;

    private static readonly HashSet<string> AllowedMessages = new(StringComparer.Ordinal)
    {
        "start-recording", "stop-recording", "send-command", "stop-response", "toggle-voice-reply", "compact-mode", "restore-main"
    };

    public MainWindow()
    {
        InitializeComponent();
        Width = 1380;
        Height = 860;
        MinWidth = 1120;
        MinHeight = 700;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.Parse("#020711"));

        SizeChanged += OnWindowSizeChanged;

        DataContextChanged += (_, _) => AttachViewModel(DataContext as MainWindowViewModel);
        Opened += (_, _) =>
        {
            _hasOpened = true;
            _webView ??= new NativeWebView();
            _webView.NavigationCompleted += OnNavigationCompleted;
            _webView.WebMessageReceived += OnWebMessageReceived;
            _webView.EnvironmentRequested += ConfigureWebViewEnvironment;
            if (OrbHost.Child is null) OrbHost.Child = _webView;
            Dispatcher.UIThread.Post(LoadLocalHtml, DispatcherPriority.Background);
        };
        PropertyChanged += OnWindowPropertyChanged;
        Closing += (_, _) =>
        {
            _isClosing = true;
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            if (_compactWindow is not null)
            {
                _compactWindow.PrepareForAppShutdown();
                _compactWindow.Close();
                _compactWindow = null;
            }
        };
    }

    private static void ConfigureWebViewEnvironment(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        e.EnableDevTools = false;
        if (e is WindowsWebView2EnvironmentRequestedEventArgs windows)
        {
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var userDataFolder = Path.Combine(localData, "VortexAI", "Desktop", "WebView2");
            Directory.CreateDirectory(userDataFolder);
            windows.UserDataFolder = userDataFolder;
            windows.ProfileName = "VortexDesktop";
            return;
        }

        if (e is LinuxWpeWebViewEnvironmentRequestedEventArgs linux)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dataRoot = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            var cacheRoot = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (string.IsNullOrWhiteSpace(dataRoot)) dataRoot = Path.Combine(home, ".local", "share");
            if (string.IsNullOrWhiteSpace(cacheRoot)) cacheRoot = Path.Combine(home, ".cache");
            linux.DataDirectory = Path.Combine(dataRoot, "vortex", "desktop", "webview");
            linux.CacheDirectory = Path.Combine(cacheRoot, "vortex", "desktop", "webview");
            Directory.CreateDirectory(linux.DataDirectory);
            Directory.CreateDirectory(linux.CacheDirectory);
        }
    }

    private void AttachViewModel(MainWindowViewModel? viewModel)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = viewModel;
        if (_viewModel is null) return;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ApplyScale();
        ApplyVoiceModeLayout();
        UpdateRightPanelContentLayout();
        _ = SyncVisualStateAsync();
    }

    private async void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsAuthenticated))
        {
            if (_viewModel?.IsAuthenticated == true)
            {
                RestoreMainWindow();
            }
            else
            {
                _compactWindow?.HideCompact();
                Show();
                WindowState = _wasHiddenForCompact ? _windowStateBeforeCompact : WindowState;
                _wasHiddenForCompact = false;
                Activate();
            }
        }

        if (e.PropertyName is nameof(MainWindowViewModel.IsVoiceModeEnabled) or nameof(MainWindowViewModel.IsSidebarCollapsed))
        {
            ApplyVoiceModeLayout();
        }

        if (e.PropertyName is nameof(MainWindowViewModel.IsMascotVisible) or nameof(MainWindowViewModel.IsTasksTabSelected) or nameof(MainWindowViewModel.IsCreditsTabSelected))
        {
            UpdateRightPanelContentLayout();
        }

        if (e.PropertyName is nameof(MainWindowViewModel.MainUiScale) or nameof(MainWindowViewModel.CompactUiScale))
        {
            ApplyScale();
            if (_compactWindow is not null && _viewModel is not null) _compactWindow.ApplyScale(_viewModel.CompactUiScale);
        }

        if (e.PropertyName is nameof(MainWindowViewModel.OrbState) or nameof(MainWindowViewModel.InputAudioLevel) or nameof(MainWindowViewModel.ProviderMode) or nameof(MainWindowViewModel.IsVoiceReplyEnabled) or nameof(MainWindowViewModel.IsAuthenticated))
        {
            await SyncVisualStateAsync();
        }
    }

    private void ApplyScale()
    {
        if (_viewModel is null) return;
        var scale = Math.Clamp(_viewModel.MainUiScale, 0.85, 1.25);
        if (WindowState == WindowState.Normal) Width = 1380 * scale;
        if (WindowState == WindowState.Normal) Height = 860 * scale;
        MinWidth = 1120 * scale;
        MinHeight = 700 * scale;
        AuthenticatedShell.Margin = new Thickness(10 * scale);
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveLayout();
    }

    private void UpdateResponsiveLayout()
    {
        if (_viewModel is null || _viewModel.IsVoiceModeEnabled) return;
        var width = Bounds.Width;
        var sidebarWidth = _viewModel.IsSidebarCollapsed ? 70 : 280;
        const double rightPanelWidth = 360;
        const double shellOuterPadding = 20;
        const double minimumComfortableChatWidth = 760;
        var chatWidthWithRightPanel = width - sidebarWidth - rightPanelWidth - shellOuterPadding;

        if (chatWidthWithRightPanel < minimumComfortableChatWidth)
        {
            AuthenticatedShell.ColumnDefinitions[2].Width = new GridLength(0, GridUnitType.Pixel);
            RightPanel.IsVisible = false;
        }
        else
        {
            AuthenticatedShell.ColumnDefinitions[2].Width = new GridLength(rightPanelWidth, GridUnitType.Pixel);
            RightPanel.IsVisible = true;
        }

        var showContentArea = _viewModel.IsAssistantTabSelected || _viewModel.IsCreditsTabSelected;
        RightPanelLayout.RowDefinitions[1].Height = showContentArea
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0, GridUnitType.Pixel);
    }

    private void UpdateRightPanelContentLayout()
    {
        if (_viewModel is null) return;

        var showContentArea = _viewModel.IsAssistantTabSelected || _viewModel.IsCreditsTabSelected;
        RightPanelLayout.RowDefinitions[1].Height = showContentArea
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0, GridUnitType.Pixel);
    }

    private void ApplyVoiceModeLayout()
    {
        if (_viewModel is null) return;
        var isVoice = _viewModel.IsVoiceModeEnabled;
        var isCollapsed = _viewModel.IsSidebarCollapsed;
        var sidebarWidth = isCollapsed ? 70 : 280;

        if (isVoice)
        {
            AuthenticatedShell.ColumnDefinitions[0].Width = new GridLength(sidebarWidth, GridUnitType.Pixel);
            AuthenticatedShell.ColumnDefinitions[1].Width = new GridLength(0, GridUnitType.Pixel);
            AuthenticatedShell.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            RightPanel.IsVisible = true;
        }
        else
        {
            AuthenticatedShell.ColumnDefinitions[0].Width = new GridLength(sidebarWidth, GridUnitType.Pixel);
            AuthenticatedShell.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            RightPanel.IsVisible = true;
            UpdateResponsiveLayout();
        }
    }

    private void LoadLocalHtml()
    {
        try
        {
            var basePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
            var html = File.ReadAllText(Path.Combine(basePath, "index.html"));
            var css = File.ReadAllText(Path.Combine(basePath, "styles.css"));
            var js = File.ReadAllText(Path.Combine(basePath, "app.js"));
            html = html.Replace("/*__INLINE_CSS__*/", css, StringComparison.Ordinal)
                       .Replace("/*__INLINE_JS__*/", js, StringComparison.Ordinal);
            _webView?.NavigateToString(html);
        }
        catch (Exception ex)
        {
            _viewModel?.Messages.Add(new MessageViewModel("Arayüz", "Orb görseli yüklenemedi: " + ex.Message));
        }
    }

    private async void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            await SendToWebAsync(new { type = "host-error", value = "Orb WebView yüklenemedi." });
            return;
        }
        await SendToWebAsync(new { type = "host-ready", value = "Vortex.Desktop" });
        await SyncVisualStateAsync();
    }

    private async void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(e.Body)) return;
            var message = JsonSerializer.Deserialize<BridgeMessage>(e.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (message is null || !AllowedMessages.Contains(message.Type))
            {
                await SendToWebAsync(new { type = "host-error", value = "Bu HTML komutuna izin verilmiyor." });
                return;
            }
            await HandleBridgeMessageAsync(message.Type, message.Value);
        }
        catch (Exception ex)
        {
            await SendToWebAsync(new { type = "host-error", value = ex.Message });
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != WindowStateProperty || !_hasOpened || _isClosing || _switchingToCompact) return;
        if (WindowState == WindowState.Minimized && _viewModel?.IsAuthenticated == true) Dispatcher.UIThread.Post(ShowCompactMode, DispatcherPriority.Send);
    }

    private async Task HandleBridgeMessageAsync(string type, string? value)
    {
        if (_viewModel is null) return;
        switch (type)
        {
            case "start-recording":
                await _viewModel.StartRecordingFromBridgeAsync();
                break;
            case "stop-recording":
                await _viewModel.StopRecordingFromBridgeAsync();
                break;
            case "send-command":
                await _viewModel.SendAsync();
                break;
            case "stop-response":
                _viewModel.StopCurrentOperation();
                break;
            case "toggle-voice-reply":
                _viewModel.IsVoiceReplyEnabled = string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
                break;
            case "compact-mode":
                ShowCompactMode();
                break;
            case "restore-main":
                RestoreMainWindow();
                break;
        }
        await SyncVisualStateAsync();
    }

    private async Task SyncVisualStateAsync()
    {
        if (_viewModel is null) return;
        await SendToWebAsync(new { type = "state", value = _viewModel.OrbState });
        await SendToWebAsync(new { type = "audio-level", value = _viewModel.InputAudioLevel });
        await SendToWebAsync(new { type = "provider-mode", value = _viewModel.ProviderMode });
        await SendToWebAsync(new { type = "voice-reply", value = _viewModel.IsVoiceReplyEnabled });
        if (_compactWindow is not null)
        {
            await _compactWindow.SetStateAsync(_viewModel.OrbState);
            await _compactWindow.SetAudioLevelAsync(_viewModel.InputAudioLevel);
            await _compactWindow.SetProviderModeAsync(_viewModel.ProviderMode);
            await _compactWindow.SetVoiceReplyAsync(_viewModel.IsVoiceReplyEnabled);
        }
    }

    private async Task SendToWebAsync(object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload);
            var quoted = JsonSerializer.Serialize(json);
            if (_webView is not null) await _webView.InvokeScript($"window.vortexHost && window.vortexHost.receive(JSON.parse({quoted}))");
        }
        catch
        {
        }
    }

    private void EnsureCompactWindow()
    {
        if (_compactWindow is not null) return;
        var scale = _viewModel?.CompactUiScale ?? 1.0;
        _compactWindow = new CompactVoiceWindow(scale);
        _compactWindow.RestoreRequested += (_, _) => RestoreMainWindow();
        _compactWindow.CommandRequested += async (_, e) =>
        {
            if (!AllowedMessages.Contains(e.Type))
            {
                await _compactWindow.ShowToastAsync("Bu compact komutuna izin verilmiyor.");
                return;
            }
            await HandleBridgeMessageAsync(e.Type, e.Value);
        };
        _compactWindow.Closed += (_, _) =>
        {
            if (!_isClosing) _compactWindow = null;
        };
    }

    private void ShowCompactMode()
    {
        if (_isClosing || _switchingToCompact || _viewModel?.IsAuthenticated != true) return;
        _switchingToCompact = true;
        try
        {
            EnsureCompactWindow();
            _windowStateBeforeCompact = WindowState;
            _wasHiddenForCompact = true;
            WindowState = WindowState.Normal;
            Hide();
            _compactWindow!.ShowCompact();
            _ = SyncVisualStateAsync();
        }
        finally
        {
            _switchingToCompact = false;
        }
    }

    private void RestoreMainWindow()
    {
        if (_isClosing) return;
        _compactWindow?.HideCompact();
        var restoreWindowState = _wasHiddenForCompact ? _windowStateBeforeCompact : WindowState;
        if (restoreWindowState == WindowState.Minimized)
        {
            restoreWindowState = WindowState.Normal;
        }
        _wasHiddenForCompact = false;
        Show();
        WindowState = restoreWindowState;
        Activate();
    }

    private void CompactButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ShowCompactMode();
}



