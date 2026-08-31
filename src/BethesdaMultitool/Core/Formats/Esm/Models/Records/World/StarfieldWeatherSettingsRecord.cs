namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     Identifies which version-4 reflection payload supplied a Starfield WTHS patch.
/// </summary>
public enum StarfieldWeatherSettingsPayloadKind
{
    /// <summary>No valid reflection payload kind was established.</summary>
    Unknown,

    /// <summary>A root <c>REFL</c>/<c>OBJT</c> payload containing a complete reflected object.</summary>
    FullObject,

    /// <summary>An <c>RDIF</c>/<c>DIFF</c> payload containing only indexed changes.</summary>
    Diff
}

/// <summary>
///     One Starfield Creation Engine 2 WTHS record. Reflection decoding is deliberately retained as
///     a nullable patch: malformed streams remain observable through <see cref="DecodeFailure" />
///     and can never masquerade as an authored all-zero weather.
/// </summary>
public sealed record StarfieldWeatherSettingsRecord
{
    /// <summary>WTHS FormID.</summary>
    public uint FormId { get; init; }

    /// <summary>EDID, when present.</summary>
    public string? EditorId { get; init; }

    /// <summary>
    ///     Parent WTHS FormID from RFDP. Null means RFDP was absent; zero means an explicit null
    ///     reference was authored. The handler cross-checks this against the reflected pParent patch.
    /// </summary>
    public uint? ParentFormId { get; init; }

    /// <summary>Whether the record carries a complete OBJT or an indexed DIFF.</summary>
    public StarfieldWeatherSettingsPayloadKind PayloadKind { get; init; }

    /// <summary>True only for the root OBJT representation.</summary>
    public bool IsFullDefinition => PayloadKind == StarfieldWeatherSettingsPayloadKind.FullObject;

    /// <summary>
    ///     Typed reflected values. Null when decoding or projection failed; inspect
    ///     <see cref="DecodeFailure" /> for the fail-closed reason.
    /// </summary>
    public StarfieldWeatherSettingsPatch? Patch { get; init; }

    /// <summary>Strict decode/projection failure, or null for a valid patch.</summary>
    public string? DecodeFailure { get; init; }

    /// <summary>Offset in the source plugin where the WTHS record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the enclosing record was detected as big-endian.</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     Lossless nullable projection of the WTHS fields whose reflection schemas are established.
///     Every nullable scalar distinguishes an absent DIFF member (null) from an explicitly authored
///     zero/false value. A non-null nested patch can itself contain only the changed components.
/// </summary>
public sealed record StarfieldWeatherSettingsPatch
{
    public uint? ParentFormId { get; init; }
    public uint? DisplayNameKeywordFormId { get; init; }
    public StarfieldWeatherChoicePatch? WeatherChoice { get; init; }
    public uint? ImageSpaceFormId { get; init; }
    public uint? ImageSpaceNightFormId { get; init; }
    public uint? VolumetricLightingFormId { get; init; }
    public uint? CloudsFormId { get; init; }
    public StarfieldWeatherColorSettingsPatch? Colors { get; init; }
    public uint? PrecipitationEffectFormId { get; init; }
    public uint? OptionalPhotoModeEffectFormId { get; init; }
    public uint? LensFlareFormId { get; init; }
    public float? LensFlareCloudOcclusionStrength { get; init; }
    public uint? WindForceFormId { get; init; }
    public StarfieldBlendableFloatPatch? WindDirectionRange { get; init; }
    public StarfieldBlendableFloatPatch? WindTurbulence { get; init; }
    public bool? WindDirectionOverrideEnabled { get; init; }
    public StarfieldBlendableFloatPatch? WindDirectionOverrideValue { get; init; }
    public float? TransDelta { get; init; }
    public StarfieldBlendableFloatPatch? VolatilityMultiplier { get; init; }
    public StarfieldBlendableFloatPatch? VisibilityMultiplier { get; init; }
}

/// <summary>Known scalar portion of the reflected WeatherChoice class.</summary>
public sealed record StarfieldWeatherChoicePatch
{
    public uint? Weight { get; init; }
}

/// <summary>The ten reflected weather color channels authored by BGSWeatherSettingsForm.</summary>
public sealed record StarfieldWeatherColorSettingsPatch
{
    public StarfieldBlendableColorPatch? EffectLighting { get; init; }
    public StarfieldBlendableColorPatch? FogFar { get; init; }
    public StarfieldBlendableColorPatch? FogFarHigh { get; init; }
    public StarfieldBlendableColorPatch? FogNear { get; init; }
    public StarfieldBlendableColorPatch? FogNearHigh { get; init; }
    public StarfieldBlendableColorPatch? Sun { get; init; }
    public StarfieldBlendableColorPatch? SunGlare { get; init; }
    public StarfieldBlendableColorPatch? Sunlight { get; init; }
    public StarfieldBlendableColorPatch? MoonGlare { get; init; }
    public StarfieldBlendableColorPatch? Moonlight { get; init; }
}

/// <summary>A partial BSBlendable::ColorValue reflected object.</summary>
public sealed record StarfieldBlendableColorPatch
{
    public string? Operation { get; init; }
    public StarfieldFloat4Patch? Value { get; init; }
    public float? BlendAmount { get; init; }
}

/// <summary>A partial BSBlendable::FloatValue reflected object.</summary>
public sealed record StarfieldBlendableFloatPatch
{
    public string? Operation { get; init; }
    public float? Value { get; init; }
    public float? BlendAmount { get; init; }
}

/// <summary>
///     A partial XMFLOAT4. Nullable components are required because a nested DIFF may replace only one
///     color channel; zero is an authored value and must not be used as the absence sentinel.
/// </summary>
public sealed record StarfieldFloat4Patch
{
    public float? X { get; init; }
    public float? Y { get; init; }
    public float? Z { get; init; }
    public float? W { get; init; }
}
