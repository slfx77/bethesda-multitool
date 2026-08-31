namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

/// <summary>Identifies the reflected payload representation carried by a Starfield SUNP record.</summary>
public enum StarfieldSunPresetPayloadKind
{
    /// <summary>No supported reflection payload was established.</summary>
    Unknown,

    /// <summary>A root <c>REFL</c>/<c>OBJT</c> payload containing every authored field.</summary>
    FullObject,

    /// <summary>An <c>RDIF</c>/<c>DIFF</c> payload containing only indexed changes.</summary>
    Diff
}

/// <summary>
///     One Starfield Creation Engine 2 SUNP record. The bounded retail inventory contains 52
///     records: seven complete roots and 45 one-edge diffs. This envelope preserves the source and
///     failure state without treating an invalid reflection stream as an authored zero preset.
/// </summary>
public sealed record StarfieldSunPresetRecord
{
    /// <summary>SUNP FormID.</summary>
    public uint FormId { get; init; }

    /// <summary>EDID, when present.</summary>
    public string? EditorId { get; init; }

    /// <summary>
    ///     Parent SUNP FormID from RFDP. Null means RFDP was absent; zero means an explicitly
    ///     authored null outer reference. Resolution requires a nonzero value for a DIFF record.
    /// </summary>
    public uint? ParentFormId { get; init; }

    /// <summary>Whether the source carries a complete OBJT or an indexed DIFF.</summary>
    public StarfieldSunPresetPayloadKind PayloadKind { get; init; }

    /// <summary>True only for the complete OBJT representation.</summary>
    public bool IsFullDefinition => PayloadKind == StarfieldSunPresetPayloadKind.FullObject;

    /// <summary>
    ///     Lossless typed projection. Null means decoding or schema projection failed; inspect
    ///     <see cref="DecodeFailure" /> for the fail-closed reason.
    /// </summary>
    public StarfieldSunPresetPatch? Patch { get; init; }

    /// <summary>Strict decode/projection failure, or null for a valid patch.</summary>
    public string? DecodeFailure { get; init; }

    /// <summary>Offset in the source plugin where the SUNP record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the enclosing record was detected as big-endian.</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     Nullable projection of the exact retail <c>BSGalaxy::BGSSunPresetForm</c> fields. Null means
///     a DIFF omitted the member; zero, an empty string, and zero color components remain authored
///     replacements. Only <see cref="ParentFormId" /> is a FormID. Every other numeric member is a
///     scalar and must never participate in load-order rebasing.
/// </summary>
public sealed record StarfieldSunPresetPatch
{
    /// <summary>Reflected <c>pParent: Ref&lt;UInt32&gt;</c>; this is the sole FormID in the patch.</summary>
    public uint? ParentFormId { get; init; }

    public StarfieldSunPresetFloat4Patch? SunColor { get; init; }
    public float? SunIlluminance { get; init; }
    public StarfieldSunPresetFloat4Patch? SunGlareColor { get; init; }
    public string? SunDiskTexture { get; init; }
    public float? SunDiskScreenSizeMin { get; init; }
    public float? SunDiskScreenSizeMax { get; init; }
    public StarfieldSunPresetDawnDuskPatch? DuskDawnPreset { get; init; }
    public StarfieldSunPresetNightPatch? NightPreset { get; init; }
}

/// <summary>A partial reflected <c>XMFLOAT4</c>; nullable channels retain nested DIFF semantics.</summary>
public sealed record StarfieldSunPresetFloat4Patch
{
    public float? X { get; init; }
    public float? Y { get; init; }
    public float? Z { get; init; }
    public float? W { get; init; }
}

/// <summary>Nullable projection of the exact reflected dawn/dusk transition settings.</summary>
public sealed record StarfieldSunPresetDawnDuskPatch
{
    public StarfieldSunPresetFloat4Patch? DirectionalColor { get; init; }
    public float? TransitionStartAngle { get; init; }
    public float? TransitionEndAngle { get; init; }
}

/// <summary>Nullable projection of the exact reflected night directional and glare settings.</summary>
public sealed record StarfieldSunPresetNightPatch
{
    public StarfieldSunPresetFloat4Patch? DirectionalColor { get; init; }
    public float? DirectionalIlluminance { get; init; }
    public StarfieldSunPresetFloat4Patch? GlareColor { get; init; }
}
