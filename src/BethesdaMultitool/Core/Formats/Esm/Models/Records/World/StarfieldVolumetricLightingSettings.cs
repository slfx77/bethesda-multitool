namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     One Starfield VOLI record. A malformed later load-order override remains represented with
///     null <see cref="Settings" /> and a non-null <see cref="DecodeFailure" />, so consumers can
///     fail closed instead of falling back to an earlier valid definition.
/// </summary>
public sealed record StarfieldVolumetricLightingRecord
{
    /// <summary>VOLI FormID.</summary>
    public uint FormId { get; init; }

    /// <summary>EDID, when present.</summary>
    public string? EditorId { get; init; }

    /// <summary>
    ///     Strictly decoded reflected definition. Null means outer framing, reflection decoding,
    ///     or typed projection failed; <see cref="DecodeFailure" /> retains the reason.
    /// </summary>
    public StarfieldVolumetricLightingSettings? Settings { get; init; }

    /// <summary>Strict decode/projection failure, or null for a valid definition.</summary>
    public string? DecodeFailure { get; init; }

    /// <summary>Offset in the source plugin where the VOLI record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the enclosing record was detected as big-endian.</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     Complete typed projection of the Starfield <c>BGSVolumetricLighting</c> reflection object.
///     Retail VOLI records author a full REFL/OBJT and do not use RFDP/RDIF inheritance, so every
///     value is required and retained without clamping or semantic reinterpretation.
/// </summary>
public sealed record StarfieldVolumetricLightingSettings(
    StarfieldVolumetricExteriorAndInteriorSettings ExteriorAndInterior,
    StarfieldVolumetricExteriorSettings Exterior,
    StarfieldVolumetricDistantLightingSettings DistantLighting);

/// <summary>Parameters shared by exterior and interior volumetric-lighting evaluation.</summary>
public sealed record StarfieldVolumetricExteriorAndInteriorSettings(
    float ScatteringVolumeNear,
    float ScatteringVolumeFar,
    float HighFrequencyNoiseScale,
    float HighFrequencyNoiseDensityScale);

/// <summary>The four exterior fog parameter groups authored by a VOLI record.</summary>
public sealed record StarfieldVolumetricExteriorSettings(
    StarfieldVolumetricFogThicknessSettings FogThickness,
    StarfieldVolumetricFogDensitySettings FogDensity,
    StarfieldVolumetricHorizonFogSettings HorizonFog,
    StarfieldVolumetricFogMapSettings FogMap);

/// <summary>Noise and bounds for the authored exterior fog thickness.</summary>
public sealed record StarfieldVolumetricFogThicknessSettings(
    float ThicknessNoiseScale,
    float ThicknessNoiseBias,
    float MinFogThickness,
    float MaxFogThickness);

/// <summary>Noise, bounds, and distance ramp for the authored exterior fog density.</summary>
public sealed record StarfieldVolumetricFogDensitySettings(
    float DensityNoiseScale,
    float DensityNoiseBias,
    float MinFogDensity,
    float MaxFogDensity,
    float DensityStartDistance,
    float DensityFullDistance,
    float DensityDistanceExponent);

/// <summary>Thickness, density, and distance ramp for the horizon fog layer.</summary>
public sealed record StarfieldVolumetricHorizonFogSettings(
    float FogThickness,
    float FogDensity,
    float DensityStartDistance,
    float DensityFullDistance);

/// <summary>Terrain-relative fog-map optical and height parameters.</summary>
public sealed record StarfieldVolumetricFogMapSettings(
    float HeightAboveTerrain,
    float TerrainMatch,
    StarfieldVolumetricFloat4 Albedo,
    float Anisotropy,
    float MinMeanFreePath,
    float MaxMeanFreePath,
    float HeightFalloffExponent,
    float Span);

/// <summary>Far-field volumetric scattering transition parameters.</summary>
public sealed record StarfieldVolumetricDistantLightingSettings(
    float ScatteringTransition,
    float ScatteringFar);

/// <summary>An exact four-float value from the reflected <c>XMFLOAT4</c> class.</summary>
public sealed record StarfieldVolumetricFloat4(float X, float Y, float Z, float W);
