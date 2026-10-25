using System.Globalization;

namespace ServoBench.Core.Drive;

public enum ParameterAccess
{
    ReadOnly,
    ReadWrite,
}

/// <summary>
/// Describes one drive parameter: where it lives in the register map, how raw register
/// values scale to engineering units, and which values the drive accepts.
/// </summary>
public sealed record ParameterDefinition
{
    public required string Key { get; init; }

    public required string Name { get; init; }

    public required string Group { get; init; }

    public required ushort Address { get; init; }

    public string Unit { get; init; } = string.Empty;

    /// <summary>Engineering value = raw register value x Scale.</summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>Interpret the 16-bit register as two's complement.</summary>
    public bool IsSigned { get; init; }

    public required double Minimum { get; init; }

    public required double Maximum { get; init; }

    public double DefaultValue { get; init; }

    public ParameterAccess Access { get; init; } = ParameterAccess.ReadWrite;

    /// <summary>Whether the value belongs in a saved configuration profile. Live commands such as
    /// the speed setpoint are writable but are not configuration.</summary>
    public bool IsPersistent { get; init; } = true;

    public string Description { get; init; } = string.Empty;

    public bool IsWritable => Access == ParameterAccess.ReadWrite;

    /// <summary>Number of decimals implied by the scale (0.1 → 1, 0.001 → 3).</summary>
    public int Decimals => Scale >= 1 ? 0 : (int)Math.Ceiling(-Math.Log10(Scale) - 1e-9);

    public double FromRaw(ushort raw)
    {
        var value = IsSigned ? unchecked((short)raw) : (int)raw;
        return Math.Round(value * Scale, Decimals);
    }

    /// <summary>Converts an engineering value to its register value. Throws if the value is not allowed.</summary>
    public ushort ToRaw(double value)
    {
        var error = Validate(value);
        if (error is not null)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, error);
        }

        var scaled = (int)Math.Round(value / Scale, MidpointRounding.AwayFromZero);
        return IsSigned ? unchecked((ushort)(short)scaled) : (ushort)scaled;
    }

    /// <summary>Returns a user-facing error message, or null when the value is acceptable.</summary>
    public string? Validate(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return "Enter a number.";
        }

        // A tiny tolerance so binary float noise (e.g. 0.1 * 150) does not reject a boundary value.
        var tolerance = Scale * 1e-6;
        if (value < Minimum - tolerance || value > Maximum + tolerance)
        {
            return $"Must be between {Format(Minimum, CultureInfo.CurrentCulture)} and {Format(Maximum, CultureInfo.CurrentCulture)}{UnitSuffix}.";
        }

        var steps = value / Scale;
        if (Math.Abs(steps - Math.Round(steps)) > 1e-6)
        {
            return $"Resolution is {Format(Scale, CultureInfo.CurrentCulture)}{UnitSuffix}.";
        }

        return null;
    }

    /// <summary>Parses text typed by a user using the given culture.</summary>
    public bool TryParse(string? text, IFormatProvider? culture, out double value, out string? error)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "A value is required.";
            return false;
        }

        if (!double.TryParse(text.Trim(), NumberStyles.Float, culture ?? CultureInfo.CurrentCulture, out value))
        {
            error = "Enter a number.";
            return false;
        }

        error = Validate(value);
        return error is null;
    }

    public string Format(double value, IFormatProvider? culture = null) =>
        value.ToString("F" + Decimals.ToString(CultureInfo.InvariantCulture), culture ?? CultureInfo.CurrentCulture);

    private string UnitSuffix => string.IsNullOrEmpty(Unit) ? string.Empty : " " + Unit;
}
