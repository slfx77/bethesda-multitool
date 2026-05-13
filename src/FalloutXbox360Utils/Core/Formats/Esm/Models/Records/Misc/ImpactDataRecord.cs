namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

/// <summary>
///     Impact Data (IPCT) record. Defines the visual + audio effect when a
///     projectile hits a surface. PDB struct: BGSImpactData (136 bytes,
///     FormType 0x5E).
/// </summary>
public record ImpactDataRecord
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }

    /// <summary>Particle/decal model path (TESModel cModel at +44).</summary>
    public string? ModelPath { get; init; }

    /// <summary>Decal texture set FormID (pDecalTextureSet pointer at +88).</summary>
    public uint DecalTextureSetFormId { get; init; }

    /// <summary>Primary impact sound FormID (pSound1 pointer at +92).</summary>
    public uint Sound1FormId { get; init; }

    /// <summary>Secondary impact sound FormID (pSound2 pointer at +96).</summary>
    public uint Sound2FormId { get; init; }

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }
}
