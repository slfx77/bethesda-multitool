using BethesdaMultitool.Core.Formats.Esm.RecordModel.Generated;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Schema;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.RecordModel;

/// <summary>
///     Registry mapping a <see cref="BethesdaGame" /> to its generated record-layout schema (the per-game
///     <c>*Schema.g.cs</c> in <see cref="Generated" />). The schema-driven reader looks the game up here
///     to decode records with the right layout. Games without a generated schema return null and fall back
///     to the hand-written typed handlers; new games light up by emitting their schema and adding a case.
/// </summary>
public static class EsmSchemas
{
    private static readonly Dictionary<string, RecordDef> OblivionByType = Index(OblivionSchema.Records);
    private static readonly Dictionary<string, RecordDef> SkyrimByType = Index(SkyrimSchema.Records);

    /// <summary>The schema record set for the game, or null when no generated schema exists yet.</summary>
    public static IReadOnlyList<RecordDef>? ForGame(BethesdaGame game) => game switch
    {
        BethesdaGame.Oblivion => OblivionSchema.Records,
        BethesdaGame.Skyrim => SkyrimSchema.Records,
        _ => null
    };

    /// <summary>A signature -&gt; <see cref="RecordDef" /> lookup for the game, or null when unschema'd.</summary>
    public static IReadOnlyDictionary<string, RecordDef>? IndexForGame(BethesdaGame game) => game switch
    {
        BethesdaGame.Oblivion => OblivionByType,
        BethesdaGame.Skyrim => SkyrimByType,
        _ => null
    };

    private static Dictionary<string, RecordDef> Index(IReadOnlyList<RecordDef> records)
    {
        var map = new Dictionary<string, RecordDef>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            map[record.Signature] = record;
        }

        return map;
    }
}
