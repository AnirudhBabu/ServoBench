using System.Net;
using System.Net.Sockets;
using ServoBench.Core.Drive;
using ServoBench.Core.Modbus;
using ServoBench.Simulator;
using Xunit;

namespace ServoBench.Core.Tests;

/// <summary>Client and simulator talking over a real loopback socket.</summary>
public sealed class ModbusTcpRoundTripTests : IAsyncLifetime
{
    private readonly SimulatedServoDrive _drive = new();
    private readonly ModbusTcpServer _server;

    public ModbusTcpRoundTripTests()
    {
        _server = new ModbusTcpServer(_drive, IPAddress.Loopback, port: 0);
    }

    public Task InitializeAsync()
    {
        _server.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private async Task<ModbusDriveClient> ConnectAsync()
    {
        var client = new ModbusDriveClient(new ModbusTcpClient("127.0.0.1", _server.LocalEndpoint.Port));
        await client.ConnectAsync();
        return client;
    }

    [Fact]
    public async Task Snapshot_reads_default_parameters_over_tcp()
    {
        await using var client = await ConnectAsync();

        var snapshot = await client.ReadSnapshotAsync();

        Assert.Equal(6.0, snapshot.Parameters[ParameterCatalog.CurrentLimit.Key]);
        Assert.Equal(3000, snapshot.Parameters[ParameterCatalog.RatedSpeed.Key]);
        Assert.False(snapshot.IsEnabled);
    }

    [Fact]
    public async Task Written_parameter_is_read_back()
    {
        await using var client = await ConnectAsync();

        await client.WriteParameterAsync(ParameterCatalog.SpeedLoopKp, 1.234);
        await client.WriteParameterAsync(ParameterCatalog.SpeedSetpoint, -750);
        var snapshot = await client.ReadSnapshotAsync();

        Assert.Equal(1.234, snapshot.Parameters[ParameterCatalog.SpeedLoopKp.Key]);
        Assert.Equal(-750, snapshot.SpeedSetpoint);
    }

    [Fact]
    public async Task Enable_and_run_shows_up_in_telemetry()
    {
        await using var client = await ConnectAsync();

        await client.SetEnabledAsync(true);
        await client.WriteParameterAsync(ParameterCatalog.SpeedSetpoint, 1200);
        _drive.Advance(TimeSpan.FromSeconds(2));
        var snapshot = await client.ReadSnapshotAsync();

        Assert.True(snapshot.IsEnabled);
        Assert.Equal(1200, snapshot.ActualSpeed, tolerance: 12);
        Assert.True(snapshot.MotorCurrent > 0);
    }

    [Fact]
    public async Task Device_rejection_arrives_as_modbus_exception_and_keeps_the_connection()
    {
        var modbus = new ModbusTcpClient("127.0.0.1", _server.LocalEndpoint.Port);
        await using var _ = modbus;
        await modbus.ConnectAsync();

        // Bypass client-side validation to prove the device-side check and the exception path.
        var ex = await Assert.ThrowsAsync<ModbusException>(() =>
            modbus.WriteSingleRegisterAsync(ParameterCatalog.CurrentLimit.Address, 999));

        Assert.Equal(ModbusExceptionCode.IllegalDataValue, ex.ExceptionCode);
        Assert.True(modbus.IsConnected);
        Assert.Single(await modbus.ReadHoldingRegistersAsync(DriveRegisterMap.StatusWord, 1));
    }

    [Fact]
    public async Task Several_clients_can_share_the_drive()
    {
        await using var first = await ConnectAsync();
        await using var second = await ConnectAsync();

        await first.WriteParameterAsync(ParameterCatalog.MaxSpeed, 2500);

        Assert.Equal(2500, (await second.ReadSnapshotAsync()).Parameters[ParameterCatalog.MaxSpeed.Key]);
    }

    [Fact]
    public async Task Connecting_to_a_closed_port_fails_cleanly()
    {
        // Grab a free port, then release it so nothing is listening there.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        await using var client = new ModbusTcpClient("127.0.0.1", port);

        // Linux refuses instantly; Windows retries the SYN and can hit the client timeout first.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync());
        Assert.True(ex is SocketException or TimeoutException, $"unexpected {ex.GetType().Name}");
        Assert.False(client.IsConnected);
    }
}
