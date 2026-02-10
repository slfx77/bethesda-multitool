namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Reconstructed Explosion (EXPL) from memory dump.
/// </summary>
public record ExplosionRecord
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }

    /// <summary>Model path from MODL subrecord.</summary>
    public string? ModelPath { get; init; }

    /// <summary>Force from DATA.</summary>
    public float Force { get; init; }

    /// <summary>Damage from DATA.</summary>
    public float Damage { get; init; }

    /// <summary>Radius from DATA.</summary>
    public float Radius { get; init; }

    /// <summary>Light FormID from DATA.</summary>
    public uint Light { get; init; }

    /// <summary>Sound1 FormID from DATA.</summary>
    public uint Sound1 { get; init; }

    /// <summary>Flags from DATA.</summary>
    public uint Flags { get; init; }

    /// <summary>IS Radius from DATA (image space modifier radius).</summary>
    public float ISRadius { get; init; }

    /// <summary>Impact Data Set FormID from DATA.</summary>
    public uint ImpactDataSet { get; init; }

    /// <summary>Sound2 FormID from DATA.</summary>
    public uint Sound2 { get; init; }

    /// <summary>Enchantment/Object Effect FormID from EITM subrecord.</summary>
    public uint Enchantment { get; init; }

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }
}
