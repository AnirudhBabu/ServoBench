namespace ServoBench.Core.Drive;

/// <summary>One coherent read of the drive: live telemetry plus the parameter values at that instant.</summary>
public sealed record DriveSnapshot
{
    public required DateTimeOffset Timestamp { get; init; }

    public required DriveStatus Status { get; init; }

    public required DriveFault Fault { get; init; }

    public required double ActualSpeed { get; init; }

    public required double MotorCurrent { get; init; }

    public required double DcBusVoltage { get; init; }

    public required double Temperature { get; init; }

    /// <summary>Engineering values keyed by <see cref="ParameterDefinition.Key"/>.</summary>
    public required IReadOnlyDictionary<string, double> Parameters { get; init; }

    public bool IsEnabled => Status.HasFlag(DriveStatus.Enabled);

    public bool IsFaulted => Status.HasFlag(DriveStatus.Faulted);

    public double SpeedSetpoint => Parameters.GetValueOrDefault(ParameterCatalog.SpeedSetpoint.Key);

    /// <summary>Decodes a block of registers starting at <see cref="DriveRegisterMap.SnapshotStart"/>.</summary>
    public static DriveSnapshot FromRegisters(IReadOnlyList<ushort> registers, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(registers);
        if (registers.Count < DriveRegisterMap.SnapshotLength)
        {
            throw new ArgumentException(
                $"Need {DriveRegisterMap.SnapshotLength} registers, got {registers.Count}.", nameof(registers));
        }

        var parameters = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var parameter in ParameterCatalog.All)
        {
            parameters[parameter.Key] = parameter.FromRaw(registers[parameter.Address - DriveRegisterMap.SnapshotStart]);
        }

        ushort At(ushort address) => registers[address - DriveRegisterMap.SnapshotStart];

        return new DriveSnapshot
        {
            Timestamp = timestamp,
            Status = (DriveStatus)At(DriveRegisterMap.StatusWord),
            Fault = (DriveFault)At(DriveRegisterMap.FaultCode),
            ActualSpeed = unchecked((short)At(DriveRegisterMap.ActualSpeed)),
            MotorCurrent = Math.Round(unchecked((short)At(DriveRegisterMap.MotorCurrent)) * DriveRegisterMap.CurrentScale, 2),
            DcBusVoltage = Math.Round(At(DriveRegisterMap.DcBusVoltage) * DriveRegisterMap.VoltageScale, 1),
            Temperature = Math.Round(unchecked((short)At(DriveRegisterMap.DriveTemperature)) * DriveRegisterMap.TemperatureScale, 1),
            Parameters = parameters,
        };
    }
}
