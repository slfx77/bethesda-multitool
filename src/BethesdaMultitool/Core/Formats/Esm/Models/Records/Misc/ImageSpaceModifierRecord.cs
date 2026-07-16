namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

/// <summary>
///     Image Space Modifier (IMAD) record. Every authored frame table is retained in source
///     order; <see cref="Parameters" /> is the semantic projection of the 21 recovered
///     multiply/add channels used by the classic imagespace manager. Scalar/color tables not
///     consumed by the current tonemap pass remain available losslessly for later effects work.
/// </summary>
public record ImageSpaceModifierRecord
{
    public uint FormId { get; init; }

    public string? EditorId { get; init; }

    /// <summary>IMAD DNAM payload, 244 bytes. See <see cref="ImageSpaceModifierData" /> for layout.</summary>
    public ImageSpaceModifierData? Data { get; init; }

    /// <summary>
    ///     The 21 recovered parameter pairs in engine ordinal order. Each parameter has an
    ///     absolute multiply curve (<c>\0IAD</c>..<c>\x14IAD</c>) and an add curve
    ///     (<c>@IAD</c>..<c>TIAD</c>).
    /// </summary>
    public IReadOnlyList<ImageSpaceModifierParameterTimeline> Parameters { get; init; } = [];

    /// <summary>Named scalar timelines (BNAM/VNAM/RNAM/SNAM/UNAM/NAM1/NAM2/WNAM/XNAM/YNAM/NAM4).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ImageSpaceModifierFloatKey>> ScalarTimelines { get; init; } =
        new Dictionary<string, IReadOnlyList<ImageSpaceModifierFloatKey>>(StringComparer.Ordinal);

    /// <summary>TNAM tint-color timeline (time + RGBA float).</summary>
    public IReadOnlyList<ImageSpaceModifierColorKey> TintColorTimeline { get; init; } = [];

    /// <summary>NAM3 fade-color timeline (time + RGBA float).</summary>
    public IReadOnlyList<ImageSpaceModifierColorKey> FadeColorTimeline { get; init; } = [];

    public uint? IntroSoundFormId { get; init; }
    public uint? OutroSoundFormId { get; init; }

    /// <summary>
    ///     Ordered raw subrecords, including unknown signatures and malformed/trailing bytes. This is
    ///     the lossless authority when a format variant is not represented by a semantic projection.
    /// </summary>
    public IReadOnlyList<ImageSpaceModifierRawSubrecord> OrderedSubrecords { get; init; } = [];

    public long Offset { get; init; }

    public bool IsBigEndian { get; init; }
}

/// <summary>
///     IMAD DNAM payload (244 bytes). Per
///     <see cref="BethesdaMultitool.Core.Formats.Esm.Conversion.Schema.SubrecordSchemaProcessor" />:
///     bytes 0..3 are uint32 already-LE on Xbox 360, bytes 4..243 are 60 little-endian
///     floats / uint32s that need byte-swapping on Xbox. The PC-output encoder simply
///     writes the canonical LE form.
/// </summary>
public record ImageSpaceModifierData
{
    /// <summary>Animatable flag (DNAM bytes 0..3, uint32).</summary>
    public uint AnimatableFlag { get; init; }

    /// <summary>Duration in seconds (DNAM bytes 4..7, float).</summary>
    public float Duration { get; init; }

    /// <summary>
    ///     Remaining payload (DNAM bytes 8..243, 59 × 4-byte values).
    ///     Each entry is either a uint32 count or a float (per fopdoc's per-slot
    ///     schema); the converter and encoder both treat them as little-endian
    ///     4-byte values without distinguishing — endian flips uniformly.
    /// </summary>
    public IReadOnlyList<uint> RawPayload { get; init; } = [];

    public bool IsAnimatable => AnimatableFlag != 0;
}

/// <summary>Recovered classic IMAD parameter ordinals (TESImageSpaceModifier::Apply[Weather]).</summary>
public enum ImageSpaceModifierParameter
{
    EyeAdaptSpeed = 0,
    HdrBlurRadius = 1,
    HdrSkinDimmer = 2,
    HdrEmissiveMult = 3,
    HdrTargetLum = 4,
    HdrUpperLumClamp = 5,
    HdrBrightScale = 6,
    HdrBrightClamp = 7,
    HdrLumRampNoTex = 8,
    HdrLumRampMin = 9,
    HdrLumRampMax = 10,
    HdrSunlightDimmer = 11,
    HdrGrassDimmer = 12,
    HdrTreeDimmer = 13,
    BloomBlurRadius = 14,
    BloomAlphaAddInterior = 15,
    BloomAlphaAddExterior = 16,
    CinematicSaturation = 17,
    CinematicContrastAvgLum = 18,
    CinematicContrast = 19,
    CinematicBrightness = 20,
}

public enum ImageSpaceModifierOperation
{
    Multiply = 0,
    Add = 1,
}

public readonly record struct ImageSpaceModifierFloatKey(float Time, float Value);

public readonly record struct ImageSpaceModifierColorKey(
    float Time, float Red, float Green, float Blue, float Alpha);

public sealed record ImageSpaceModifierParameterTimeline(
    ImageSpaceModifierParameter Parameter,
    IReadOnlyList<ImageSpaceModifierFloatKey> Multiply,
    IReadOnlyList<ImageSpaceModifierFloatKey> Add);

public sealed record ImageSpaceModifierRawSubrecord(string Signature, byte[] Data);
