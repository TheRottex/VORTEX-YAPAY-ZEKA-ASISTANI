using Avalonia.Controls;
using Avalonia.Threading;
using Vortex.Desktop.ViewModels;

namespace Vortex.Desktop;

public sealed partial class SetupWizardWindow : Window
{
    private bool _finished;

    public SetupWizardWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachViewModel();
        Closed += (_, _) =>
        {
            if (!_finished && Owner is null && Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        };
    }

    private void AttachViewModel()
    {
        if (DataContext is SetupWizardViewModel viewModel)
        {
            viewModel.Finished += (_, _) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _finished = true;
                    Close();
                });
            };
        }
    }
}
