using ServoBench.Core.Drive;
using ServoBench.Core.Modbus;

namespace ServoBench.Simulator;

/// <summary>Holding-register storage that can reject a request with a Modbus exception code.</summary>
public interface IRegisterBank
{
    ushort[] ReadRegisters(ushort startAddress, ushort count);

    void WriteRegisters(ushort startAddress, IReadOnlyList<ushort> values);
}

public sealed class RegisterAccessException : Exception
{
    public RegisterAccessException(ModbusExceptionCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public ModbusExceptionCode Code { get; }
}

/// <summary>
/// A servo drive and motor simulated well enough to make tuning meaningful: a ramped speed
/// reference, a PI speed loop with anti-windup, a current limit, friction and load, a thermal
/// model and two protection faults. All state is guarded by one lock so the Modbus server and
/// the simulation clock can run on different threads.
/// </summary>
public sealed class SimulatedServoDrive : IRegisterBank
{
    /// <summary>Speed reached per second of acceleration for each amp of torque-producing current.</summary>
    public const double AccelerationPerAmp = 5000;

    public const double RatedSpeedRpm = 3000;
    public const double FrictionCurrent = 0.4;
    public const double ViscousCurrentPerRpm = 0.0005;
    public const double AmbientTemperature = 25;
    public const double OvertemperatureTrip = 90;
    public const double OvertemperatureResetBelow = 80;
    public const double OverspeedFactor = 1.1;

    private static readonly TimeSpan SubStep = TimeSpan.FromMilliseconds(1);

    private readonly object _sync = new();
    private readonly ushort[] _registers = new ushort[DriveRegisterMap.RegisterCount];

    private double _speed;
    private double _reference;
    private double _integral;
    private double _current;
    private double _temperature = AmbientTemperature;
    private double _elapsedSeconds;
    private bool _currentLimited;
    private DriveFault _fault;

    public SimulatedServoDrive()
    {
        foreach (var parameter in ParameterCatalog.All)
        {
            _registers[parameter.Address] = parameter.ToRaw(parameter.DefaultValue);
        }

        PublishTelemetry();
    }

    public double ActualSpeed { get { lock (_sync) { return _speed; } } }

    public double MotorCurrent { get { lock (_sync) { return _current; } } }

    public double Temperature { get { lock (_sync) { return _temperature; } } }

    public DriveFault Fault { get { lock (_sync) { return _fault; } } }

    public bool IsEnabled { get { lock (_sync) { return Enabled; } } }

    /// <summary>Test hook: forces the thermal state, e.g. to exercise the overtemperature trip.</summary>
    public void SetTemperature(double celsius)
    {
        lock (_sync)
        {
            _temperature = celsius;
            PublishTelemetry();
        }
    }

    /// <summary>Advances the simulation by <paramref name="elapsed"/>, in fixed 1 ms sub-steps.</summary>
    public void Advance(TimeSpan elapsed)
    {
        lock (_sync)
        {
            var steps = Math.Max(1, (int)Math.Round(elapsed / SubStep));
            var dt = SubStep.TotalSeconds;
            for (var i = 0; i < steps; i++)
            {
                Step(dt);
            }

            PublishTelemetry();
        }
    }

    public ushort[] ReadRegisters(ushort startAddress, ushort count)
    {
        if (count == 0 || startAddress + count > DriveRegisterMap.RegisterCount)
        {
            throw new RegisterAccessException(
                ModbusExceptionCode.IllegalDataAddress,
                $"Registers {startAddress}..{startAddress + count - 1} do not exist.");
        }

        lock (_sync)
        {
            return _registers.AsSpan(startAddress, count).ToArray();
        }
    }

    public void WriteRegisters(ushort startAddress, IReadOnlyList<ushort> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        lock (_sync)
        {
            // Validate every register before touching any, so a multi-register write is all or nothing.
            for (var i = 0; i < values.Count; i++)
            {
                ValidateWrite((ushort)(startAddress + i), values[i]);
            }

            for (var i = 0; i < values.Count; i++)
            {
                ApplyWrite((ushort)(startAddress + i), values[i]);
            }

            PublishTelemetry();
        }
    }

    private bool Enabled => (_registers[DriveRegisterMap.ControlWord] & (ushort)ControlWordBits.Enable) != 0;

    private double Param(ParameterDefinition parameter) => parameter.FromRaw(_registers[parameter.Address]);

    private static void ValidateWrite(ushort address, ushort value)
    {
        if (address == DriveRegisterMap.ControlWord)
        {
            const ushort allowed = (ushort)(ControlWordBits.Enable | ControlWordBits.FaultReset);
            if ((value & ~allowed) != 0)
            {
                throw new RegisterAccessException(ModbusExceptionCode.IllegalDataValue, "Unsupported control word bits.");
            }

            return;
        }

        var parameter = ParameterCatalog.FindByAddress(address);
        if (parameter is null || !parameter.IsWritable)
        {
            throw new RegisterAccessException(ModbusExceptionCode.IllegalDataAddress, $"Register {address} is not writable.");
        }

        if (parameter.Validate(parameter.FromRaw(value)) is { } error)
        {
            throw new RegisterAccessException(ModbusExceptionCode.IllegalDataValue, $"{parameter.Name}: {error}");
        }
    }

    private void ApplyWrite(ushort address, ushort value)
    {
        if (address == DriveRegisterMap.ControlWord)
        {
            var bits = (ControlWordBits)value;
            if (bits.HasFlag(ControlWordBits.FaultReset))
            {
                // Overtemperature only clears once the drive has cooled; resetting also disables.
                if (_fault != DriveFault.Overtemperature || _temperature < OvertemperatureResetBelow)
                {
                    _fault = DriveFault.None;
                }

                _registers[address] = 0;
                return;
            }

            if (bits.HasFlag(ControlWordBits.Enable) && !Enabled)
            {
                // Bumpless start: the ramp begins from the current speed.
                _reference = _speed;
                _integral = 0;
            }

            _registers[address] = _fault == DriveFault.None ? value : (ushort)0;
            return;
        }

        _registers[address] = value;
    }

    private void Step(double dt)
    {
        _elapsedSeconds += dt;

        var maxSpeed = Param(ParameterCatalog.MaxSpeed);
        var running = Enabled && _fault == DriveFault.None;

        if (running)
        {
            var target = Math.Clamp(Param(ParameterCatalog.SpeedSetpoint), -maxSpeed, maxSpeed);
            AdvanceRamp(target, dt);

            var error = _reference - _speed;
            _integral += error * dt;
            var command = ((Param(ParameterCatalog.SpeedLoopKp) * error) + (Param(ParameterCatalog.SpeedLoopKi) * _integral)) / 100;

            var limit = Param(ParameterCatalog.CurrentLimit);
            _currentLimited = Math.Abs(command) > limit;
            if (_currentLimited)
            {
                _integral -= error * dt; // anti-windup: stop integrating while saturated
                command = Math.Clamp(command, -limit, limit);
            }

            _current = command;
        }
        else
        {
            _current = 0;
            _integral = 0;
            _currentLimited = false;
            _reference = _speed;
        }

        var losses = (FrictionCurrent * Math.Tanh(_speed / 10)) + (ViscousCurrentPerRpm * _speed);
        var acceleration = AccelerationPerAmp * (_current - losses);
        var next = _speed + (acceleration * dt);

        // A coasting motor stops; friction must not drive it backwards through zero.
        if (!running && Math.Sign(next) != Math.Sign(_speed))
        {
            next = 0;
        }

        _speed = next;

        // First-order thermal model: I^2 heating, Newtonian cooling, 100 s time constant.
        _temperature += ((_current * _current * 0.03) - ((_temperature - AmbientTemperature) * 0.01)) * dt;

        if (_fault == DriveFault.None)
        {
            if (_temperature >= OvertemperatureTrip)
            {
                TripFault(DriveFault.Overtemperature);
            }
            else if (Math.Abs(_speed) > maxSpeed * OverspeedFactor)
            {
                TripFault(DriveFault.Overspeed);
            }
        }
    }

    private void AdvanceRamp(double target, double dt)
    {
        var accelerating = Math.Abs(target) > Math.Abs(_reference) && Math.Sign(target) != -Math.Sign(_reference);
        var rampMs = accelerating ? Param(ParameterCatalog.AccelerationTime) : Param(ParameterCatalog.DecelerationTime);
        var maxStep = RatedSpeedRpm / (rampMs / 1000) * dt;
        var delta = Math.Clamp(target - _reference, -maxStep, maxStep);
        _reference += delta;
    }

    private void TripFault(DriveFault fault)
    {
        _fault = fault;
        _registers[DriveRegisterMap.ControlWord] = 0;
        _current = 0;
        _integral = 0;
    }

    private void PublishTelemetry()
    {
        var maxSpeed = Param(ParameterCatalog.MaxSpeed);
        var target = Math.Clamp(Param(ParameterCatalog.SpeedSetpoint), -maxSpeed, maxSpeed);
        var atSpeed = Enabled
            && Math.Abs(_reference - target) < 0.5
            && Math.Abs(_speed - target) <= Math.Max(10, 0.01 * maxSpeed);

        var status = DriveStatus.None;
        if (Enabled)
        {
            status |= DriveStatus.Enabled;
        }

        if (_fault != DriveFault.None)
        {
            status |= DriveStatus.Faulted;
        }

        if (atSpeed)
        {
            status |= DriveStatus.AtSpeed;
        }

        if (_currentLimited)
        {
            status |= DriveStatus.CurrentLimited;
        }

        var busVoltage = 565 - (1.5 * Math.Abs(_current)) + Math.Sin(_elapsedSeconds * 2 * Math.PI * 0.7);

        _registers[DriveRegisterMap.StatusWord] = (ushort)status;
        _registers[DriveRegisterMap.FaultCode] = (ushort)_fault;
        _registers[DriveRegisterMap.ActualSpeed] = ToSigned(_speed);
        _registers[DriveRegisterMap.MotorCurrent] = ToSigned(_current / DriveRegisterMap.CurrentScale);
        _registers[DriveRegisterMap.DcBusVoltage] = (ushort)Math.Round(busVoltage / DriveRegisterMap.VoltageScale);
        _registers[DriveRegisterMap.DriveTemperature] = ToSigned(_temperature / DriveRegisterMap.TemperatureScale);
    }

    private static ushort ToSigned(double value) =>
        unchecked((ushort)(short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue));
}
