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

    /// <summary>HDR parameters (HNAM, 36 bytes / 9 floats). Optional.</summary>
    public ImageSpaceHdr? Hdr { get; init; }

    /// <summary>Cinematic color grading (CNAM, 12 bytes / 3 floats). Optional.</summary>
    public ImageSpaceCinematic? Cinematic { get; init; }

    /// <summary>Tint color and amount (TNAM, 16 bytes / 4 floats). Optional.</summary>
    public ImageSpaceTint? Tint { get; init; }

    /// <summary>Depth-of-field parameters (DNAM, variable). Optional.</summary>
    public IReadOnlyList<float>? DepthOfField { get; init; }

    public long Offset { get; init; }

    public bool IsBigEndian { get; init; }
}

/// <summary>IMGS HNAM payload (36 bytes, 9 LE floats).</summary>
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
}

/// <summary>
///     IMGS cinematic block. Skyrim-style CNAM carries 3 floats (Saturation/Brightness/Contrast);
///     the FO3/FNV DNAM cinematic block additionally authors the contrast pivot ("Avg Lum Value",
///     applied as <c>Brightness·(Contrast·c − pivot) + pivot</c> in the engine's HDR blend-in shader).
/// </summary>
public record ImageSpaceCinematic
{
    public float Saturation { get; init; }
    public float Brightness { get; init; }
    public float Contrast { get; init; }

    /// <summary>Contrast pivot (FO3/FNV DNAM "Avg Lum Value"; 0.5 = neutral midpoint).</summary>
    public float ContrastAvgLum { get; init; } = 0.5f;
}

/// <summary>IMGS TNAM payload (16 bytes, 4 LE floats: Amount, Red, Green, Blue).</summary>
public record ImageSpaceTint
{
    public float Amount { get; init; }
    public float Red { get; init; }
    public float Green { get; init; }
    public float Blue { get; init; }
}
