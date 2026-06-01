namespace FalloutXbox360Utils.Core.Formats.Esm.Models.World;

/// <summary>
///     Provenance of LAND visual data (VCLR / BTXT / ATXT / VTXT / VTEX).
///     Tracked per-field on <see cref="LandVisualData" /> so downstream tooling (converter
///     encoders, GUI cell tooltip, diagnostic CSV exports) can distinguish vanilla bytes
///     from runtime memory extracts.
/// </summary>
public enum VisualDataSource
{
    /// <summary>No data present for this field.</summary>
    None = 0,

    /// <summary>Parsed from raw DMP bytes (big-endian Xbox 360 ESM living inside the dump).</summary>
    Dmp = 1,

    /// <summary>Extracted from DMP live game state via <c>RuntimeLandVisualReader</c>.</summary>
    Runtime = 2,

    /// <summary>Parsed from master ESM bytes (vanilla little-endian PC plugin).</summary>
    MasterEsm = 3,

    /// <summary>Aggregate value when sibling fields came from different sources.</summary>
    Merged = 4
}
