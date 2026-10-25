using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ServoBench.Presentation;

public readonly record struct TrendSample(TimeSpan Time, double Setpoint, double Speed, double Current);

/// <summary>
/// Rolling window of speed and current samples for the trend chart. Stored in a fixed ring
/// buffer so a long session never grows memory.
/// </summary>
public sealed class TrendViewModel : ObservableObject
{
    private readonly TrendSample[] _buffer;
    private int _start;
    private int _count;
    private bool _isFrozen;
    private DateTimeOffset? _origin;

    public TrendViewModel(int capacity = 300)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        _buffer = new TrendSample[capacity];
        ClearCommand = new RelayCommand(Clear);
    }

    /// <summary>Raised after samples change, so a chart control can redraw.</summary>
    public event EventHandler? Updated;

    public int Capacity => _buffer.Length;

    public int Count => _count;

    /// <summary>While frozen, new samples are dropped so the user can study a step response.</summary>
    public bool IsFrozen
    {
        get => _isFrozen;
        set => SetProperty(ref _isFrozen, value);
    }

    public IRelayCommand ClearCommand { get; }

    public void Add(DateTimeOffset timestamp, double setpoint, double speed, double current)
    {
        if (_isFrozen)
        {
            return;
        }

        _origin ??= timestamp;
        var sample = new TrendSample(timestamp - _origin.Value, setpoint, speed, current);

        if (_count < _buffer.Length)
        {
            _buffer[(_start + _count) % _buffer.Length] = sample;
            _count++;
        }
        else
        {
            _buffer[_start] = sample;
            _start = (_start + 1) % _buffer.Length;
        }

        OnPropertyChanged(nameof(Count));
        Updated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Samples oldest to newest.</summary>
    public IReadOnlyList<TrendSample> Snapshot()
    {
        var copy = new TrendSample[_count];
        for (var i = 0; i < _count; i++)
        {
            copy[i] = _buffer[(_start + i) % _buffer.Length];
        }

        return copy;
    }

    public void Clear()
    {
        _start = 0;
        _count = 0;
        _origin = null;
        OnPropertyChanged(nameof(Count));
        Updated?.Invoke(this, EventArgs.Empty);
    }
}
