using System.Globalization;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PocketCake.ViewModels;

namespace PocketCake.Views;

/// <summary>The pedal's global settings, plus app options.</summary>
public sealed partial class SettingsView : Page
{
    public SettingsView()
    {
        NavigationCacheMode = NavigationCacheMode.Required;
        ViewModel = MainWindow.Instance.Settings;
        InitializeComponent();
        Loaded += (_, _) => _ = MainWindow.Instance.State.RefreshGlobalSettingsAsync();
    }

    public SettingsViewModel ViewModel { get; }

    public string Version { get; } = $"PocketCake {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";

    public static string Db(double value) => $"{value.ToString("+0;-0;0", CultureInfo.InvariantCulture)} dB";

    private async void OnFactoryResetClick(object sender, RoutedEventArgs e)
    {
        if (await Dialogs.ConfirmAsync(XamlRoot, "Reset to factory settings?",
                "This restores all presets and settings to factory defaults. The pedal will reboot and disconnect.", "Reset"))
        {
            await ViewModel.FactoryResetAsync();
        }
    }
}
