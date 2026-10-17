using Microsoft.UI.Dispatching;
using PocketCake.Core.Model;

namespace PocketCake.ViewModels;

/// <summary>
/// Tap tempo for the Delay module: taps or a typed BPM plus a note division set the delay Time
/// (ms, clamped to the parameter range). <see cref="IsBeat"/> pulses on the beat.
/// </summary>
public sealed class TapTempoModel : ObservableObject, IDisposable
{
    private readonly int _timeMin;
    private readonly int _timeMax;
    private readonly Action<int> _send;
    private readonly List<long> _taps = [];
    private readonly DispatcherQueueTimer _beat;
    private readonly DispatcherQueueTimer _beatOff;
    private readonly DispatcherQueueTimer _tapReset;

    private double _bpm = 120;
    private int _divisionIndex = 2; // 1/4
    private int? _lastSent;
    private int _currentMs = -1;
    private bool _isBeat;

    public TapTempoModel(int timeMin, int timeMax, Action<int> send)
    {
        _timeMin = timeMin;
        _timeMax = timeMax;
        _send = send;

        var dq = DispatcherQueue.GetForCurrentThread();
        _beatOff = dq.CreateTimer();
        _beatOff.IsRepeating = false;
        _beatOff.Tick += (_, _) => IsBeat = false;
        _beat = dq.CreateTimer();
        _beat.Tick += (_, _) =>
        {
            IsBeat = true;
            _beatOff.Start();
        };
        _tapReset = dq.CreateTimer();
        _tapReset.IsRepeating = false;
        _tapReset.Interval = TimeSpan.FromSeconds(2);
        _tapReset.Tick += (_, _) => _taps.Clear();
    }

    public IReadOnlyList<string> Divisions { get; } = TapTempo.NoteDivisions.Select(d => d.Label).ToList();

    public double Bpm
    {
        get => _bpm;
        set
        {
            var bpm = double.IsNaN(value) ? _bpm : TapTempo.ClampBpm(value);

            if (bpm == _bpm)
            {
                // Re-show the clamped value after an empty or out-of-range entry.
                OnPropertyChanged();
                return;
            }

            _bpm = bpm;
            RestartBeat();
            Send();
            OnPropertyChanged("");
        }
    }

    public int DivisionIndex
    {
        get => _divisionIndex;
        set
        {
            if (value < 0 || value == _divisionIndex)
            {
                return;
            }

            _divisionIndex = value;
            Send();
            OnPropertyChanged("");
        }
    }

    public string ResultText => $"{Math.Clamp(RawMs, _timeMin, _timeMax)} ms";

    /// <summary>The division at this tempo is longer than the delay can go (clamped).</summary>
    public bool IsOutOfRange => RawMs > _timeMax;

    public bool IsBeat { get => _isBeat; private set => Set(ref _isBeat, value); }

    private double Multiplier => TapTempo.NoteDivisions[_divisionIndex].Multiplier;

    private int RawMs => TapTempo.DelayMs(_bpm, Multiplier);

    /// <summary>The delay's current Time (ms); seeds the BPM unless it's the echo of our own send.</summary>
    public void Sync(int ms)
    {
        if (ms == _currentMs)
        {
            return;
        }

        _currentMs = ms;

        if (ms != _lastSent && ms > 0)
        {
            _bpm = TapTempo.ClampBpm(60000 * Multiplier / ms);
            RestartBeat();
            OnPropertyChanged("");
        }
    }

    public void Tap()
    {
        _taps.Add(Environment.TickCount64);

        if (_taps.Count > 4)
        {
            _taps.RemoveAt(0);
        }

        _tapReset.Stop();
        _tapReset.Start();

        if (TapTempo.BpmFromTaps(_taps) is { } tapped)
        {
            Bpm = tapped;
        }
    }

    public void Dispose()
    {
        _beat.Stop();
        _beatOff.Stop();
        _tapReset.Stop();
    }

    private void Send()
    {
        var ms = Math.Clamp(RawMs, _timeMin, _timeMax);
        _lastSent = ms;
        _send(ms);
    }

    private void RestartBeat()
    {
        var period = Math.Clamp((int)Math.Round(60000 / _bpm), 60, 2000);
        _beat.Stop();
        _beat.Interval = TimeSpan.FromMilliseconds(period);
        _beatOff.Interval = TimeSpan.FromMilliseconds(Math.Clamp((int)Math.Round(period * 0.28), 40, 140));
        _beat.Start();
    }
}
