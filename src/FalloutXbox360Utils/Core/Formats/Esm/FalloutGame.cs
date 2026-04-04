namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Identifies which Fallout game an ESM file belongs to.
///     Detected from the HEDR version float in the TES4 record header.
/// </summary>
public enum FalloutGame
{
    /// <summary>Unknown or undetected game version.</summary>
    Unknown = 0,

    /// <summary>Fallout 3 (HEDR version 0.94).</summary>
    Fallout3,

    /// <summary>Fallout: New Vegas (HEDR version 1.32–1.35).</summary>
    FalloutNewVegas
}
