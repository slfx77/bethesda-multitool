using FalloutXbox360Utils.Core.Formats;

namespace FalloutXbox360Utils.Core.Formats.Nif.Conversion;

/// <summary>
///     Result of a NIF conversion operation. Extends the base ConversionResult
///     with NIF-specific metadata about source and output file info.
/// </summary>
internal sealed class NifConversionResult : ConversionResult
{
    /// <summary>
    ///     NIF-specific error message (maps to Notes in base class).
    /// </summary>
    public string? ErrorMessage
    {
        get => Notes;
        init => Notes = value;
    }

    /// <summary>
    ///     Information about the source NIF file.
    /// </summary>
    public NifInfo? SourceInfo { get; init; }

    /// <summary>
    ///     Information about the converted output NIF file.
    /// </summary>
    public NifInfo? OutputInfo { get; init; }
}
