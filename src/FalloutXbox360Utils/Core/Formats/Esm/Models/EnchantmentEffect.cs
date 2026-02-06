namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

public record EnchantmentEffect
{
    /// <summary>Base effect FormID (MGEF) from EFID subrecord.</summary>
    public uint EffectFormId { get; init; }

    /// <summary>Magnitude from EFIT.</summary>
    public float Magnitude { get; init; }

    /// <summary>Area of effect from EFIT.</summary>
    public uint Area { get; init; }

    /// <summary>Duration in seconds from EFIT.</summary>
    public uint Duration { get; init; }

    /// <summary>Effect type from EFIT: 0=Self, 1=Touch, 2=Target.</summary>
    public uint Type { get; init; }

    /// <summary>Actor value index from EFIT (-1 if not applicable).</summary>
    public int ActorValue { get; init; }
}
