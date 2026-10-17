using PocketCake.Core.Protocol;

namespace PocketCake.ViewModels;

/// <summary>The tuner page: holds the pedal in tuner mode while the page is shown.</summary>
public sealed class TunerViewModel : ObservableObject
{
    /// <summary>Meter width per cent; the meter spans ±50 cents.</summary>
    public const double PixelsPerCent = 4;

    private readonly AppState _state;
    private bool _isActive;

    public TunerViewModel(AppState state)
    {
        _state = state;
        state.PropertyChanged += async (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(AppState.Tuner) or nameof(AppState.IsTunerOn):
                    OnPropertyChanged("");
                    break;

                case nameof(AppState.IsConnected):
                    OnPropertyChanged("");

                    if (_isActive && _state.IsConnected)
                    {
                        await _state.StartTunerAsync();
                    }

                    break;
            }
        };
    }

    private TunerReading? Reading => _state.IsTunerOn ? _state.Tuner : null;

    public bool ShowDisconnected => !_state.IsConnected;

    public string Note => Reading?.Note ?? "—";

    public string CentsText => Reading is { Note: not null } r ? $"{r.Cents:+0;-0;0} ¢" : "Play a string";

    public bool HasSignal => Reading is { Note: not null };

    public bool InTune => Reading?.InTune == true;

    public double NeedleOffset => Reading is { Note: not null } r ? r.Cents * PixelsPerCent : 0;

    /// <summary>The page appeared: enter tuner mode.</summary>
    public async Task ActivateAsync()
    {
        _isActive = true;

        if (_state.IsConnected)
        {
            await _state.StartTunerAsync();
        }
    }

    /// <summary>The page went away: leave tuner mode.</summary>
    public async Task DeactivateAsync()
    {
        _isActive = false;
        await _state.StopTunerAsync();
    }
}
