using FalloutXbox360Utils.Core.Formats.Esm.Reporting;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin;

/// <summary>
///     Outcome of a DMP→ESP conversion run.
/// </summary>
public sealed record PluginBuildResult
{
    public required bool Success { get; init; }
    public required ConversionPipelineStats Stats { get; init; }

    /// <summary>Path of the written ESP, or null if the run failed before write.</summary>
    public string? OutputPath { get; init; }

    /// <summary>Error message, set when <see cref="Success" /> is false.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Validation report, set when <see cref="PluginBuildOptions.ValidateOutput" /> was true.</summary>
    public string? ValidationReport { get; init; }
}
