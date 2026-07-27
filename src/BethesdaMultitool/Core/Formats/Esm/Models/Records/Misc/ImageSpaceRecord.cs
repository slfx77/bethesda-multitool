using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

/// <summary>
///     Image Space (IMGS) record. Defines the post-processing parameters (HDR, cinematic
///     color grading, tint, depth of field) applied to a cell or worldspace. Cells reference
///     an IMGS FormID via XCIM; missing the encoder means proto-only IMGS records are
///     stripped and any cell that references them falls back to the engine default — a
///     render-mismatch crash on cell entry for proto-only worldspaces.
/// </summary>
public record ImageSpaceRecord
{
    public uint FormId { get; init; }

    public string? EditorId { get; init; }

    /// <summary>
    ///     Classic FO3/FNV HDR parameters. Modern 36-byte HNAM records are deliberately not projected
    ///     through this ordinal contract; use <see cref="ModernHdr"/> for Skyrim/FO4-family records.
    /// </summary>
    public ImageSpaceHdr? Hdr { get; init; }

    /// <summary>Semantic Skyrim or FO4-family HNAM/legacy-ENAM data, without ordinal aliasing.</summary>
    public ImageSpaceModernHdr? ModernHdr { get; init; }

    /// <summary>Cinematic color grading (CNAM, 12 bytes / 3 floats). Optional.</summary>
    public ImageSpaceCinematic? Cinematic { get; init; }

    /// <summary>Tint color and amount (TNAM, 16 bytes / 4 floats). Optional.</summary>
    public ImageSpaceTint? Tint { get; init; }

    /// <summary>
    ///     Classic FO3/FNV fade color retained from DNAM. The 132-byte layout omits this block and
    ///     is semantically normalized to the recovered manager default (1,1,1,0).
    /// </summary>
    public ImageSpaceFade? Fade { get; init; }

    /// <summary>Depth-of-field parameters (DNAM, variable). Optional.</summary>
    public IReadOnlyList<float>? DepthOfField { get; init; }

    /// <summary>Lossless modern DNAM depth-of-field data. <see cref="DepthOfField"/> is its compatibility projection.</summary>
    public ImageSpaceDepthOfField? DepthOfFieldData { get; init; }

    /// <summary>FO4-family TX00 color-grading LUT path. Retained even when the renderer has no LUT binding.</summary>
    public string? LutTexturePath { get; init; }

    public long Offset { get; init; }

    public bool IsBigEndian { get; init; }
}

/// <summary>
///     Classic IMGS HDR values. DNAM carries fourteen common values plus SkinDimmer in the
///     152-byte layout; old 132/148-byte layouts normalize the omitted SkinDimmer to 1.
///     A split 36-byte compatibility HNAM supplies only the first nine values.
/// </summary>
public record ImageSpaceHdr
{
    public float EyeAdaptSpeed { get; init; }
    public float BlurRadius { get; init; }
    public float BlurPasses { get; init; }
    public float EmissiveMult { get; init; }
    public float TargetLum { get; init; }
    public float UpperLumClamp { get; init; }
    public float BrightScale { get; init; }
    public float BrightClamp { get; init; }
    public float LumRampNoTex { get; init; }
    public float LumRampMin { get; init; }
    public float LumRampMax { get; init; }
    public float SunlightDimmer { get; init; }
    public float GrassDimmer { get; init; }
    public float TreeDimmer { get; init; }
    public float SkinDimmer { get; init; }
}

/// <summary>
///     Semantic modern IMGS HDR block. The generated Skyrim and Fallout 4 schemas are authoritative:
///     positions 1/4/5/8 change meaning between the two families. Nullable fields were not present in
///     the legacy packed ENAM layout and therefore remain absent instead of receiving invented defaults.
/// </summary>
public sealed record ImageSpaceModernHdr
{
    public ImageSpaceModernFamily Family { get; init; }
    public bool IsLegacyPackedEnam { get; init; }
    public float EyeAdaptSpeed { get; init; }
    public float BloomThreshold { get; init; }
    public float BloomScale { get; init; }
    public float SunlightScale { get; init; }
    public float SkyScale { get; init; }

    // Skyrim semantics.
    public float? BloomBlurRadius { get; init; }
    public float? ReceiveBloomThreshold { get; init; }
    public float? White { get; init; }
    public float? EyeAdaptStrength { get; init; }

    // FO4/FO76 semantics. Packed ENAM has one combined Min/Max value, surfaced in both fields.
    public float? TonemapE { get; init; }
    public float? AutoExposureMax { get; init; }
    public float? AutoExposureMin { get; init; }
    public float? MiddleGray { get; init; }
}

/// <summary>Legacy packed modern ENAM: seven HDR values followed by cinematic and tint values.</summary>
public sealed record ImageSpacePackedData(
    ImageSpaceModernHdr Hdr,
    ImageSpaceCinematic Cinematic,
    ImageSpaceTint Tint);

/// <summary>
///     Modern IMGS DNAM. Bytes 12..13 are authored padding and bytes 14..15 are a U16 sky/blur
///     radius, so treating this payload as a float array loses data. FO4-family records append two
///     vignette floats. RawData preserves any future trailing bytes.
/// </summary>
public sealed record ImageSpaceDepthOfField
{
    public float Strength { get; init; }
    public float Distance { get; init; }
    public float Range { get; init; }
    public byte Unused0 { get; init; }
    public byte Unused1 { get; init; }
    public ushort SkyBlurRadius { get; init; }
    public float? VignetteRadius { get; init; }
    public float? VignetteStrength { get; init; }
    public byte[] RawData { get; init; } = [];
}

/// <summary>
///     IMGS cinematic block. Skyrim-style CNAM carries 3 floats (Saturation/Brightness/Contrast);
///     the FO3/FNV DNAM cinematic block additionally authors the contrast pivot ("Avg Lum Value",
///     applied as <c>Contrast·(Brightness·c − pivot) + pivot</c> in the engine's HDR blend-in shader).
/// </summary>
public record ImageSpaceCinematic
{
    /// <summary>
    ///     Whether the source layout actually stored the FO3/FNV mask dword. Classic 132/148/152-byte
    ///     DNAM records store it at layout-specific offsets; Creation-era CNAM blocks do not. The
    ///     shipped classic pixel shaders do not consume the mask.
    /// </summary>
    public bool HasExplicitFlags { get; init; } = true;

    /// <summary>
    ///     FO3/FNV cinematic enable mask: bit 0 Saturation, bit 1 Contrast, bit 2 Tint,
    ///     bit 3 Brightness. High bits in the source dword are non-semantic and are discarded.
    ///     Consult <see cref="HasExplicitFlags"/> because Skyrim-style CNAM has no stored mask.
    ///     This is source metadata, not a switch in the recovered shipped classic composite shader.
    /// </summary>
    public ImageSpaceCinematicFlags Flags { get; init; } = ImageSpaceCinematicFlags.All;

    public float Saturation { get; init; }
    public float Brightness { get; init; }
    public float Contrast { get; init; }

    /// <summary>Contrast pivot (FO3/FNV DNAM "Avg Lum Value"; 0.5 = neutral midpoint).</summary>
    public float ContrastAvgLum { get; init; } = 0.5f;
}

[Flags]
public enum ImageSpaceCinematicFlags : byte
{
    None = 0,
    Saturation = 1 << 0,
    Contrast = 1 << 1,
    Tint = 1 << 2,
    Brightness = 1 << 3,
    All = Saturation | Contrast | Tint | Brightness,
}

/// <summary>IMGS TNAM payload (16 bytes, 4 LE floats: Amount, Red, Green, Blue).</summary>
public record ImageSpaceTint
{
    public float Amount { get; init; }
    public float Red { get; init; }
    public float Green { get; init; }
    public float Blue { get; init; }
}

/// <summary>Classic DNAM fade block; absent old layouts carry the recovered neutral default.</summary>
public sealed record ImageSpaceFade
{
    public bool IsAuthored { get; init; }
    public float Red { get; init; } = 1f;
    public float Green { get; init; } = 1f;
    public float Blue { get; init; } = 1f;
    public float Amount { get; init; }
}

/// <summary>Semantic projection of one classic 132/148/152-byte IMGS DNAM payload.</summary>
public sealed record ImageSpaceClassicData(
    ImageSpaceHdr Hdr,
    ImageSpaceCinematic Cinematic,
    ImageSpaceTint Tint,
    ImageSpaceFade Fade);
