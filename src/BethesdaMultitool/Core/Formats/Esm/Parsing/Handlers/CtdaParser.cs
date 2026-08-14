using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

/// <summary>
///     Decoder for the 20/24/28-byte classic and 32-byte modern CTDA condition subrecords, shared across record types that
///     carry conditions (INFO, TERM, QUST, COBJ, ...). Mirrors
///     <see cref="BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Quest.InfoEncoder" />'s
///     28-byte BuildCtdaSubrecord prefix exactly: Type(1) + pad(3) + ComparisonValue(f32) +
///     FunctionIndex(u16) + pad(2) + Parameter1(u32) + Parameter2(u32) + RunOn(u32) +
///     Reference(u32). A complete modern layout appends signed Parameter3(i32).
/// </summary>
internal static class CtdaParser
{
    internal static bool IsSupportedBodyLength(int length)
    {
        return ConditionSubrecordDecoder.IsSupportedBodyLength(length);
    }

    /// <summary>
    ///     Returns whether an exact physical CTDA width belongs to the selected game's on-disk
    ///     layout family. Unknown games fail closed; the game-neutral overload remains available
    ///     to parsers that have not yet been given an explicit game identity.
    /// </summary>
    internal static bool IsSupportedBodyLength(BethesdaGame game, int length)
    {
        return game switch
        {
            BethesdaGame.Oblivion => length == 20,
            BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas => length is 20 or 24 or 28,
            BethesdaGame.Skyrim or BethesdaGame.Fallout4 or BethesdaGame.Fallout76 or
                BethesdaGame.Starfield => length == 32,
            _ => false,
        };
    }

    internal static string GetLayoutStatus(BethesdaGame game, int length)
    {
        if (!IsSupportedBodyLength(length))
        {
            return "unsupported_length";
        }

        if (game is BethesdaGame.Unknown or BethesdaGame.Morrowind)
        {
            return "unsupported_game";
        }

        return IsSupportedBodyLength(game, length) ? "valid" : "game_width_mismatch";
    }

    internal static DialogueCondition Decode(ReadOnlySpan<byte> data, bool bigEndian)
    {
        return ToDialogueCondition(ConditionSubrecordDecoder.Decode(data, 0, bigEndian));
    }

    internal static bool TryDecode(
        ReadOnlySpan<byte> data,
        bool bigEndian,
        out DialogueCondition condition,
        out ConditionSubrecord physical)
    {
        if (!ConditionSubrecordDecoder.TryDecode(data, 0, bigEndian, out physical))
        {
            condition = null!;
            return false;
        }

        condition = ToDialogueCondition(physical);
        return true;
    }

    internal static DialogueCondition ToDialogueCondition(ConditionSubrecord physical)
    {
        return new DialogueCondition
        {
            Type = physical.Type,
            ComparisonValue = physical.ComparisonValue,
            FunctionIndex = physical.FunctionIndex,
            Parameter1 = physical.Param1,
            Parameter2 = physical.Param2,
            RunOn = physical.RunOn ?? 0,
            Reference = physical.ReferenceStorage ?? 0,
            Parameter3 = physical.Parameter3
        };
    }
}
