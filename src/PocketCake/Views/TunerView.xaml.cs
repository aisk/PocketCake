using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using PocketCake.ViewModels;

namespace PocketCake.Views;

/// <summary>Chromatic tuner fed by the pedal's tuner pushes.</summary>
public sealed partial class TunerView : Page
{
    private static readonly SolidColorBrush InTuneBrush = new(ColorHelper.FromArgb(0xFF, 0x30, 0xA4, 0x6C));

    public TunerView()
    {
        NavigationCacheMode = NavigationCacheMode.Required;
        ViewModel = MainWindow.Instance.Tuner;
        InitializeComponent();
    }

    public TunerViewModel ViewModel { get; }

    public static Brush NoteBrush(bool inTune) =>
        inTune ? InTuneBrush : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await ViewModel.ActivateAsync();

    protected override async void OnNavigatedFrom(NavigationEventArgs e) => await ViewModel.DeactivateAsync();
}
