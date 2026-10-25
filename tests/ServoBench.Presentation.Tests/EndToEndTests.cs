using System.Globalization;
using System.Net;
using ServoBench.Core.Drive;
using ServoBench.Simulator;
using Xunit;

namespace ServoBench.Presentation.Tests;

/// <summary>The real ViewModel, the real Modbus client and the simulated drive, over loopback TCP.</summary>
public sealed class EndToEndTests : IAsyncLifetime
{
    private readonly SimulatedServoDrive _drive = new();
    private readonly ModbusTcpServer _server;
    private readonly MainViewModel _vm;

    public EndToEndTests()
    {
        _server = new ModbusTcpServer(_drive, IPAddress.Loopback, port: 0);
        _vm = new MainViewModel(new ModbusDriveClientFactory(), new FakeProfileFileService(), culture: CultureInfo.InvariantCulture);
    }

    public Task InitializeAsync()
    {
        _server.Start();
        _vm.Port = _server.LocalEndpoint.Port;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _vm.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task Tune_enable_and_run_the_simulated_drive_from_the_view_model()
    {
        await _vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(_vm.IsConnected, _vm.StatusMessage);

        _vm.Parameters.Single(p => p.Definition == ParameterCatalog.AccelerationTime).EditText = "200";
        await _vm.WriteChangesCommand.ExecuteAsync(null);
        await _vm.EnableCommand.ExecuteAsync(null);
        _vm.SpeedCommandText = "1800";
        await _vm.SendSpeedCommand.ExecuteAsync(null);

        _drive.Advance(TimeSpan.FromSeconds(1.5));
        await _vm.PollAsync();

        Assert.Equal(200, _vm.Parameters.Single(p => p.Definition == ParameterCatalog.AccelerationTime).DeviceValue);
        Assert.True(_vm.IsEnabled);
        Assert.Equal(1800, _vm.ActualSpeed, tolerance: 18);
        Assert.True(_vm.IsAtSpeed);
    }

    [Fact]
    public async Task Stopping_the_server_shows_up_as_a_lost_connection()
    {
        await _vm.ConnectCommand.ExecuteAsync(null);

        await _server.DisposeAsync();
        await _vm.PollAsync();

        Assert.Equal(ConnectionState.Disconnected, _vm.State);
    }

    [Fact]
    public async Task An_invalid_port_is_reported_instead_of_crashing()
    {
        _vm.Port = 0;

        await _vm.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(ConnectionState.Disconnected, _vm.State);
        Assert.Contains("Could not connect", _vm.StatusMessage);
    }
}
