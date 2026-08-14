namespace BethesdaMultitool.Core.Formats.Esm.Models;

/// <summary>
///     One ordered PERK entry condition group: an optional signed PRKC selector followed by
///     zero or more CTDA conditions.
/// </summary>
public record PerkConditionGroup
{
    /// <summary>
    ///     Signed PRKC "Run On" selector. Null preserves a malformed CTDA sequence that had no
    ///     preceding PRKC instead of inventing a selector during round-trip encoding.
    /// </summary>
    public sbyte? RunOn { get; init; }

    /// <summary>Conditions belonging to this PRKC selector, in source order.</summary>
    public List<PerkCondition> Conditions { get; init; } = [];
}
