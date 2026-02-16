namespace FalloutXbox360Utils.Core.VersionTracking.Models;

/// <summary>
///     Identifies a single data source (ESM file or memory dump) in the build timeline.
/// </summary>
public record BuildInfo
{
    /// <summary>Human-readable label (e.g., "July 21, 2010", "Release Beta DMP #1").</summary>
    public required string Label { get; init; }

    /// <summary>Full path to the ESM or DMP file.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Whether this source is an ESM file or memory dump.</summary>
    public required BuildSourceType SourceType { get; init; }

    /// <summary>Build compilation date from PE TimeDateStamp.</summary>
    public DateTimeOffset? BuildDate { get; init; }

    /// <summary>Build type classification (e.g., "Debug", "Release Beta").</summary>
    public string? BuildType { get; init; }

    /// <summary>Raw PE TimeDateStamp value, used for grouping DMPs with same build.</summary>
    public uint? PeTimestamp { get; init; }

    /// <summary>Whether this source is authoritative (ESM = true, DMP = false).</summary>
    public bool IsAuthoritative => SourceType == BuildSourceType.Esm;
}

/// <summary>
///     Type of data source for version tracking.
/// </summary>
public enum BuildSourceType
{
    Esm,
    Dmp
}
