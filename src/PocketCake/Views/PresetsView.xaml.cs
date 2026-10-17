using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PocketCake.Core.Model;
using PocketCake.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace PocketCake.Views;

/// <summary>Picks the parameter card for a <see cref="ParameterItem"/>'s kind.</summary>
public sealed partial class ParameterTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Slider { get; set; }

    public DataTemplate? Toggle { get; set; }

    public DataTemplate? Choice { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => (item as ParameterItem)?.Kind switch
    {
        ParameterKind.Toggle => Toggle,
        ParameterKind.Choice => Choice,
        _ => Slider,
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

/// <summary>Browse and load presets, then edit the loaded one: fields, signal chain, modules.</summary>
public sealed partial class PresetsView : Page
{
    public PresetsView()
    {
        NavigationCacheMode = NavigationCacheMode.Required;
        ViewModel = MainWindow.Instance.Presets;
        InitializeComponent();

        SyncBank();
        State.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.PresetTab))
            {
                SyncBank();
            }
        };
    }

    public PresetsViewModel ViewModel { get; }

    private AppState State => ViewModel.State;

    public static Visibility Not(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static double BeatOpacity(bool isBeat) => isBeat ? 1 : 0.25;

    // -------------------------------------------------------------------
    // Preset list
    // -------------------------------------------------------------------

    private void SyncBank() => BankBar.SelectedItem = State.PresetTab == PresetBank.User ? UserBankItem : FactoryBankItem;

    private void OnBankChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) =>
        State.PresetTab = sender.SelectedItem == FactoryBankItem ? PresetBank.Factory : PresetBank.User;

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => State.SearchQuery = sender.Text;

    private async void OnPresetClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PresetItem item)
        {
            return;
        }

        if (!State.IsModified || await Dialogs.ConfirmAsync(
                XamlRoot,
                "Discard unsaved changes?",
                "The current preset has edits that are not saved to a slot. Loading a preset will discard them.",
                "Discard"))
        {
            Dialogs.ReportIfBusy(await State.LoadPresetAsync(item.Preset));
        }

        // The list selects on click; put it back on the preset actually loaded.
        PresetList.SelectedItem = ViewModel.SelectedPreset;
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e) => await MainWindow.Instance.ConnectAsync();

    private async void OnDemoClick(object sender, RoutedEventArgs e) => await State.StartDemoAsync();

    // -------------------------------------------------------------------
    // Preset actions
    // -------------------------------------------------------------------

    private async void OnSaveClick(object sender, RoutedEventArgs e) => await Dialogs.ShowSavePresetAsync(XamlRoot, State);

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        if (await MainWindow.Instance.PickFileAsync(".json") is not { } path)
        {
            return;
        }

        try
        {
            Dialogs.ReportIfBusy(await State.ImportPresetAsync(await File.ReadAllTextAsync(path)));
        }
        catch (Exception ex)
        {
            State.ReportStatus($"import failed: {ex.Message}");
        }
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (State.ExportPresetJson() is not { } json)
        {
            return;
        }

        var name = string.Join("_", State.CurrentPresetName.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        if (await MainWindow.Instance.PickSaveFileAsync(name, "Preset JSON", ".json") is not { } path)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, json);
            State.ReportStatus($"exported to {path}");
        }
        catch (Exception ex)
        {
            State.ReportStatus($"export failed: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------
    // Signal chain
    // -------------------------------------------------------------------

    private void OnChainDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        // The DRV-AMP-IR-EQ block is fixed; only the free modules move around it.
        if (State.IsLoading || e.Items.Any(i => i is ChainGroup { IsAmpBlock: true }))
        {
            e.Cancel = true;
        }
    }

    private async void OnChainDragCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (args.DropResult == DataPackageOperation.Move)
        {
            await ViewModel.CommitChainOrderAsync();
        }
    }

    // -------------------------------------------------------------------
    // Module editor
    // -------------------------------------------------------------------

    private void OnTapClick(object sender, RoutedEventArgs e) => ViewModel.Editor.TapTempo?.Tap();

    private async void OnSlotLoadClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Editor.Slot is { } slot)
        {
            await Dialogs.LoadSlotAsync(slot.Index, slot.IsClone);
        }
    }

    private async void OnSlotRenameClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Editor.Slot is { } slot)
        {
            await Dialogs.RenameSlotAsync(slot.Index, slot.IsClone);
        }
    }

    private async void OnSlotClearClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Editor.Slot is { } slot)
        {
            await Dialogs.ClearSlotAsync(slot.Index, slot.IsClone);
        }
    }
}
