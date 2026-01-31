using FalloutXbox360Utils.Core.Formats.EsmRecord.Enums;

namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Detected asset path string from runtime string pools.
///     These are found in contiguous string regions (not ESM subrecords).
/// </summary>
public record DetectedAssetString
{
    /// <summary>The asset path string (e.g., "meshes\weapons\pistol10mm.nif").</summary>
    public required string Path { get; init; }

    /// <summary>Offset in the dump where string was found.</summary>
    public long Offset { get; init; }

    /// <summary>Category based on file extension.</summary>
    public AssetCategory Category { get; init; }
}
