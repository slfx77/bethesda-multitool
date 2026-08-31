namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     One Fallout 76 classic VOLI record. This is intentionally separate from Starfield's reflected
///     <see cref="StarfieldVolumetricLightingRecord" /> representation: the two games reuse the signature
///     for unrelated binary schemas.
/// </summary>
public sealed record Fallout76VolumetricLightingRecord
{
    /// <summary>VOLI FormID.</summary>
    public uint FormId { get; init; }

    /// <summary>Required EDID retained from the classic record envelope when it can be recovered.</summary>
    public string? EditorId { get; init; }

    /// <summary>
    ///     Strictly decoded classic settings. Null means the source bytes failed the proven Fallout 76
    ///     schema; <see cref="DecodeFailure" /> retains the reason so an invalid later override remains
    ///     authoritative and consumers cannot silently fall back to an earlier valid definition.
    /// </summary>
    public Fallout76VolumetricLightingSettings? Settings { get; init; }

    /// <summary>Strict schema/decode failure, or null for a valid definition.</summary>
    public string? DecodeFailure { get; init; }

    /// <summary>Offset in the source plugin where the VOLI record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the enclosing record was detected as big-endian.</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     Lossless typed projection of the classic Fallout 76 VOLI floats. Names and subrecord mappings
///     follow xEdit's FO76 definition. Values are retained exactly when finite; no renderer equation or
///     guessed range clamp is imposed here.
/// </summary>
public sealed record Fallout76VolumetricLightingSettings
{
    /// <summary>CNAM — intensity.</summary>
    public float Intensity { get; init; }

    /// <summary>DNAM — custom-color contribution.</summary>
    public float CustomColorContribution { get; init; }

    /// <summary>ENAM — red component of the authored RGB color.</summary>
    public float ColorRed { get; init; }

    /// <summary>FNAM — green component of the authored RGB color.</summary>
    public float ColorGreen { get; init; }

    /// <summary>GNAM — blue component of the authored RGB color.</summary>
    public float ColorBlue { get; init; }

    /// <summary>HNAM — density contribution.</summary>
    public float DensityContribution { get; init; }

    /// <summary>INAM — density size.</summary>
    public float DensitySize { get; init; }

    /// <summary>JNAM — density wind speed.</summary>
    public float DensityWindSpeed { get; init; }

    /// <summary>KNAM — density falling speed.</summary>
    public float DensityFallingSpeed { get; init; }

    /// <summary>
    ///     Optional LNAM — phase-function contribution. The field exists in the primary schema but is
    ///     absent from all 212 records in the audited retail SeventySix.esm.
    /// </summary>
    public float? PhaseFunctionContribution { get; init; }

    /// <summary>MNAM — phase-function scattering.</summary>
    public float PhaseFunctionScattering { get; init; }

    /// <summary>
    ///     NNAM — sampling-repartition range factor. Retail values reach 30..50, so this semantic layer
    ///     deliberately does not apply the stale xEdit comment suggesting a maximum of 1.
    /// </summary>
    public float SamplingRepartitionRangeFactor { get; init; }
}
