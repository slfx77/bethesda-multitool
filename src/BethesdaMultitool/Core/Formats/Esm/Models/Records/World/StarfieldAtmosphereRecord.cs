namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

/// <summary>Identifies which version-4 reflection payload supplied a Starfield ATMO patch.</summary>
public enum StarfieldAtmospherePayloadKind
{
    /// <summary>No valid reflection payload kind was established.</summary>
    Unknown,

    /// <summary>A root <c>REFL</c>/<c>OBJT</c> payload containing a complete reflected object.</summary>
    FullObject,

    /// <summary>An <c>RDIF</c>/<c>DIFF</c> payload containing only indexed changes.</summary>
    Diff
}

/// <summary>
///     One Starfield Creation Engine 2 ATMO record. This envelope deliberately retains only the
///     proven inheritance and preset references; it does not infer atmospheric scattering or
///     rendering equations from the reflection schema. The bounded retail inventory contains 594
///     records: two EDID+REFL roots and 592 EDID+RFDP+RDIF overlays.
/// </summary>
public sealed record StarfieldAtmosphereRecord
{
    /// <summary>ATMO FormID.</summary>
    public uint FormId { get; init; }

    /// <summary>EDID, when present.</summary>
    public string? EditorId { get; init; }

    /// <summary>
    ///     Parent ATMO FormID from RFDP. Null means RFDP was absent; zero means an explicitly
    ///     authored null outer reference. Resolution requires a nonzero value for DIFF records.
    /// </summary>
    public uint? ParentFormId { get; init; }

    /// <summary>Whether the record carries a complete OBJT or an indexed DIFF.</summary>
    public StarfieldAtmospherePayloadKind PayloadKind { get; init; }

    /// <summary>True only for the root OBJT representation.</summary>
    public bool IsFullDefinition => PayloadKind == StarfieldAtmospherePayloadKind.FullObject;

    /// <summary>
    ///     Strictly projected structural references. Null when decoding or projection failed;
    ///     inspect <see cref="DecodeFailure" /> for the fail-closed reason.
    /// </summary>
    public StarfieldAtmospherePatch? Patch { get; init; }

    /// <summary>Strict decode/projection failure, or null for a valid patch.</summary>
    public string? DecodeFailure { get; init; }

    /// <summary>Offset in the source plugin where the ATMO record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the enclosing record was detected as big-endian.</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     Lossless nullable projection of the three proven ATMO structural references. Null means a
///     DIFF omitted that member and therefore inherits it; zero is an explicitly authored null
///     reference and must replace an inherited nonzero value.
/// </summary>
public sealed record StarfieldAtmospherePatch
{
    /// <summary><c>Settings.pParent</c>.</summary>
    public uint? ParentFormId { get; init; }

    /// <summary><c>Settings.Overrides.pSunPresetOverride</c>.</summary>
    public uint? SunPresetOverrideFormId { get; init; }

    /// <summary><c>Settings.Misc.pClimateOverride</c>.</summary>
    public uint? ClimateOverrideFormId { get; init; }
}
