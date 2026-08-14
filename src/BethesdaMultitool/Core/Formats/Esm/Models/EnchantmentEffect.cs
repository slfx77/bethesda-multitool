using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Models;

/// <summary>One magic effect on a consumable, enchantment, or spell.</summary>
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

    /// <summary>
    ///     Per-effect conditions (CTDA subrecords following this effect's EFID/EFIT), including physically
    ///     adjacent CIS1/CIS2 strings when present. The typed ALCH/ENCH/SPEL parsers preserve that grammar.
    ///     The Fallout New Vegas target writers emit its 28-byte CTDA form; modern CIS strings and the
    ///     Parameter #3 tail are outside that writer contract.
    /// </summary>
    public List<DialogueCondition> Conditions { get; init; } = [];
}
