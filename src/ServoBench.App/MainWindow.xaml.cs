using System.Windows;
using System.Windows.Threading;
using ServoBench.Presentation;

namespace ServoBench.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _pollTimer;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _viewModel;

        // 10 Hz telemetry. The timer ticks on the UI thread, so the view model needs no dispatching,
        // and PollAsync skips a tick if the previous read is still in flight.
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _pollTimer.Tick += async (_, _) => await _viewModel.PollAsync();
        Loaded += (_, _) => _pollTimer.Start();
        Closed += (_, _) => _pollTimer.Stop();
    }
}
