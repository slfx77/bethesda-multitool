namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;

/// <summary>
///     Inputs for a DMP→ESM conversion run.
/// </summary>
public sealed record DmpToEsmInputs
{
    /// <summary>Path to the Xbox 360 DMP file.</summary>
    public required string DmpPath { get; init; }

    /// <summary>
    ///     Path to the PC FalloutNV.esm master file. The DMP records will be overlaid on records
    ///     parsed from this file.
    /// </summary>
    public required string PcEsmPath { get; init; }

    /// <summary>Path where the output ESM plugin should be written.</summary>
    public required string OutputEsmPath { get; init; }

    /// <summary>Conversion options (compression, validation, plugin metadata).</summary>
    public PluginBuildOptions Options { get; init; } = new();
}
