namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Pipeline;

/// <summary>
///     Inputs for a DMP→ESP conversion run.
/// </summary>
public sealed record DmpToEspInputs
{
    /// <summary>Path to the Xbox 360 DMP file.</summary>
    public required string DmpPath { get; init; }

    /// <summary>
    ///     Path to the PC FalloutNV.esm master file. The DMP records will be overlaid on records
    ///     parsed from this file.
    /// </summary>
    public required string PcEsmPath { get; init; }

    /// <summary>Path where the output ESP plugin should be written.</summary>
    public required string OutputEspPath { get; init; }

    /// <summary>Conversion options (compression, validation, plugin metadata).</summary>
    public PluginBuildOptions Options { get; init; } = new();
}
