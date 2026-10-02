using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Script.Conditions;

/// <summary>Game-specific meanings of the CTDA type byte's low flag bits.</summary>
internal static class ConditionTypeFlags
{
    internal static bool HasModernFlags(BethesdaGame game) => game is BethesdaGame.Skyrim
        or BethesdaGame.Fallout4 or BethesdaGame.Fallout76 or BethesdaGame.Starfield;

    internal static byte UnknownBits(byte type, BethesdaGame game) =>
        (byte)(type & (HasModernFlags(game) ? 0 : game is BethesdaGame.Oblivion or BethesdaGame.Fallout3
            or BethesdaGame.FalloutNewVegas ? 0x18 : 0x1A));

    internal static IReadOnlyList<string> Describe(byte type, BethesdaGame game)
    {
        var flags = new List<string>();
        if ((type & 0x02) != 0)
        {
            if (HasModernFlags(game))
            {
                flags.Add("Use Aliases");
            }
            else if (game is BethesdaGame.Oblivion or BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas)
            {
                flags.Add("Run On Target (type flag)");
            }
        }

        if (HasModernFlags(game))
        {
            if ((type & 0x08) != 0)
            {
                flags.Add("Use Pack Data");
            }
            if ((type & 0x10) != 0)
            {
                flags.Add("Swap Subject/Target");
            }
        }

        if (UnknownBits(type, game) is var unknown && unknown != 0)
        {
            flags.Add($"type bits 0x{unknown:X2} (no known meaning in this game)");
        }
        return flags;
    }
}
