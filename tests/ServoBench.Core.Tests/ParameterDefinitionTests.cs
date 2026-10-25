using System.Globalization;
using ServoBench.Core.Drive;
using Xunit;

namespace ServoBench.Core.Tests;

public class ParameterDefinitionTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [Fact]
    public void Scaled_value_converts_to_raw_and_back()
    {
        var limit = ParameterCatalog.CurrentLimit; // 0.1 A per count

        var raw = limit.ToRaw(7.5);

        Assert.Equal((ushort)75, raw);
        Assert.Equal(7.5, limit.FromRaw(raw));
    }

    [Fact]
    public void Negative_speed_uses_twos_complement()
    {
        var setpoint = ParameterCatalog.SpeedSetpoint;

        var raw = setpoint.ToRaw(-1500);

        Assert.Equal((ushort)0xFA24, raw);
        Assert.Equal(-1500, setpoint.FromRaw(raw));
    }

    [Fact]
    public void Values_outside_the_range_are_rejected_with_a_readable_message()
    {
        var error = ParameterCatalog.CurrentLimit.Validate(20);

        Assert.NotNull(error);
        Assert.Contains("between", error);
        Assert.Throws<ArgumentOutOfRangeException>(() => ParameterCatalog.CurrentLimit.ToRaw(20));
    }

    [Fact]
    public void Values_finer_than_the_resolution_are_rejected()
    {
        Assert.Contains("Resolution", ParameterCatalog.CurrentLimit.Validate(6.05));
    }

    [Fact]
    public void Boundary_values_survive_float_noise()
    {
        // 0.1 * 150 is 15.000000000000002 in binary floating point.
        Assert.Null(ParameterCatalog.CurrentLimit.Validate(0.1 * 150));
    }

    [Theory]
    [InlineData("1.25", true)]
    [InlineData(" 0.5 ", true)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    [InlineData("10.0001", false)]
    public void TryParse_accepts_only_valid_numbers(string text, bool expected)
    {
        var ok = ParameterCatalog.SpeedLoopKp.TryParse(text, Invariant, out _, out var error);

        Assert.Equal(expected, ok);
        Assert.Equal(expected, error is null);
    }

    [Fact]
    public void Parsing_respects_the_users_culture()
    {
        var german = CultureInfo.GetCultureInfo("de-DE");

        Assert.True(ParameterCatalog.CurrentLimit.TryParse("7,5", german, out var value, out _));
        Assert.Equal(7.5, value);
    }

    [Fact]
    public void Decimals_follow_the_scale()
    {
        Assert.Equal(0, ParameterCatalog.MaxSpeed.Decimals);
        Assert.Equal(1, ParameterCatalog.CurrentLimit.Decimals);
        Assert.Equal(2, ParameterCatalog.SpeedLoopKi.Decimals);
        Assert.Equal(3, ParameterCatalog.SpeedLoopKp.Decimals);
    }

    [Fact]
    public void Catalog_addresses_are_unique_and_inside_the_snapshot_block()
    {
        var addresses = ParameterCatalog.All.Select(p => p.Address).ToList();

        Assert.Equal(addresses.Count, addresses.Distinct().Count());
        Assert.True(addresses.All(a => a < DriveRegisterMap.SnapshotStart + DriveRegisterMap.SnapshotLength));
    }

    [Fact]
    public void Every_default_value_is_valid()
    {
        foreach (var parameter in ParameterCatalog.All)
        {
            Assert.Null(parameter.Validate(parameter.DefaultValue));
        }
    }
}
