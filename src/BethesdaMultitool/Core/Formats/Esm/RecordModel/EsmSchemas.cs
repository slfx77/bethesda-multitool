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
    private static readonly Dictionary<string, RecordDef> Tes3ByType = Index(Tes3Schema.Records);
    private static readonly Dictionary<string, RecordDef> OblivionByType = Index(OblivionSchema.Records);
    private static readonly Dictionary<string, RecordDef> SkyrimByType = Index(SkyrimSchema.Records);
    private static readonly Dictionary<string, RecordDef> Fallout3ByType = Index(Fallout3Schema.Records);
    private static readonly Dictionary<string, RecordDef> FalloutNvByType = Index(FalloutNvSchema.Records);
    private static readonly Dictionary<string, RecordDef> Fallout4ByType = Index(Fallout4Schema.Records);
    private static readonly Dictionary<string, RecordDef> Fallout76ByType = Index(Fallout76Schema.Records);

    /// <summary>The schema record set for the game, or null when no generated schema exists yet.</summary>
    public static IReadOnlyList<RecordDef>? ForGame(BethesdaGame game) => game switch
    {
        BethesdaGame.Morrowind => Tes3Schema.Records,
        BethesdaGame.Oblivion => OblivionSchema.Records,
        BethesdaGame.Skyrim => SkyrimSchema.Records,
        BethesdaGame.Fallout3 => Fallout3Schema.Records,
        BethesdaGame.FalloutNewVegas => FalloutNvSchema.Records,
        BethesdaGame.Fallout4 => Fallout4Schema.Records,
        BethesdaGame.Fallout76 => Fallout76Schema.Records,
        _ => null
    };

    /// <summary>A signature -&gt; <see cref="RecordDef" /> lookup for the game, or null when unschema'd.</summary>
    public static IReadOnlyDictionary<string, RecordDef>? IndexForGame(BethesdaGame game) => game switch
    {
        BethesdaGame.Morrowind => Tes3ByType,
        BethesdaGame.Oblivion => OblivionByType,
        BethesdaGame.Skyrim => SkyrimByType,
        BethesdaGame.Fallout3 => Fallout3ByType,
        BethesdaGame.FalloutNewVegas => FalloutNvByType,
        BethesdaGame.Fallout4 => Fallout4ByType,
        BethesdaGame.Fallout76 => Fallout76ByType,
        _ => null
    };

    /// <summary>
    ///     Whether the schema decode is the <em>primary</em> Records-tab source for a game (it has no
    ///     complete hand-written typed handlers). True for Oblivion/Skyrim/FO4/FO76: the
    ///     <see cref="Parsing.SchemaDrivenRecordParser" /> produces the base <see cref="Models.RecordCollection" />.
    ///     False for FNV/FO3 (and Morrowind): those keep their rich typed handlers as the base and the schema
    ///     only <em>enriches</em> — every record gets a parallel DecodedTree (in
    ///     <see cref="Models.RecordCollection.DecodedTreesByFormId" />) without disturbing the typed lists. Morrowind
    ///     never reaches this gate (its own <c>Tes3RecordParser</c> returns early) but is typed-primary too.
    /// </summary>
    public static bool IsSchemaPrimary(BethesdaGame game) => game is
        BethesdaGame.Oblivion or BethesdaGame.Skyrim or BethesdaGame.Fallout4 or BethesdaGame.Fallout76;

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
