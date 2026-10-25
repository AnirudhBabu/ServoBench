using System.Text.Json;
using System.Text.Json.Serialization;
using ServoBench.Core.Drive;

namespace ServoBench.Core.Profiles;

/// <summary>A named set of configuration values that can be saved, shared and loaded onto a drive.</summary>
public sealed record DriveProfile
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string Name { get; init; }

    public DateTimeOffset SavedAt { get; init; }

    /// <summary>Engineering values keyed by <see cref="ParameterDefinition.Key"/>.</summary>
    public required IReadOnlyDictionary<string, double> Parameters { get; init; }
}

/// <summary>Result of loading a profile: values safe to apply, plus anything that was skipped and why.</summary>
public sealed record ProfileLoadResult(
    DriveProfile Profile,
    IReadOnlyDictionary<ParameterDefinition, double> ApplicableValues,
    IReadOnlyList<string> Warnings);

public sealed class ProfileFormatException : Exception
{
    public ProfileFormatException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

public static class ProfileSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Builds a profile from current values, keeping only persistent writable parameters.</summary>
    public static DriveProfile Create(string name, IReadOnlyDictionary<string, double> values, DateTimeOffset savedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(values);

        var persisted = ParameterCatalog.All
            .Where(p => p.IsWritable && p.IsPersistent && values.ContainsKey(p.Key))
            .ToDictionary(p => p.Key, p => values[p.Key], StringComparer.Ordinal);

        return new DriveProfile { Name = name.Trim(), SavedAt = savedAt, Parameters = persisted };
    }

    public static string Serialize(DriveProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // Emit keys in catalog order so diffs between saved profiles stay readable.
        var ordered = new SortedDictionary<string, double>(
            profile.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value),
            Comparer<string>.Create(CompareByCatalogOrder));

        return JsonSerializer.Serialize(profile with { Parameters = ordered }, Options);
    }

    public static ProfileLoadResult Deserialize(string json)
    {
        DriveProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize<DriveProfile>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new ProfileFormatException($"Profile is not valid JSON: {ex.Message}", ex);
        }

        if (profile is null || profile.Parameters is null)
        {
            throw new ProfileFormatException("Profile is empty.");
        }

        if (profile.SchemaVersion != DriveProfile.CurrentSchemaVersion)
        {
            throw new ProfileFormatException(
                $"Profile schema version {profile.SchemaVersion} is not supported (expected {DriveProfile.CurrentSchemaVersion}).");
        }

        var applicable = new Dictionary<ParameterDefinition, double>();
        var warnings = new List<string>();

        foreach (var (key, value) in profile.Parameters)
        {
            var parameter = ParameterCatalog.FindByKey(key);
            if (parameter is null)
            {
                warnings.Add($"Skipped unknown parameter '{key}'.");
                continue;
            }

            if (!parameter.IsWritable || !parameter.IsPersistent)
            {
                warnings.Add($"Skipped '{parameter.Name}': it is not a configuration parameter.");
                continue;
            }

            var error = parameter.Validate(value);
            if (error is not null)
            {
                warnings.Add($"Skipped '{parameter.Name}' = {value}: {error}");
                continue;
            }

            applicable[parameter] = value;
        }

        return new ProfileLoadResult(profile, applicable, warnings);
    }

    private static int CompareByCatalogOrder(string? a, string? b)
    {
        static int IndexOf(string? key)
        {
            for (var i = 0; i < ParameterCatalog.All.Count; i++)
            {
                if (ParameterCatalog.All[i].Key == key)
                {
                    return i;
                }
            }

            return int.MaxValue;
        }

        var byCatalog = IndexOf(a).CompareTo(IndexOf(b));
        return byCatalog != 0 ? byCatalog : string.CompareOrdinal(a, b);
    }
}
