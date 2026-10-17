using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PocketCake.ViewModels;

namespace PocketCake.Views;

/// <summary>Load, rename and clear the pedal's User-IR and User-Profile slots.</summary>
public sealed partial class SlotsView : UserControl
{
    public SlotsView()
    {
        ViewModel = MainWindow.Instance.Slots;
        InitializeComponent();
    }

    public SlotsViewModel ViewModel { get; }

    private static SlotItem Slot(object sender) => (SlotItem)((FrameworkElement)sender).DataContext;

    private async void OnLoadClick(object sender, RoutedEventArgs e) =>
        await Dialogs.LoadSlotAsync(Slot(sender).Index, Slot(sender).IsClone);

    private async void OnRenameClick(object sender, RoutedEventArgs e) =>
        await Dialogs.RenameSlotAsync(Slot(sender).Index, Slot(sender).IsClone);

    private async void OnClearClick(object sender, RoutedEventArgs e) =>
        await Dialogs.ClearSlotAsync(Slot(sender).Index, Slot(sender).IsClone);
}
