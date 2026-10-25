using ServoBench.Core.Drive;
using ServoBench.Core.Modbus;
using ServoBench.Simulator;
using Xunit;

namespace ServoBench.Core.Tests;

public class SimulatedServoDriveTests
{
    private static void Write(SimulatedServoDrive drive, ParameterDefinition parameter, double value) =>
        drive.WriteRegisters(parameter.Address, [parameter.ToRaw(value)]);

    private static void Enable(SimulatedServoDrive drive) =>
        drive.WriteRegisters(DriveRegisterMap.ControlWord, [(ushort)ControlWordBits.Enable]);

    private static DriveSnapshot Snapshot(SimulatedServoDrive drive) =>
        DriveSnapshot.FromRegisters(
            drive.ReadRegisters(DriveRegisterMap.SnapshotStart, DriveRegisterMap.SnapshotLength),
            DateTimeOffset.UnixEpoch);

    [Fact]
    public void Disabled_drive_does_not_move()
    {
        var drive = new SimulatedServoDrive();
        Write(drive, ParameterCatalog.SpeedSetpoint, 1000);

        drive.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(0, drive.ActualSpeed);
        Assert.False(Snapshot(drive).IsEnabled);
    }

    [Fact]
    public void Enabled_drive_reaches_setpoint_and_reports_at_speed()
    {
        var drive = new SimulatedServoDrive();
        Enable(drive);
        Write(drive, ParameterCatalog.SpeedSetpoint, 1500);

        drive.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(1500, drive.ActualSpeed, tolerance: 15);
        Assert.True(Snapshot(drive).Status.HasFlag(DriveStatus.AtSpeed));
    }

    [Fact]
    public void Acceleration_follows_the_ramp_time()
    {
        var drive = new SimulatedServoDrive();
        Write(drive, ParameterCatalog.AccelerationTime, 1000); // 0 → 3000 rpm in 1 s
        Enable(drive);
        Write(drive, ParameterCatalog.SpeedSetpoint, 3000);

        drive.Advance(TimeSpan.FromMilliseconds(500));

        // Halfway through the ramp the motor tracks ~1500 rpm, lagging a little behind the reference.
        Assert.InRange(drive.ActualSpeed, 1300, 1550);
    }

    [Fact]
    public void Low_current_limit_slows_acceleration_and_is_flagged()
    {
        var drive = new SimulatedServoDrive();
        Write(drive, ParameterCatalog.AccelerationTime, 10); // ask for an almost instant ramp
        Write(drive, ParameterCatalog.CurrentLimit, 1.0);
        Enable(drive);
        Write(drive, ParameterCatalog.SpeedSetpoint, 3000);

        drive.Advance(TimeSpan.FromMilliseconds(100));

        var snapshot = Snapshot(drive);
        Assert.True(snapshot.Status.HasFlag(DriveStatus.CurrentLimited));
        Assert.True(drive.ActualSpeed < 600, $"speed {drive.ActualSpeed:F0} rpm should be torque-limited");
        Assert.Equal(1.0, snapshot.MotorCurrent, tolerance: 0.01);
    }

    [Fact]
    public void Integral_gain_removes_steady_state_error_under_load()
    {
        var withoutKi = new SimulatedServoDrive();
        Write(withoutKi, ParameterCatalog.SpeedLoopKi, 0);
        Enable(withoutKi);
        Write(withoutKi, ParameterCatalog.SpeedSetpoint, 2000);
        withoutKi.Advance(TimeSpan.FromSeconds(3));

        var withKi = new SimulatedServoDrive();
        Enable(withKi);
        Write(withKi, ParameterCatalog.SpeedSetpoint, 2000);
        withKi.Advance(TimeSpan.FromSeconds(3));

        Assert.True(2000 - withoutKi.ActualSpeed > 50, "P-only control should sag under friction and load");
        Assert.Equal(2000, withKi.ActualSpeed, tolerance: 2);
    }

    [Fact]
    public void Aggressive_tuning_overshoots_into_an_overspeed_trip()
    {
        var drive = new SimulatedServoDrive();
        Write(drive, ParameterCatalog.MaxSpeed, 1000);
        Write(drive, ParameterCatalog.AccelerationTime, 10);
        Write(drive, ParameterCatalog.CurrentLimit, 15);
        Write(drive, ParameterCatalog.SpeedLoopKp, 0.05);
        Write(drive, ParameterCatalog.SpeedLoopKi, 50);
        Enable(drive);
        Write(drive, ParameterCatalog.SpeedSetpoint, 1000);

        drive.Advance(TimeSpan.FromSeconds(2));

        var snapshot = Snapshot(drive);
        Assert.Equal(DriveFault.Overspeed, snapshot.Fault);
        Assert.False(snapshot.IsEnabled);
    }

    [Fact]
    public void Overtemperature_trips_and_only_resets_once_cool()
    {
        var drive = new SimulatedServoDrive();
        Enable(drive);
        drive.SetTemperature(95);
        drive.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Equal(DriveFault.Overtemperature, drive.Fault);

        drive.WriteRegisters(DriveRegisterMap.ControlWord, [(ushort)ControlWordBits.FaultReset]);
        Assert.Equal(DriveFault.Overtemperature, drive.Fault);

        drive.SetTemperature(40);
        drive.WriteRegisters(DriveRegisterMap.ControlWord, [(ushort)ControlWordBits.FaultReset]);
        Assert.Equal(DriveFault.None, drive.Fault);
        Assert.False(drive.IsEnabled);
    }

    [Fact]
    public void Faulted_drive_refuses_to_enable()
    {
        var drive = new SimulatedServoDrive();
        Enable(drive);
        drive.SetTemperature(95);
        drive.Advance(TimeSpan.FromMilliseconds(10));

        Enable(drive);

        Assert.False(drive.IsEnabled);
    }

    [Fact]
    public void Writes_are_validated_like_real_firmware()
    {
        var drive = new SimulatedServoDrive();

        var outOfRange = Assert.Throws<RegisterAccessException>(() =>
            drive.WriteRegisters(ParameterCatalog.CurrentLimit.Address, [500]));
        var readOnly = Assert.Throws<RegisterAccessException>(() =>
            drive.WriteRegisters(DriveRegisterMap.ActualSpeed, [1]));
        var missing = Assert.Throws<RegisterAccessException>(() =>
            drive.ReadRegisters(60, 10));

        Assert.Equal(ModbusExceptionCode.IllegalDataValue, outOfRange.Code);
        Assert.Equal(ModbusExceptionCode.IllegalDataAddress, readOnly.Code);
        Assert.Equal(ModbusExceptionCode.IllegalDataAddress, missing.Code);
    }

    [Fact]
    public void Multi_register_write_is_all_or_nothing()
    {
        var drive = new SimulatedServoDrive();
        var before = drive.ReadRegisters(DriveRegisterMap.AccelerationTime, 3);

        // Acceleration and deceleration are valid, the max speed of 5000 rpm is not.
        Assert.Throws<RegisterAccessException>(() =>
            drive.WriteRegisters(DriveRegisterMap.MaxSpeed, [5000, 200, 200]));

        Assert.Equal(before, drive.ReadRegisters(DriveRegisterMap.AccelerationTime, 3));
    }
}
