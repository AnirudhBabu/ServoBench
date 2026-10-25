namespace ServoBench.Core.Drive;

/// <summary>
/// Holding-register layout of the ServoBench reference drive. Every register is 16 bits.
/// Real drives publish an equivalent table in their fieldbus manual.
/// </summary>
public static class DriveRegisterMap
{
    /// <summary>Registers 0..63 exist; anything above is an illegal address.</summary>
    public const ushort RegisterCount = 64;

    // Control and status
    public const ushort ControlWord = 0;
    public const ushort StatusWord = 1;
    public const ushort FaultCode = 2;

    // Parameters
    public const ushort SpeedSetpoint = 10;
    public const ushort MaxSpeed = 11;
    public const ushort AccelerationTime = 12;
    public const ushort DecelerationTime = 13;
    public const ushort CurrentLimit = 14;
    public const ushort SpeedLoopKp = 15;
    public const ushort SpeedLoopKi = 16;

    // Live telemetry (read-only)
    public const ushort ActualSpeed = 20;
    public const ushort MotorCurrent = 21;
    public const ushort DcBusVoltage = 22;
    public const ushort DriveTemperature = 23;

    // Nameplate (read-only)
    public const ushort RatedSpeed = 30;
    public const ushort RatedCurrent = 31;

    /// <summary>One read covering control, status, parameters, telemetry and nameplate.</summary>
    public const ushort SnapshotStart = 0;
    public const ushort SnapshotLength = 32;

    /// <summary>Telemetry scaling: current in 0.01 A, bus voltage in 0.1 V, temperature in 0.1 °C.</summary>
    public const double CurrentScale = 0.01;
    public const double VoltageScale = 0.1;
    public const double TemperatureScale = 0.1;
}

[Flags]
public enum ControlWordBits : ushort
{
    None = 0,
    Enable = 1 << 0,
    FaultReset = 1 << 7,
}

[Flags]
public enum DriveStatus : ushort
{
    None = 0,
    Enabled = 1 << 0,
    Faulted = 1 << 1,
    AtSpeed = 1 << 2,
    CurrentLimited = 1 << 3,
}

public enum DriveFault : ushort
{
    None = 0,
    Overtemperature = 1,
    Overspeed = 2,
}
