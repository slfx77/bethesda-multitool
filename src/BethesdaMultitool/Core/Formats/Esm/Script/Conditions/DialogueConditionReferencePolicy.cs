using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Script.Conditions;

/// <summary>
///     Decides whether the raw CTDA offset-24 storage is semantically a Reference FormID.
///     This mirrors xEdit's game-aware <c>wbConditionReferenceDecider</c>: the slot is a
///     FormID only for RunOn=Reference (2), except that Fallout: New Vegas functions
///     0x006A (IsFacingUp) and 0x011D (IsLeftUp) do not use it even with that selector.
/// </summary>
internal static class DialogueConditionReferencePolicy
{
    /// <summary>Returns whether offset 24 is a semantic Reference FormID for this game.</summary>
    public static bool IsSemanticReferenceSlot(
        DialogueCondition condition,
        BethesdaGame game)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return IsSemanticReferenceSlot(condition.FunctionIndex, condition.RunOn, game);
    }

    /// <summary>Returns whether offset 24 is a semantic Reference FormID for these raw CTDA fields.</summary>
    public static bool IsSemanticReferenceSlot(
        ushort functionIndex,
        uint runOn,
        BethesdaGame game)
    {
        if (runOn != 2)
        {
            return false;
        }

        if (game is BethesdaGame.Unknown or BethesdaGame.Morrowind)
        {
            return false;
        }

        return game != BethesdaGame.FalloutNewVegas
               || functionIndex is not (0x006A or 0x011D);
    }

    /// <summary>Gets the nonzero semantic Reference FormID without exposing ignored raw storage.</summary>
    public static bool TryGetSemanticReference(
        DialogueCondition condition,
        BethesdaGame game,
        out uint reference)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (condition.Reference != 0 && IsSemanticReferenceSlot(condition, game))
        {
            reference = condition.Reference;
            return true;
        }

        reference = 0;
        return false;
    }
}
