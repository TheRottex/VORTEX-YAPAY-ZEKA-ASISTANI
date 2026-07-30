using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Vortex.Desktop.Services;
using Vortex.Desktop.ViewModels;

namespace Vortex.Desktop;

public sealed partial class App : Application
{
    private static void StartupTrace(string message)
    {
        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(root, "VortexAI", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "desktop-startup.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            Debug.WriteLine($"VortexDesktopStartup: {message}");
        }
        catch
        {
        }
    }

    public override void Initialize()
    {
        StartupTrace("Initialize:before-xaml");
        AvaloniaXamlLoader.Load(this);
        StartupTrace("Initialize:after-xaml");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        StartupTrace("FrameworkInit:enter");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            StartupTrace("FrameworkInit:desktop-lifetime");
            var tokenStorage = new TokenStorageService();
            var settings = new DesktopSettingsService();
            var loaded = new DesktopSettings();
            StartupTrace("Settings:startup-defaults");
            ApplyThemePreference(loaded.ThemePreference);
            var backend = new BackendClient(new HttpClient { BaseAddress = DesktopProductionEndpoints.ServerBaseUri }, tokenStorage, settings);
            var auth = new DesktopAuthenticationService(backend, DesktopProductionEndpoints.WebBaseUri);
            StartupTrace("MainWindow:create-before");
            var mainWindow = new MainWindow();
            StartupTrace("MainWindow:create-after");
            mainWindow.DataContext = new MainWindowViewModel(backend, auth, settings);
            StartupTrace("MainWindow:datacontext-set");
            desktop.MainWindow = mainWindow;
            StartupTrace("MainWindow:assigned");
            base.OnFrameworkInitializationCompleted();
            StartupTrace("MainWindow:show-before");
            mainWindow.Show();
            StartupTrace($"MainWindow:show-after visible={mainWindow.IsVisible}");
            mainWindow.WindowState = WindowState.Normal;
            mainWindow.Activate();
            StartupTrace("MainWindow:activated");
            return;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Applies Avalonia Fluent theme variant. System/Default follows the OS light/dark preference.
    /// Custom Vortex panel hex colors remain fixed dark palette unless rethemed separately.
    /// </summary>
    public static void ApplyThemePreference(string? preference)
    {
        var app = Current;
        if (app is null) return;
        var p = (preference ?? "System").Trim();
        app.RequestedThemeVariant = p.Equals("Light", StringComparison.OrdinalIgnoreCase)
            ? ThemeVariant.Light
            : p.Equals("Dark", StringComparison.OrdinalIgnoreCase)
                ? ThemeVariant.Dark
                : ThemeVariant.Default; // System / OS
    }
}


