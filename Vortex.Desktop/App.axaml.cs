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
            var settings = new DesktopSettingsService();

            void StartMainWindow(DesktopSettings effectiveSettings)
            {
                var serverBaseUri = effectiveSettings.ResolveServerBaseUri();
                var webBaseUri = effectiveSettings.ResolveWebBaseUri();
                var tokenStorage = new TokenStorageService();
                var handler = DesktopHttpHandlerFactory.Create(effectiveSettings);
                var backend = new BackendClient(new HttpClient(handler) { BaseAddress = serverBaseUri }, tokenStorage, settings);
                StartupTrace("Backend:configured");
                var auth = new DesktopAuthenticationService(backend, webBaseUri);
                StartupTrace("MainWindow:create-before");
                var mainWindow = new MainWindow();
                mainWindow.DataContext = new MainWindowViewModel(backend, auth, settings);
                desktop.MainWindow = mainWindow;
                mainWindow.Show();
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
                desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
                StartupTrace($"MainWindow:show-after visible={mainWindow.IsVisible}");
            }

            void PostStartup(DesktopSettings loaded)
            {
                StartupTrace("Startup:ui-dispatch-posted");
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        StartupTrace("Startup:ui-dispatch-running");
                        ApplyThemePreference(loaded.ThemePreference);
                        if (loaded.SetupWizardVersion < SetupWizardViewModel.CurrentVersion)
                        {
                            StartupTrace("Startup:branch=wizard");
                            var wizard = CreateSetupWizard(settings, loaded, isRerun: false);
                            if (wizard.DataContext is SetupWizardViewModel viewModel)
                            {
                                viewModel.Finished += (_, effectiveSettings) =>
                                {
                                    Dispatcher.UIThread.Post(() =>
                                    {
                                        try
                                        {
                                            wizard.Close();
                                            StartMainWindow(effectiveSettings);
                                        }
                                        catch (Exception ex)
                                        {
                                            StartupTrace($"Startup:wizard-finish-failed type={ex.GetType().Name}");
                                            desktop.Shutdown();
                                        }
                                    });
                                };
                            }
                            wizard.Show();
                            return;
                        }

                        StartupTrace("Startup:branch=main");
                        StartMainWindow(loaded);
                    }
                    catch (Exception ex)
                    {
                        StartupTrace($"Startup:ui-failed type={ex.GetType().Name}");
                        desktop.Shutdown();
                    }
                });
            }

            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnFrameworkInitializationCompleted();
            _ = Task.Run(async () =>
            {
                try
                {
                    StartupTrace("Settings:startup-load-begin");
                    var loaded = await settings.LoadAsync(CancellationToken.None).ConfigureAwait(false);
                    StartupTrace("Settings:startup-loaded");
                    PostStartup(loaded);
                }
                catch (Exception ex)
                {
                    StartupTrace($"Settings:startup-failed type={ex.GetType().Name}");
                    PostStartup(new DesktopSettings());
                }
            });
            return;
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static SetupWizardWindow CreateSetupWizard(DesktopSettingsService settings, DesktopSettings loaded, bool isRerun)
    {
        var voiceInput = new VoiceInputService();
        var readiness = new DesktopSetupReadinessService(voiceInput);
        var localAgentRuntime = new LocalAgentRuntimeService(settings, new LocalAgentClient());
        async Task<DesktopNetworkDiagnosticsReport> RunDiagnostics(DesktopSettings effectiveSettings, CancellationToken cancellationToken)
        {
            using var handler = DesktopHttpHandlerFactory.Create(effectiveSettings);
            using var client = new HttpClient(handler);
            var diagnostics = new DesktopNetworkDiagnosticsService(client);
            return await diagnostics.RunAsync(effectiveSettings.ResolveServerBaseUri(), effectiveSettings, cancellationToken);
        }

        return new SetupWizardWindow
        {
            DataContext = new SetupWizardViewModel(settings, loaded, readiness, RunDiagnostics, localAgentRuntime.EnsureReadyAsync, isRerun)
        };
    }

    public static void ApplyThemePreference(string? preference)
    {
        if (Current is not null) Current.RequestedThemeVariant = ThemeVariant.Dark;
    }
}


