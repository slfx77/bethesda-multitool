using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

/// <summary>Decodes the game-dependent type byte stored in a TES4 <c>CREA.DATA</c> subrecord.</summary>
internal static class CreatureTypeNamePolicy
{
    internal static string Resolve(BethesdaGame game, byte creatureType)
    {
        return game switch
        {
            BethesdaGame.Oblivion => ResolveOblivion(creatureType),
            BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas => ResolveFallout(creatureType),
            _ => $"Creature type {creatureType}"
        };
    }

    private static string ResolveOblivion(byte creatureType)
    {
        return creatureType switch
        {
            0 => "Creature",
            1 => "Daedra",
            2 => "Undead",
            3 => "Humanoid",
            4 => "Horse",
            5 => "Giant",
            _ => $"Unknown ({creatureType})"
        };
    }

    private static string ResolveFallout(byte creatureType)
    {
        return creatureType switch
        {
            0 => "Animal",
            1 => "Mutated Animal",
            2 => "Mutated Insect",
            3 => "Abomination",
            4 => "Super Mutant",
            5 => "Feral Ghoul",
            6 => "Robot",
            7 => "Giant",
            _ => $"Unknown ({creatureType})"
        };
    }
}
