using System.Diagnostics;

namespace ServoBench.Simulator;

/// <summary>Advances a simulated drive in real time on a background task.</summary>
public sealed class SimulationClock : IAsyncDisposable
{
    private readonly SimulatedServoDrive _drive;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public SimulationClock(SimulatedServoDrive drive, TimeSpan? interval = null)
    {
        _drive = drive ?? throw new ArgumentNullException(nameof(drive));
        _interval = interval ?? TimeSpan.FromMilliseconds(10);
    }

    public void Start() => _loop = RunAsync(_stop.Token);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            await _loop.ConfigureAwait(false);
        }

        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_interval);
        var watch = Stopwatch.StartNew();
        var last = watch.Elapsed;

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                // Step by real elapsed time so timer jitter does not slow the simulated motor down.
                var now = watch.Elapsed;
                _drive.Advance(now - last);
                last = now;
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }
}
