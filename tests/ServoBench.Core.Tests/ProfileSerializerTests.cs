using ServoBench.Core.Drive;
using ServoBench.Core.Profiles;
using Xunit;

namespace ServoBench.Core.Tests;

public class ProfileSerializerTests
{
    private static readonly DateTimeOffset SavedAt = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, double> DriveValues() => ParameterCatalog.All
        .ToDictionary(p => p.Key, p => p.DefaultValue);

    [Fact]
    public void Create_keeps_only_persistent_configuration()
    {
        var profile = ProfileSerializer.Create("Conveyor", DriveValues(), SavedAt);

        Assert.DoesNotContain(ParameterCatalog.SpeedSetpoint.Key, string.Join(",", profile.Parameters.Keys));
        Assert.DoesNotContain(ParameterCatalog.RatedSpeed.Key, string.Join(",", profile.Parameters.Keys));
        Assert.Contains(ParameterCatalog.CurrentLimit.Key, profile.Parameters.Keys);
    }

    [Fact]
    public void Profile_round_trips_through_json()
    {
        var values = DriveValues();
        values[ParameterCatalog.CurrentLimit.Key] = 8.5;
        var profile = ProfileSerializer.Create("Conveyor", values, SavedAt);

        var result = ProfileSerializer.Deserialize(ProfileSerializer.Serialize(profile));

        Assert.Equal("Conveyor", result.Profile.Name);
        Assert.Equal(SavedAt, result.Profile.SavedAt);
        Assert.Equal(8.5, result.ApplicableValues[ParameterCatalog.CurrentLimit]);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Json_lists_parameters_in_catalog_order()
    {
        var json = ProfileSerializer.Serialize(ProfileSerializer.Create("Ordered", DriveValues(), SavedAt));

        Assert.True(json.IndexOf("motion.accelerationTime", StringComparison.Ordinal) < json.IndexOf("tuning.speedKi", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_read_only_and_out_of_range_entries_are_skipped_with_warnings()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "name": "Hand edited",
              "savedAt": "2026-10-25T09:00:00+00:00",
              "parameters": {
                "limits.currentLimit": 99,
                "nameplate.ratedSpeed": 1000,
                "made.up": 1,
                "tuning.speedKp": 1.2
              }
            }
            """;

        var result = ProfileSerializer.Deserialize(json);

        Assert.Equal(1.2, Assert.Single(result.ApplicableValues).Value);
        Assert.Equal(3, result.Warnings.Count);
    }

    [Fact]
    public void Unsupported_schema_version_is_refused()
    {
        const string json = """{ "schemaVersion": 99, "name": "Future", "parameters": {} }""";

        Assert.Throws<ProfileFormatException>(() => ProfileSerializer.Deserialize(json));
    }

    [Fact]
    public void Malformed_json_is_reported_as_a_profile_format_error()
    {
        Assert.Throws<ProfileFormatException>(() => ProfileSerializer.Deserialize("{ not json"));
    }
}
