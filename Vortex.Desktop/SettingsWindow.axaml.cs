using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Vortex.Desktop;

public sealed partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
