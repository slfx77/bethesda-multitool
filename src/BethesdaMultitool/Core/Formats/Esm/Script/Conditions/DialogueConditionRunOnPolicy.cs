using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Script.Conditions;

/// <summary>
///     Formats CTDA Run-On semantics according to the game and function that owns them. Classic
///     records can carry Target in Type bit 1; later records use offset 20. Two FNV functions
///     reinterpret offset 20 as a sparse animation-body selector whose zero value is meaningful.
/// </summary>
internal static class DialogueConditionRunOnPolicy
{
    public static bool ShouldDisplay(DialogueCondition condition, BethesdaGame game)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return ShouldDisplay(condition.Type, condition.FunctionIndex, condition.RunOn, game);
    }

    public static bool ShouldDisplay(byte type, ushort functionIndex, uint runOn, BethesdaGame game)
    {
        return runOn != 0 || IsLegacyRunOnTarget(type, game) || IsFnvAnimationBodySelector(functionIndex, game);
    }

    public static string Format(DialogueCondition condition, BethesdaGame game)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return Format(condition.Type, condition.FunctionIndex, condition.RunOn, game);
    }

    public static string Format(byte type, ushort functionIndex, uint runOn, BethesdaGame game)
    {
        // TES4 and the original FO3/FNV 20-byte layout stored Target in Type bit 1. xEdit's
        // after-load migration clears the bit and writes Run On=Target, so the raw bit wins here.
        if (IsLegacyRunOnTarget(type, game))
        {
            return "Target";
        }

        if (IsFnvAnimationBodySelector(functionIndex, game))
        {
            return runOn switch
            {
                0 => "Idle",
                1 => "Movement",
                2 => "Left Arm",
                3 => "Left Hand",
                4 => "Weapon",
                5 => "Weapon Up",
                6 => "Weapon Down",
                7 => "Special Idle",
                20 => "Whole Body",
                21 => "Upper Body",
                _ => $"Unknown ({runOn})"
            };
        }

        if (game == BethesdaGame.Skyrim)
        {
            return runOn switch
            {
                5 => "Quest Alias",
                6 => "Package Data",
                7 => "Event Data",
                _ => FormatCommon(runOn)
            };
        }

        if (game is BethesdaGame.Fallout4 or BethesdaGame.Fallout76)
        {
            return runOn switch
            {
                5 => "Quest Alias",
                6 => "Package Data",
                7 => "Event Data",
                8 => "Command Target",
                9 => "Event Camera Ref",
                10 => "My Killer",
                11 when game == BethesdaGame.Fallout76 => "Active Players",
                12 when game == BethesdaGame.Fallout76 => "Potential Players",
                13 when game == BethesdaGame.Fallout76 => "Player Teammates",
                14 when game == BethesdaGame.Fallout76 => "Target List",
                15 when game == BethesdaGame.Fallout76 => "Instance Owner",
                _ => FormatCommon(runOn)
            };
        }

        if (game == BethesdaGame.Starfield)
        {
            // Community provenance: xEdit commit e0e529a2d473756520f2d41f72c24dea0cf5ee0d,
            // wbDefinitionsSF1.pas SHA-256
            // 8736162FCE44C970CFA3DDAC945A739530169390C4FDABAFC0209B36B247A576,
            // MPL-2.0. Retail data confirms the 32-byte layout, not these labels.
            return runOn switch
            {
                5 => "Quest Alias",
                6 => "Package Data",
                7 => "Event Data",
                8 => "Command Target",
                9 => "Event Camera Ref",
                10 => "My Killer",
                11 => "Self Packin",
                12 => "Target Packin",
                13 => "My Ship",
                14 => "Player Home Ship",
                15 => "Player",
                _ => FormatCommon(runOn)
            };
        }

        return game is BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas
            ? FormatCommon(runOn)
            : $"Unknown ({runOn})";
    }

    private static bool IsLegacyRunOnTarget(byte type, BethesdaGame game)
    {
        return (type & 0x02) != 0 &&
               game is BethesdaGame.Oblivion or BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas;
    }

    private static bool IsFnvAnimationBodySelector(ushort functionIndex, BethesdaGame game)
    {
        return game == BethesdaGame.FalloutNewVegas && functionIndex is 0x006A or 0x011D;
    }

    private static string FormatCommon(uint runOn)
    {
        return runOn switch
        {
            0 => "Subject",
            1 => "Target",
            2 => "Reference",
            3 => "Combat Target",
            4 => "Linked Reference",
            _ => $"Unknown ({runOn})"
        };
    }
}
