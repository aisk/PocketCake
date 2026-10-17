using PocketCake.Core.Model;

namespace PocketCake.ViewModels;

/// <summary>The pedal's five User-IR and five User-Profile (clone) slots.</summary>
public sealed class SlotsViewModel : ObservableObject
{
    private readonly AppState _state;

    public SlotsViewModel(AppState state)
    {
        _state = state;
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppState.UserNames) or nameof(AppState.IsConnected) or nameof(AppState.IsLoading))
            {
                Refresh();
            }
        };
        Refresh();
    }

    public IReadOnlyList<SlotItem> IrSlots { get; } = Enumerable.Range(0, ModuleIds.UserSlotCount).Select(i => new SlotItem(i, isClone: false)).ToList();

    public IReadOnlyList<SlotItem> CloneSlots { get; } = Enumerable.Range(0, ModuleIds.UserSlotCount).Select(i => new SlotItem(i, isClone: true)).ToList();

    public bool IsConnected => _state.IsConnected;

    public bool ShowDisconnected => !_state.IsConnected;

    private void Refresh()
    {
        foreach (var slot in IrSlots.Concat(CloneSlots))
        {
            slot.Name = _state.IsConnected && _state.UserNames.TryGetValue(ModuleIds.UserSlotBase(slot.IsClone) + slot.Index, out var name) ? name : "—";
            slot.IsEnabled = _state.IsConnected && !_state.IsLoading;
        }

        OnPropertyChanged("");
    }
}
