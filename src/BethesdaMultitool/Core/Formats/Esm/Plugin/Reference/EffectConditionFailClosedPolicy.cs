using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

/// <summary>Shared fail-closed replacement for an effect condition expression that cannot resolve.</summary>
internal static class EffectConditionFailClosedPolicy
{
    /// <summary>
    ///     A standalone condition that is deterministically false: GetIsID(Player base) returns
    ///     either zero or one, so equality with two cannot pass. Replacing the complete expression
    ///     avoids widening an authored AND/OR chain by dropping only one member.
    /// </summary>
    internal static DialogueCondition NeverFire { get; } = new()
    {
        Type = 0,
        ComparisonValue = 2.0f,
        FunctionIndex = 0x0048,
        Parameter1 = 0x00000007,
        Parameter2 = 0,
        RunOn = 0,
        Reference = 0
    };
}
