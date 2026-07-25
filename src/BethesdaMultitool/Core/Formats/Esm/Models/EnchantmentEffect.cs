using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Models;

/// <summary>One magic effect on an enchantment or spell, parsed from its EFID/EFIT subrecords.</summary>
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

    /// <summary>Per-effect conditions (CTDA subrecords following this effect's EFID/EFIT). Empty for
    ///     unconditioned effects and for ENCH/SPEL (whose encoders do not emit effect conditions).</summary>
    public List<DialogueCondition> Conditions { get; init; } = [];
}
