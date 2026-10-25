using ServoBench.Core.Modbus;

namespace ServoBench.Core.Drive;

/// <summary>High-level drive operations, independent of the fieldbus underneath.</summary>
public interface IDriveClient : IAsyncDisposable
{
    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task<DriveSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default);

    Task WriteParameterAsync(ParameterDefinition parameter, double value, CancellationToken cancellationToken = default);

    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

    Task ResetFaultAsync(CancellationToken cancellationToken = default);
}

/// <summary>Drive operations over Modbus, using the ServoBench register map.</summary>
public sealed class ModbusDriveClient : IDriveClient
{
    private readonly IModbusClient _modbus;
    private readonly TimeProvider _time;

    public ModbusDriveClient(IModbusClient modbus, TimeProvider? timeProvider = null)
    {
        _modbus = modbus ?? throw new ArgumentNullException(nameof(modbus));
        _time = timeProvider ?? TimeProvider.System;
    }

    public bool IsConnected => _modbus.IsConnected;

    public Task ConnectAsync(CancellationToken cancellationToken = default) => _modbus.ConnectAsync(cancellationToken);

    public async Task<DriveSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var registers = await _modbus
            .ReadHoldingRegistersAsync(DriveRegisterMap.SnapshotStart, DriveRegisterMap.SnapshotLength, cancellationToken)
            .ConfigureAwait(false);
        return DriveSnapshot.FromRegisters(registers, _time.GetUtcNow());
    }

    public Task WriteParameterAsync(ParameterDefinition parameter, double value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        if (!parameter.IsWritable)
        {
            throw new InvalidOperationException($"{parameter.Name} is read-only.");
        }

        // ToRaw validates range and resolution, so bad values never reach the wire.
        var raw = parameter.ToRaw(value);
        return _modbus.WriteSingleRegisterAsync(parameter.Address, raw, cancellationToken);
    }

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        _modbus.WriteSingleRegisterAsync(
            DriveRegisterMap.ControlWord,
            (ushort)(enabled ? ControlWordBits.Enable : ControlWordBits.None),
            cancellationToken);

    public Task ResetFaultAsync(CancellationToken cancellationToken = default) =>
        _modbus.WriteSingleRegisterAsync(DriveRegisterMap.ControlWord, (ushort)ControlWordBits.FaultReset, cancellationToken);

    public ValueTask DisposeAsync() => _modbus.DisposeAsync();
}
