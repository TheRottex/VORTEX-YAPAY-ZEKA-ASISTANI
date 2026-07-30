using System.Text.Json;
using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Interactivity;

namespace Vortex.Desktop;

public sealed class CompactCommandEventArgs(string type, string? value) : EventArgs
{
    public string Type { get; } = type;
    public string? Value { get; } = value;
}

public sealed class CompactVoiceWindow : Window
{
    private readonly NativeWebView _orbWebView = new();
    private readonly CompactWaveformView _waveform = new();
    private readonly TextBlock _statusText = new();
    private readonly TextBlock _providerText = new();
    private readonly Button _microphoneButton = new();
    private readonly Button _voiceReplyButton = new();
    private readonly Grid _layoutGrid = new();
    private bool _orbWebReady;
    private bool _isClosing;
    private bool _isRecording;
    private bool _voiceReplyEnabled;
    private string _currentState = "idle";
    private double _currentLevel = 0.04;
    private bool _hasUserPosition;
    private bool _dragging;
    private bool _dragMoved;
    private PixelPoint _dragStartPointer;
    private PixelPoint _dragStartWindow;
    private double _scale;

    public event EventHandler? RestoreRequested;
    public event EventHandler<CompactCommandEventArgs>? CommandRequested;

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

    public CompactVoiceWindow(double scale)
    {
        _scale = Math.Clamp(scale, 0.85, 1.35);
        Title = "Vortex Compact Voice";
        CanResize = false;
        Topmost = true;
        ShowInTaskbar = false;
        WindowDecorations = WindowDecorations.None;
        Background = new SolidColorBrush(Color.Parse("#0B1727"));
        ExtendClientAreaToDecorationsHint = false;

        _orbWebView.EnvironmentRequested += ConfigureWebViewEnvironment;
        _orbWebView.NavigationCompleted += async (_, e) =>
        {
            _orbWebReady = e.IsSuccess;
            if (_orbWebReady)
            {
                await SendOrbMessageAsync(new { type = "state", value = _currentState });
                await SendOrbMessageAsync(new { type = "audio-level", value = _currentLevel });
            }
        };

        Content = BuildIsland();
        ApplyScale(_scale);

        Opened += (_, _) =>
        {
            if (!_hasUserPosition) PositionAtBottomCenter();
            LoadCompactOrbHtml();
        };
        Closing += (_, _) => _isClosing = true;
    }

    public void ApplyScale(double scale)
    {
        _scale = Math.Clamp(scale, 0.85, 1.35);
        Width = 420 * _scale;
        Height = 68 * _scale; // Slim flat modern layout height
        MinWidth = MaxWidth = Width;
        MinHeight = MaxHeight = Height;
        var identityWidth = (int)Math.Round(134 * _scale);
        var buttonWidth = (int)Math.Round(40 * _scale);
        _layoutGrid.ColumnDefinitions = new ColumnDefinitions($"{identityWidth},*,{buttonWidth},{buttonWidth}");
        _layoutGrid.ColumnSpacing = (int)Math.Round(6 * _scale);
        _orbWebView.Width = (int)Math.Round(40 * _scale);
        _orbWebView.Height = (int)Math.Round(40 * _scale);
        _waveform.Height = (int)Math.Round(40 * _scale);
        _microphoneButton.Width = _microphoneButton.Height = (int)Math.Round(32 * _scale);
        _voiceReplyButton.Width = _voiceReplyButton.Height = (int)Math.Round(32 * _scale);
        _statusText.FontSize = (int)Math.Round(11 * _scale);
        _providerText.FontSize = (int)Math.Round(9 * _scale);
    }

    public void ShowCompact()
    {
        if (_isClosing) return;
        if (!IsVisible) Show();
        if (!_hasUserPosition) PositionAtBottomCenter();
        Activate();
    }

    public void HideCompact()
    {
        if (!_isClosing && IsVisible) Hide();
    }

    public void PrepareForAppShutdown() => _isClosing = true;

    public Task SetStateAsync(string state)
    {
        _currentState = state;
        _waveform.State = state;
        _ = SendOrbMessageAsync(new { type = "state", value = state });
        _statusText.Text = state switch
        {
            "recording" => "Dinliyor",
            "transcribing" => "Yazıya aktarılıyor",
            "processing" => "İşleniyor",
            "speaking" => "Yanıt veriyor",
            "error" => "Hata",
            "offline" => "Çevrimdışı",
            _ => "Hazır"
        };
        _isRecording = state == "recording";
        UpdateMicrophoneButton();
        return Task.CompletedTask;
    }

    public Task SetProviderModeAsync(string providerMode)
    {
        _providerText.Text = providerMode switch
        {
            "local" => "Yerel mod",
            "offline" => "Bağlantı yok",
            _ => "Bulut sağlayıcı"
        };
        return Task.CompletedTask;
    }

    public Task SetAudioLevelAsync(double level)
    {
        _currentLevel = level;
        _waveform.Level = level;
        _ = SendOrbMessageAsync(new { type = "audio-level", value = level });
        return Task.CompletedTask;
    }

    public Task SetVoiceReplyAsync(bool enabled)
    {
        _voiceReplyEnabled = enabled;
        UpdateVoiceReplyButton();
        return Task.CompletedTask;
    }

    public Task ShowToastAsync(string text)
    {
        _statusText.Text = text;
        return Task.CompletedTask;
    }

    private Control BuildIsland()
    {
        var root = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.Parse("#1c2c4c")),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#F20B1727"), 0),
                    new GradientStop(Color.Parse("#F2081220"), 0.55),
                    new GradientStop(Color.Parse("#F2112133"), 1)
                }
            },
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 22, OffsetY = 7, Color = Color.Parse("#80000000") }),
            Padding = new Thickness(8),
            Child = BuildContent()
        };
        root.PointerPressed += OnDragSurfacePointerPressed;
        root.PointerMoved += OnDragSurfacePointerMoved;
        root.PointerReleased += OnDragSurfacePointerReleased;
        root.PointerCaptureLost += (_, _) => ResetDrag();
        root.DoubleTapped += OnRestoreDoubleTapped;
        root.PointerEntered += (s, e) => root.Opacity = 1.0;
        root.PointerExited += (s, e) => root.Opacity = 0.75;
        root.Opacity = 0.75;
        return root;
    }

    private Control BuildContent()
    {
        _layoutGrid.Background = Brushes.Transparent;

        var identity = new Grid { ColumnDefinitions = new ColumnDefinitions("44,*"), Background = Brushes.Transparent };
        _orbWebView.Margin = new Thickness(0, 0, 4, 0);
        _orbWebView.HorizontalAlignment = HorizontalAlignment.Center;
        _orbWebView.VerticalAlignment = VerticalAlignment.Center;
        identity.Children.Add(_orbWebView);

        var labels = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 1,
            Children =
            {
                new TextBlock { Text = "VORTEX", FontSize = 12, FontWeight = FontWeight.Bold, Foreground = Brushes.White },
                _statusText,
                _providerText
            }
        };
        _statusText.Text = "Hazır";
        _statusText.Foreground = new SolidColorBrush(Color.Parse("#18C8FF"));
        _providerText.Text = "Bulut sağlayıcı";
        _providerText.Foreground = new SolidColorBrush(Color.Parse("#819DB0"));
        Grid.SetColumn(labels, 1);
        identity.Children.Add(labels);
        Grid.SetColumn(identity, 0);
        _layoutGrid.Children.Add(identity);

        var wavePanel = new Grid { Background = Brushes.Transparent };
        wavePanel.Children.Add(_waveform);
        Grid.SetColumn(wavePanel, 1);
        _layoutGrid.Children.Add(wavePanel);

        // Vector Speaker Mute/Unmute Button
        _voiceReplyButton.CornerRadius = new CornerRadius(6);
        _voiceReplyButton.Padding = new Thickness(0);
        _voiceReplyButton.Background = new SolidColorBrush(Color.Parse("#182B3C"));
        _voiceReplyButton.BorderThickness = new Thickness(1);
        _voiceReplyButton.BorderBrush = new SolidColorBrush(Color.Parse("#1c2c4c"));
        _voiceReplyButton.Click += (_, _) =>
        {
            _voiceReplyEnabled = !_voiceReplyEnabled;
            UpdateVoiceReplyButton();
            CommandRequested?.Invoke(this, new CompactCommandEventArgs("toggle-voice-reply", _voiceReplyEnabled ? "on" : "off"));
        };
        UpdateVoiceReplyButton();
        Grid.SetColumn(_voiceReplyButton, 2);
        _layoutGrid.Children.Add(_voiceReplyButton);

        // Vector Microphone Button
        _microphoneButton.Padding = new Thickness(0);
        _microphoneButton.Click += (_, _) => CommandRequested?.Invoke(this, new CompactCommandEventArgs(_isRecording ? "stop-recording" : "start-recording", null));
        UpdateMicrophoneButton();
        Grid.SetColumn(_microphoneButton, 3);
        _layoutGrid.Children.Add(_microphoneButton);

        return _layoutGrid;
    }

    private static Button PathButton(string pathData, string color, string borderCol) => new()
    {
        Padding = new Thickness(0),
        Background = new SolidColorBrush(Color.Parse(color)),
        BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(Color.Parse(borderCol)),
        CornerRadius = new CornerRadius(6),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Content = new PathIcon
        {
            Data = Geometry.Parse(pathData),
            Width = 14,
            Height = 14,
            Foreground = Brushes.White
        }
    };

    private void UpdateVoiceReplyButton()
    {
        var path = _voiceReplyEnabled
            ? "M3 9v6h4l5 5V4L7 9H3zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02z"
            : "M 4.34 2.93 L 2.93 4.34 L 7.29 8.7 H 3 v 6 h 4 l 5 5 v -6.73 l 4.25 4.25 c -0.67 0.52 -1.42 0.93 -2.25 1.18 v 2.06 c 1.38 -0.31 2.63 -0.95 3.69 -1.81 l 2.42 2.42 l 1.41 -1.41 L 4.34 2.93 z M 12 4 L 9.91 6.09 L 12 8.18 V 4 z m 4 8 c 0 -1.77 -1.02 -3.29 -2.5 -4.03 v 2.21 l 2.45 2.45 c 0.03 -0.21 0.05 -0.42 0.05 -0.63 z m 2.5 0 c 0 .94 -0.2 1.82 -0.54 2.64 l 1.51 1.51 C 19.82 15.34 20 13.7 20 12 c 0 -4.28 -2.99 -7.86 -7 -8.77 v 2.06 c 2.89 .86 5 3.54 5 6.71 z";
        _voiceReplyButton.Content = new PathIcon
        {
            Data = Geometry.Parse(path),
            Width = 14,
            Height = 14,
            Foreground = Brushes.White
        };
        _voiceReplyButton.BorderBrush = new SolidColorBrush(Color.Parse(_voiceReplyEnabled ? "#1d4ed8" : "#1c2c4c"));
    }

    private void UpdateMicrophoneButton()
    {
        var path = "M 12 14 c 1.66 0 3 -1.34 3 -3 V 5 c 0 -1.66 -1.34 -3 -3 -3 S 9 3.34 9 5 v 6 c 0 1.66 1.34 3 3 3 z m 5.3 -3 c 0 3 -2.54 5.1 -5.3 5.1 S 6.7 14 6.7 11 H 5 c 0 3.41 2.72 6.23 6 6.72 V 21 h 2 v -3.28 c 3.28 -0.48 6 -3.3 6 -6.72 h -1.7 z";
        _microphoneButton.Content = new PathIcon
        {
            Data = Geometry.Parse(path),
            Width = 14,
            Height = 14,
            Foreground = Brushes.White
        };
        _microphoneButton.Background = new SolidColorBrush(Color.Parse(_isRecording ? "#8C2926" : "#075F91"));
        _microphoneButton.Foreground = Brushes.White;
        _microphoneButton.BorderThickness = new Thickness(1);
        _microphoneButton.BorderBrush = new SolidColorBrush(Color.Parse(_isRecording ? "#FF756B" : "#62D8FF"));
        _microphoneButton.CornerRadius = new CornerRadius(6);
    }

    private PixelPoint ToScreenPixel(Point localPoint)
    {
        var scale = RenderScaling > 0 ? RenderScaling : 1.0;
        return new PixelPoint(Position.X + (int)Math.Round(localPoint.X * scale), Position.Y + (int)Math.Round(localPoint.Y * scale));
    }

    private void OnDragSurfacePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || IsInsideButton(e.Source)) return;
        _dragging = true;
        _dragMoved = false;
        _dragStartPointer = ToScreenPixel(e.GetPosition(this));
        _dragStartWindow = Position;
        e.Pointer.Capture((IInputElement?)Content);
        e.Handled = true;
    }

    private void OnDragSurfacePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;
        var now = ToScreenPixel(e.GetPosition(this));
        var dx = now.X - _dragStartPointer.X;
        var dy = now.Y - _dragStartPointer.Y;
        if (Math.Abs(dx) > 3 || Math.Abs(dy) > 3) _dragMoved = true;
        if (_dragMoved)
        {
            Position = new PixelPoint(_dragStartWindow.X + dx, _dragStartWindow.Y + dy);
            _hasUserPosition = true;
        }
        e.Handled = true;
    }

    private void OnDragSurfacePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        e.Pointer.Capture(null);
        ResetDrag();
        e.Handled = true;
    }

    private void ResetDrag()
    {
        _dragging = false;
        _dragMoved = false;
    }

    private static bool IsInsideButton(object? source)
    {
        var current = source as StyledElement;
        while (current is not null)
        {
            if (current is Button || current is PathIcon) return true;
            current = current.Parent;
        }
        return false;
    }

    private void OnRestoreDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (!IsInsideButton(e.Source)) RestoreRequested?.Invoke(this, EventArgs.Empty);
    }

    private void PositionAtBottomCenter()
    {
        var lifetime = Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        var owner = lifetime?.MainWindow;
        var screen = Screens.ScreenFromWindow(owner ?? this) ?? Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        var x = area.X + Math.Max(0, (area.Width - (int)Width) / 2);
        var y = area.Y + Math.Max(0, area.Height - (int)Height - 30);
        Position = new PixelPoint(x, y);
    }

    private void LoadCompactOrbHtml()
    {
        try
        {
            var basePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
            var html = File.ReadAllText(Path.Combine(basePath, "compact-orb.html"));
            var css = File.ReadAllText(Path.Combine(basePath, "compact-orb.css"));
            var js = File.ReadAllText(Path.Combine(basePath, "compact-orb.js"));
            html = html.Replace("/*__INLINE_CSS__*/", css, StringComparison.Ordinal)
                       .Replace("/*__INLINE_JS__*/", js, StringComparison.Ordinal);
            _orbWebView.NavigateToString(html);
        }
        catch
        {
        }
    }

    private async Task SendOrbMessageAsync(object payload)
    {
        if (!_orbWebReady) return;
        try
        {
            var json = JsonSerializer.Serialize(payload);
            var quoted = JsonSerializer.Serialize(json);
            await _orbWebView.InvokeScript($"window.vortexCompactHost && window.vortexCompactHost.receive(JSON.parse({quoted}))");
        }
        catch
        {
        }
    }
}

public sealed class CompactWaveformView : Control
{
    private readonly DispatcherTimer _timer;
    private double _phase;
    private double _level = 0.04;
    private string _state = "idle";

    public double Level
    {
        get => _level;
        set { _level = Math.Clamp(value, 0, 1); InvalidateVisual(); }
    }

    public string State
    {
        get => _state;
        set { _state = value; InvalidateVisual(); }
    }

    public CompactWaveformView()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => { _phase += 0.08; InvalidateVisual(); };
        _timer.Start();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var mid = Bounds.Height / 2;
        var count = Math.Max(18, (int)(Bounds.Width / 7));
        var spacing = Bounds.Width / count;
        var active = State is "recording" or "speaking";
        var processing = State is "processing" or "transcribing";

        for (var i = 0; i < count; i++)
        {
            var normalized = i / (double)Math.Max(1, count - 1);
            var envelope = Math.Sin(normalized * Math.PI);
            double height;

            if (active)
                height = 8 + envelope * (10 + Level * 43) * (0.45 + 0.55 * Math.Abs(Math.Sin(_phase * 2.2 + i * 0.72)));
            else if (processing)
                height = 8 + envelope * (12 + 7 * Math.Abs(Math.Sin(_phase + i * 0.42)));
            else
                height = 5 + envelope * (6 + 4 * Math.Abs(Math.Sin(_phase * 0.7 + i * 0.5)));

            var x = i * spacing + spacing / 2;
            var alpha = (byte)(120 + envelope * 120);
            var color = Color.FromArgb(alpha, 24, 190, 255);
            context.DrawLine(new Pen(new SolidColorBrush(color), 2), new Point(x, mid - height / 2), new Point(x, mid + height / 2));
        }
    }
}
