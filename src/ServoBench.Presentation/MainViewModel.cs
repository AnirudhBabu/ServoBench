using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServoBench.Core.Drive;
using ServoBench.Core.Modbus;
using ServoBench.Core.Profiles;

namespace ServoBench.Presentation;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
}

/// <summary>
/// Drives the whole window: connection, the parameter grid, motion commands, live telemetry,
/// the trend and profiles. Polling is triggered from outside (a DispatcherTimer in the app,
/// direct calls in tests) through <see cref="PollAsync"/>.
/// </summary>
public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IDriveClientFactory _clientFactory;
    private readonly IProfileFileService _files;
    private readonly TimeProvider _time;
    private readonly IFormatProvider _culture;

    private IDriveClient? _client;
    private ConnectionState _state;
    private string _host = "127.0.0.1";
    private int _port = 1502;
    private string _statusMessage = "Start the simulator, then connect.";
    private bool _polling;

    private DriveSnapshot? _snapshot;
    private string _speedCommandText = "0";
    private string? _speedCommandError;

    public MainViewModel(
        IDriveClientFactory clientFactory,
        IProfileFileService files,
        TimeProvider? timeProvider = null,
        IFormatProvider? culture = null)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _time = timeProvider ?? TimeProvider.System;
        _culture = culture ?? CultureInfo.CurrentCulture;

        // The speed setpoint is a live command, not configuration, so it gets its own control instead of a grid row.
        foreach (var definition in ParameterCatalog.All.Where(p => p != ParameterCatalog.SpeedSetpoint))
        {
            var row = new ParameterViewModel(definition, _culture);
            row.PropertyChanged += OnParameterChanged;
            Parameters.Add(row);
        }

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => _state == ConnectionState.Disconnected);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => IsConnected);
        ReadAllCommand = new AsyncRelayCommand(ReadAllAsync, () => IsConnected);
        WriteChangesCommand = new AsyncRelayCommand(WriteChangesAsync, () => IsConnected && HasPendingChanges && !HasInvalidEdits);
        RevertChangesCommand = new RelayCommand(RevertChanges, () => HasPendingChanges || HasInvalidEdits);
        EnableCommand = new AsyncRelayCommand(() => RunDriveCommandAsync(c => c.SetEnabledAsync(true), "Drive enabled."), () => IsConnected && !IsFaulted);
        DisableCommand = new AsyncRelayCommand(() => RunDriveCommandAsync(c => c.SetEnabledAsync(false), "Drive disabled."), () => IsConnected);
        ResetFaultCommand = new AsyncRelayCommand(() => RunDriveCommandAsync(c => c.ResetFaultAsync(), "Fault reset sent."), () => IsConnected && IsFaulted);
        SendSpeedCommand = new AsyncRelayCommand(SendSpeedAsync, () => IsConnected && _speedCommandError is null);
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsConnected);
        SaveProfileCommand = new AsyncRelayCommand(SaveProfileAsync, () => _snapshot is not null);
        LoadProfileCommand = new AsyncRelayCommand(LoadProfileAsync);
    }

    public ObservableCollection<ParameterViewModel> Parameters { get; } = [];

    public TrendViewModel Trend { get; } = new();

    public IAsyncRelayCommand ConnectCommand { get; }

    public IAsyncRelayCommand DisconnectCommand { get; }

    public IAsyncRelayCommand ReadAllCommand { get; }

    public IAsyncRelayCommand WriteChangesCommand { get; }

    public IRelayCommand RevertChangesCommand { get; }

    public IAsyncRelayCommand EnableCommand { get; }

    public IAsyncRelayCommand DisableCommand { get; }

    public IAsyncRelayCommand ResetFaultCommand { get; }

    public IAsyncRelayCommand SendSpeedCommand { get; }

    public IAsyncRelayCommand StopCommand { get; }

    public IAsyncRelayCommand SaveProfileCommand { get; }

    public IAsyncRelayCommand LoadProfileCommand { get; }

    // ------------------------------------------------------------------ connection

    public string Host
    {
        get => _host;
        set => SetProperty(ref _host, value);
    }

    public int Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    public ConnectionState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsConnected));
                RefreshCommands();
            }
        }
    }

    public bool IsConnected => _state == ConnectionState.Connected;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    // ------------------------------------------------------------------ live telemetry

    public bool IsEnabled => _snapshot?.IsEnabled == true;

    public bool IsFaulted => _snapshot?.IsFaulted == true;

    public bool IsAtSpeed => _snapshot?.Status.HasFlag(DriveStatus.AtSpeed) == true;

    public bool IsCurrentLimited => _snapshot?.Status.HasFlag(DriveStatus.CurrentLimited) == true;

    public string FaultText => _snapshot?.Fault switch
    {
        null or DriveFault.None => "No fault",
        DriveFault.Overtemperature => "Overtemperature: let the drive cool, then reset",
        DriveFault.Overspeed => "Overspeed: speed exceeded the limit by 10%",
        var other => $"Fault {(ushort)other}",
    };

    public double ActualSpeed => _snapshot?.ActualSpeed ?? 0;

    public double SpeedSetpoint => _snapshot?.SpeedSetpoint ?? 0;

    public double MotorCurrent => _snapshot?.MotorCurrent ?? 0;

    public double DcBusVoltage => _snapshot?.DcBusVoltage ?? 0;

    public double Temperature => _snapshot?.Temperature ?? 0;

    // ------------------------------------------------------------------ speed command

    public string SpeedCommandText
    {
        get => _speedCommandText;
        set
        {
            if (SetProperty(ref _speedCommandText, value))
            {
                ParameterCatalog.SpeedSetpoint.TryParse(value, _culture, out _, out var error);
                SpeedCommandError = error;
            }
        }
    }

    public string? SpeedCommandError
    {
        get => _speedCommandError;
        private set
        {
            if (SetProperty(ref _speedCommandError, value))
            {
                SendSpeedCommand.NotifyCanExecuteChanged();
            }
        }
    }

    // ------------------------------------------------------------------ pending edits

    public bool HasPendingChanges => Parameters.Any(p => p.IsDirty);

    public bool HasInvalidEdits => Parameters.Any(p => p.HasErrors);

    public int PendingChangeCount => Parameters.Count(p => p.IsDirty);

    // ------------------------------------------------------------------ polling

    /// <summary>Reads one snapshot and updates telemetry, the grid and the trend. Safe to call on a timer:
    /// overlapping calls are skipped rather than queued.</summary>
    public async Task PollAsync()
    {
        if (!IsConnected || _polling || _client is null)
        {
            return;
        }

        _polling = true;
        try
        {
            var snapshot = await _client.ReadSnapshotAsync();
            ApplySnapshot(snapshot);
        }
        catch (ModbusException ex)
        {
            StatusMessage = ex.Message;
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            await HandleConnectionLostAsync(ex);
        }
        finally
        {
            _polling = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }
    }

    // ------------------------------------------------------------------ command implementations

    private async Task ConnectAsync()
    {
        State = ConnectionState.Connecting;
        StatusMessage = $"Connecting to {Host}:{Port}…";
        IDriveClient? client = null;
        try
        {
            client = _clientFactory.Create(Host, Port);
            await client.ConnectAsync();
            var snapshot = await client.ReadSnapshotAsync();

            _client = client;
            Trend.Clear();
            ApplySnapshot(snapshot, overwriteEdits: true);
            SpeedCommandText = ParameterCatalog.SpeedSetpoint.Format(snapshot.SpeedSetpoint, _culture);
            State = ConnectionState.Connected;
            StatusMessage = $"Connected to {Host}:{Port}.";
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            if (client is not null)
            {
                await client.DisposeAsync();
            }

            State = ConnectionState.Disconnected;
            StatusMessage = $"Could not connect: {ex.Message}";
        }
    }

    private async Task DisconnectAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }

        State = ConnectionState.Disconnected;
        StatusMessage = "Disconnected.";
    }

    private async Task ReadAllAsync()
    {
        await RunAsync(async client =>
        {
            var snapshot = await client.ReadSnapshotAsync();
            ApplySnapshot(snapshot, overwriteEdits: true);
            StatusMessage = "Parameters read from drive.";
        });
    }

    private async Task WriteChangesAsync()
    {
        await RunAsync(async client =>
        {
            var written = 0;
            var rejected = 0;

            foreach (var row in Parameters.Where(p => p.IsDirty).ToList())
            {
                try
                {
                    await client.WriteParameterAsync(row.Definition, row.PendingValue!.Value);
                    row.MarkWritten();
                    written++;
                }
                catch (ModbusException ex)
                {
                    // The drive refused this one value; keep going with the rest.
                    row.SetDeviceError(ex.Message);
                    rejected++;
                }
            }

            // Read back so the grid shows what the drive actually holds now.
            ApplySnapshot(await client.ReadSnapshotAsync());

            StatusMessage = rejected == 0
                ? $"Wrote {written} parameter{(written == 1 ? string.Empty : "s")}."
                : $"Wrote {written}, drive rejected {rejected}. See the highlighted rows.";
        });
    }

    private void RevertChanges()
    {
        foreach (var row in Parameters)
        {
            row.Revert();
        }

        StatusMessage = "Edits discarded.";
    }

    private async Task SendSpeedAsync()
    {
        if (!ParameterCatalog.SpeedSetpoint.TryParse(SpeedCommandText, _culture, out var speed, out var error))
        {
            SpeedCommandError = error;
            return;
        }

        await RunDriveCommandAsync(
            c => c.WriteParameterAsync(ParameterCatalog.SpeedSetpoint, speed),
            $"Speed setpoint {ParameterCatalog.SpeedSetpoint.Format(speed, _culture)} rpm sent.");
    }

    private async Task StopAsync()
    {
        SpeedCommandText = ParameterCatalog.SpeedSetpoint.Format(0, _culture);
        await RunDriveCommandAsync(c => c.WriteParameterAsync(ParameterCatalog.SpeedSetpoint, 0), "Stopping: setpoint 0 rpm.");
    }

    private async Task SaveProfileAsync()
    {
        if (_snapshot is null)
        {
            return;
        }

        var path = _files.ChooseSavePath($"drive-profile-{_time.GetLocalNow():yyyyMMdd-HHmm}.json");
        if (path is null)
        {
            return;
        }

        // Save what the drive holds, not unsent edits, so a profile always describes a real configuration.
        var name = Path.GetFileNameWithoutExtension(path);
        var profile = ProfileSerializer.Create(name, _snapshot.Parameters, _time.GetUtcNow());
        try
        {
            await _files.WriteAllTextAsync(path, ProfileSerializer.Serialize(profile));
            StatusMessage = $"Saved profile '{name}' ({profile.Parameters.Count} parameters).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Could not save profile: {ex.Message}";
        }
    }

    private async Task LoadProfileAsync()
    {
        var path = _files.ChooseOpenPath();
        if (path is null)
        {
            return;
        }

        ProfileLoadResult result;
        try
        {
            result = ProfileSerializer.Deserialize(await _files.ReadAllTextAsync(path));
        }
        catch (Exception ex) when (ex is ProfileFormatException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Could not load profile: {ex.Message}";
            return;
        }

        // Loading stages the values as edits. Nothing reaches the drive until the user writes them.
        foreach (var row in Parameters)
        {
            if (result.ApplicableValues.TryGetValue(row.Definition, out var value))
            {
                row.Stage(value);
            }
        }

        var warnings = result.Warnings.Count == 0 ? string.Empty : $" {result.Warnings.Count} skipped: {string.Join(" ", result.Warnings)}";
        StatusMessage = $"Loaded '{result.Profile.Name}': {PendingChangeCount} change(s) staged. Review, then Write to drive.{warnings}";
    }

    // ------------------------------------------------------------------ helpers

    private Task RunDriveCommandAsync(Func<IDriveClient, Task> action, string successMessage) =>
        RunAsync(async client =>
        {
            await action(client);
            ApplySnapshot(await client.ReadSnapshotAsync());
            StatusMessage = successMessage;
        });

    /// <summary>Runs a drive operation, turning drive rejections and lost connections into status messages
    /// instead of exceptions that would escape a command.</summary>
    private async Task RunAsync(Func<IDriveClient, Task> operation)
    {
        if (_client is null)
        {
            return;
        }

        try
        {
            await operation(_client);
        }
        catch (ModbusException ex)
        {
            StatusMessage = ex.Message;
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            await HandleConnectionLostAsync(ex);
        }
    }

    private void ApplySnapshot(DriveSnapshot snapshot, bool overwriteEdits = false)
    {
        var wasFaulted = IsFaulted;
        _snapshot = snapshot;

        foreach (var row in Parameters)
        {
            if (snapshot.Parameters.TryGetValue(row.Definition.Key, out var value))
            {
                row.UpdateFromDevice(value);
                if (overwriteEdits)
                {
                    row.Revert();
                }
            }
        }

        Trend.Add(snapshot.Timestamp, snapshot.SpeedSetpoint, snapshot.ActualSpeed, snapshot.MotorCurrent);

        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(IsFaulted));
        OnPropertyChanged(nameof(IsAtSpeed));
        OnPropertyChanged(nameof(IsCurrentLimited));
        OnPropertyChanged(nameof(FaultText));
        OnPropertyChanged(nameof(ActualSpeed));
        OnPropertyChanged(nameof(SpeedSetpoint));
        OnPropertyChanged(nameof(MotorCurrent));
        OnPropertyChanged(nameof(DcBusVoltage));
        OnPropertyChanged(nameof(Temperature));

        if (!wasFaulted && snapshot.IsFaulted)
        {
            StatusMessage = $"Drive tripped: {FaultText}.";
        }

        RefreshCommands();
    }

    private async Task HandleConnectionLostAsync(Exception ex)
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }

        State = ConnectionState.Disconnected;
        StatusMessage = $"Connection lost: {ex.Message}";
    }

    // ArgumentException covers a bad host or port typed into the connection bar.
    private static bool IsConnectionFailure(Exception ex) =>
        ex is IOException or SocketException or TimeoutException or ObjectDisposedException or InvalidOperationException or ArgumentException;

    private void OnParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ParameterViewModel.IsDirty) or nameof(ParameterViewModel.HasErrors))
        {
            OnPropertyChanged(nameof(HasPendingChanges));
            OnPropertyChanged(nameof(HasInvalidEdits));
            OnPropertyChanged(nameof(PendingChangeCount));
            WriteChangesCommand.NotifyCanExecuteChanged();
            RevertChangesCommand.NotifyCanExecuteChanged();
        }
    }

    private void RefreshCommands()
    {
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        ReadAllCommand.NotifyCanExecuteChanged();
        WriteChangesCommand.NotifyCanExecuteChanged();
        RevertChangesCommand.NotifyCanExecuteChanged();
        EnableCommand.NotifyCanExecuteChanged();
        DisableCommand.NotifyCanExecuteChanged();
        ResetFaultCommand.NotifyCanExecuteChanged();
        SendSpeedCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        SaveProfileCommand.NotifyCanExecuteChanged();
    }
}
