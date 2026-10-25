namespace ServoBench.Core.Drive;

/// <summary>The parameter table of the ServoBench reference drive.</summary>
public static class ParameterCatalog
{
    public const string MotionGroup = "Motion";
    public const string LimitsGroup = "Limits";
    public const string TuningGroup = "Speed loop tuning";
    public const string NameplateGroup = "Nameplate";

    public static ParameterDefinition SpeedSetpoint { get; } = new()
    {
        Key = "motion.speedSetpoint",
        Name = "Speed setpoint",
        Group = MotionGroup,
        Address = DriveRegisterMap.SpeedSetpoint,
        Unit = "rpm",
        IsSigned = true,
        Minimum = -3000,
        Maximum = 3000,
        DefaultValue = 0,
        IsPersistent = false,
        Description = "Commanded motor speed. Negative values reverse the motor.",
    };

    public static ParameterDefinition AccelerationTime { get; } = new()
    {
        Key = "motion.accelerationTime",
        Name = "Acceleration time",
        Group = MotionGroup,
        Address = DriveRegisterMap.AccelerationTime,
        Unit = "ms",
        Minimum = 10,
        Maximum = 10000,
        DefaultValue = 500,
        Description = "Ramp time from standstill to rated speed.",
    };

    public static ParameterDefinition DecelerationTime { get; } = new()
    {
        Key = "motion.decelerationTime",
        Name = "Deceleration time",
        Group = MotionGroup,
        Address = DriveRegisterMap.DecelerationTime,
        Unit = "ms",
        Minimum = 10,
        Maximum = 10000,
        DefaultValue = 500,
        Description = "Ramp time from rated speed to standstill.",
    };

    public static ParameterDefinition MaxSpeed { get; } = new()
    {
        Key = "limits.maxSpeed",
        Name = "Maximum speed",
        Group = LimitsGroup,
        Address = DriveRegisterMap.MaxSpeed,
        Unit = "rpm",
        Minimum = 100,
        Maximum = 3000,
        DefaultValue = 3000,
        Description = "Setpoints are clamped to this speed. Exceeding it by 10% trips an overspeed fault.",
    };

    public static ParameterDefinition CurrentLimit { get; } = new()
    {
        Key = "limits.currentLimit",
        Name = "Current limit",
        Group = LimitsGroup,
        Address = DriveRegisterMap.CurrentLimit,
        Unit = "A",
        Scale = 0.1,
        Minimum = 0.5,
        Maximum = 15.0,
        DefaultValue = 6.0,
        Description = "Peak motor current. Limits torque, so it also limits how fast the motor can accelerate.",
    };

    public static ParameterDefinition SpeedLoopKp { get; } = new()
    {
        Key = "tuning.speedKp",
        Name = "Speed loop Kp",
        Group = TuningGroup,
        Address = DriveRegisterMap.SpeedLoopKp,
        Scale = 0.001,
        Minimum = 0.001,
        Maximum = 10.0,
        DefaultValue = 0.5,
        Description = "Proportional gain. Higher is stiffer and faster, too high oscillates.",
    };

    public static ParameterDefinition SpeedLoopKi { get; } = new()
    {
        Key = "tuning.speedKi",
        Name = "Speed loop Ki",
        Group = TuningGroup,
        Address = DriveRegisterMap.SpeedLoopKi,
        Scale = 0.01,
        Minimum = 0,
        Maximum = 50.0,
        DefaultValue = 5.0,
        Description = "Integral gain. Removes steady-state speed error under load; too high overshoots.",
    };

    public static ParameterDefinition RatedSpeed { get; } = new()
    {
        Key = "nameplate.ratedSpeed",
        Name = "Rated speed",
        Group = NameplateGroup,
        Address = DriveRegisterMap.RatedSpeed,
        Unit = "rpm",
        Minimum = 0,
        Maximum = 65535,
        DefaultValue = 3000,
        Access = ParameterAccess.ReadOnly,
        IsPersistent = false,
        Description = "Motor nameplate speed.",
    };

    public static ParameterDefinition RatedCurrent { get; } = new()
    {
        Key = "nameplate.ratedCurrent",
        Name = "Rated current",
        Group = NameplateGroup,
        Address = DriveRegisterMap.RatedCurrent,
        Unit = "A",
        Scale = 0.1,
        Minimum = 0,
        Maximum = 6553.5,
        DefaultValue = 6.0,
        Access = ParameterAccess.ReadOnly,
        IsPersistent = false,
        Description = "Motor nameplate continuous current.",
    };

    public static IReadOnlyList<ParameterDefinition> All { get; } =
    [
        SpeedSetpoint,
        AccelerationTime,
        DecelerationTime,
        MaxSpeed,
        CurrentLimit,
        SpeedLoopKp,
        SpeedLoopKi,
        RatedSpeed,
        RatedCurrent,
    ];

    private static readonly Dictionary<string, ParameterDefinition> ByKey =
        All.ToDictionary(p => p.Key, StringComparer.Ordinal);

    private static readonly Dictionary<ushort, ParameterDefinition> ByAddress =
        All.ToDictionary(p => p.Address);

    public static ParameterDefinition? FindByKey(string key) => ByKey.GetValueOrDefault(key);

    public static ParameterDefinition? FindByAddress(ushort address) => ByAddress.GetValueOrDefault(address);
}
