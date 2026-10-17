using PocketCake.Core.Protocol;

namespace PocketCake.ViewModels;

public sealed partial class AppState
{
    private bool _isTunerOn;
    private TunerReading? _tuner;

    /// <summary>Whether the pedal is in tuner mode (it pushes <see cref="Tuner"/> readings).</summary>
    public bool IsTunerOn { get => _isTunerOn; private set => Set(ref _isTunerOn, value); }

    /// <summary>The latest tuner push, or null before the first one.</summary>
    public TunerReading? Tuner { get => _tuner; private set => Set(ref _tuner, value); }

    /// <summary>Puts the pedal into tuner mode. Not gated: it changes no stored state and other edits may follow.</summary>
    public async Task<OpResult> StartTunerAsync()
    {
        if (!IsConnected)
        {
            return OpResult.Fail("not connected");
        }

        if (IsTunerOn)
        {
            return OpResult.Ok;
        }

        try
        {
            Tuner = null;

            if (!await _service.SendAndAwaitAckAsync(TunerFrame.Enter))
            {
                return Fail("tuner: no ACK (timeout)");
            }

            IsTunerOn = true;
            return OpResult.Ok;
        }
        catch (Exception e)
        {
            return Fail($"tuner failed: {e.Message}");
        }
    }

    /// <summary>Leaves tuner mode; a no-op when it is off or the pedal is gone.</summary>
    public async Task StopTunerAsync()
    {
        if (!IsTunerOn)
        {
            return;
        }

        IsTunerOn = false;
        Tuner = null;

        if (!IsConnected)
        {
            return;
        }

        try
        {
            await _service.SendAndAwaitAckAsync(TunerFrame.Quit);
        }
        catch (Exception e)
        {
            Status = $"leaving the tuner failed: {e.Message}";
        }
    }

    private void HandleTunerReading(TunerReading reading)
    {
        if (IsTunerOn)
        {
            Tuner = reading;
        }
    }

    /// <summary>The pedal left tuner mode with the connection (it boots out of it).</summary>
    private void ResetTuner()
    {
        IsTunerOn = false;
        Tuner = null;
    }
}
