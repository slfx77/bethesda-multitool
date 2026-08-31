namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     Standalone representation of one Starfield CLDF. Retail Starfield authors CLDF with a
///     complete REFL object only: the record has no RFDP/RDIF alternative and
///     <c>BGSCloudForm</c> has no reflected parent member.
/// </summary>
public sealed record StarfieldCloudFormRecord
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }

    /// <summary>
    ///     Strictly decoded reflected definition. Null means decoding or typed projection failed;
    ///     <see cref="DecodeFailure" /> retains the fail-closed reason.
    /// </summary>
    public StarfieldCloudFormDefinition? Definition { get; init; }

    public string? DecodeFailure { get; init; }
    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }
}

/// <summary>The complete proven retail <c>BGSCloudForm</c> reflection schema.</summary>
public sealed record StarfieldCloudFormDefinition(
    StarfieldCloudShadowParams Shadows,
    IReadOnlyList<StarfieldCloudLayer> Layers,
    IReadOnlyList<StarfieldCloudPlane> Planes,
    uint CloudCardSequenceFormId);

/// <summary>The complete proven <c>BGSCloudForm::ShadowParams</c> shape.</summary>
public sealed record StarfieldCloudShadowParams(
    bool Enabled,
    string OpacityTexture,
    float TilingPerKm,
    float ElevationKm,
    float Strength,
    float WindScale);

/// <summary>The complete proven <c>BGSCloudForm::CloudLayer</c> shape.</summary>
public sealed record StarfieldCloudLayer(
    string Name,
    string ColorTexture,
    string ThicknessTexture,
    string NormalTexture,
    string OpacityTexture,
    float ElevationKm,
    float HeightKm,
    float DistanceKm,
    float Thickness,
    float TextureShadowOffset,
    float TextureShadowStrength,
    float NormalShadowStrength,
    uint Tiling,
    uint VerticalTiling,
    float TopBlendDistanceKm,
    float TopBlendStartKm,
    float BottomBlendDistanceKm,
    float BottomBlendStartKm,
    float WindScale,
    float Density,
    float Coverage,
    float AlphaAdd,
    float AlphaMultiply,
    StarfieldCloudTint Tint);

/// <summary>The complete proven <c>BGSCloudForm::CloudPlane</c> shape.</summary>
public sealed record StarfieldCloudPlane(
    string Name,
    string ColorTexture,
    string ThicknessTexture,
    string NormalTexture,
    string OpacityTexture,
    float ElevationKm,
    float FadeStartKm,
    float FadeDistanceKm,
    float Thickness,
    float TextureShadowOffset,
    float TextureShadowStrength,
    float NormalShadowStrength,
    float TilingPerKm,
    float WindScale,
    float Density,
    float Coverage,
    float AlphaAdd,
    float AlphaMultiply,
    StarfieldCloudTint Tint);

/// <summary>Retail <c>XMCOLOR</c> in its exact UInt8 channel order: r, g, b, a.</summary>
public sealed record StarfieldCloudTint(byte R, byte G, byte B, byte A);
