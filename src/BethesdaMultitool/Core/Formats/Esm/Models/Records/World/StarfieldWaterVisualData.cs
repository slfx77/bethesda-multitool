namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     Starfield WATR FNAM flags from the Creation Engine 2 record definition. Undefined bits are
///     preserved by the underlying byte when the enum is populated.
/// </summary>
[Flags]
public enum StarfieldWaterFlags : byte
{
    None = 0,
    Dangerous = 1 << 0,
    DirectionalSound = 1 << 2,
    EnableFlowmap = 1 << 3,
    BlendNormals = 1 << 4
}

/// <summary>
///     One Starfield DNAM noise layer. This is intentionally separate from the renderer-facing
///     classic <see cref="WaterNoiseLayer" /> contract: ingestion preserves CE2 field semantics
///     without implying that Starfield uses the recovered FNV scrolling-normal equation.
/// </summary>
public sealed record StarfieldWaterNoiseLayer
{
    public float WindDirection { get; init; }

    public float WindSpeed { get; init; }

    public float AmplitudeScale { get; init; }

    public float UvScale { get; init; }

    public float NoiseFalloff { get; init; }
}

/// <summary>
///     Exact three-dword preservation of Starfield WATR GNAM. xEdit defines these 12 bytes as
///     unused, not as a vector; retaining the raw little-endian words avoids assigning float
///     semantics or rejecting otherwise valid opaque bit patterns.
/// </summary>
public sealed record StarfieldWaterUnusedGnam
{
    public uint Word0 { get; init; }

    public uint Word1 { get; init; }

    public uint Word2 { get; init; }
}

/// <summary>
///     Exact typed representation of Starfield's little-endian 152-byte WATR DNAM. This model is
///     ingestion-only: it preserves CE2's authored optical, displacement, and noise parameters but
///     deliberately does not project them onto the older shallow/deep-color water contract.
/// </summary>
public sealed record StarfieldWaterDnam
{
    public float DepthAmount { get; init; }

    public (float R, float G, float B) AbsorptionRanges { get; init; }

    public float PhytoplanktonConcentration { get; init; }

    public float SedimentConcentration { get; init; }

    public float YellowMatterConcentration { get; init; }

    public float Oceanness { get; init; }

    public (byte R, byte G, byte B, byte A) UnderwaterColor { get; init; }

    public float UnderwaterFogAmount { get; init; }

    public float UnderwaterFogNear { get; init; }

    public float UnderwaterFogFar { get; init; }

    public float NormalMagnitude { get; init; }

    public float ShallowNormalFalloff { get; init; }

    public float DeepNormalFalloff { get; init; }

    public float SurfaceEffectFalloff { get; init; }

    public float DisplacementForce { get; init; }

    public float DisplacementVelocity { get; init; }

    public float DisplacementFalloff { get; init; }

    public float DisplacementDampener { get; init; }

    public float DisplacementStartingSize { get; init; }

    public StarfieldWaterNoiseLayer Layer1 { get; init; } = new();

    public StarfieldWaterNoiseLayer Layer2 { get; init; } = new();

    public StarfieldWaterNoiseLayer Layer3 { get; init; } = new();

    public float FlowmapScale { get; init; }

    public float Roughness { get; init; }
}

/// <summary>
///     Starfield's complete typed WATR visual-data envelope. DNAM is required by the CE2 schema;
///     FNAM and DNAM are required by the CE2 schema; the remaining subrecords are nullable because
///     plugin records may omit optional fields. A present malformed field invalidates the entire
///     envelope instead of leaving a mixture of trusted and untrusted values.
/// </summary>
public sealed record StarfieldWaterVisualData
{
    public StarfieldWaterDnam Dnam { get; init; } = new();

    public StarfieldWaterFlags Flags { get; init; }

    /// <summary>GNAM's 12-byte unused payload, retained without assigning numeric semantics.</summary>
    public StarfieldWaterUnusedGnam? Gnam { get; init; }

    /// <summary>NAM0 linear velocity.</summary>
    public (float X, float Y, float Z)? LinearVelocity { get; init; }

    /// <summary>NAM1 angular velocity.</summary>
    public (float X, float Y, float Z)? AngularVelocity { get; init; }

    /// <summary>ENAM river absorption CUR3.</summary>
    public uint? RiverAbsorptionCurveFormId { get; init; }

    /// <summary>HNAM ocean absorption CUR3.</summary>
    public uint? OceanAbsorptionCurveFormId { get; init; }

    /// <summary>JNAM river scattering CUR3.</summary>
    public uint? RiverScatteringCurveFormId { get; init; }

    /// <summary>LNAM ocean scattering CUR3.</summary>
    public uint? OceanScatteringCurveFormId { get; init; }

    /// <summary>MNAM phytoplankton CUR3.</summary>
    public uint? PhytoplanktonCurveFormId { get; init; }

    /// <summary>QNAM sediment CUR3.</summary>
    public uint? SedimentCurveFormId { get; init; }

    /// <summary>UNAM yellow-matter CUR3.</summary>
    public uint? YellowMatterCurveFormId { get; init; }
}
