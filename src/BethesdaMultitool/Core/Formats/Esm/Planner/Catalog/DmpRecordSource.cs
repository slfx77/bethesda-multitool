using BethesdaMultitool.Core.Formats.Esm.Models;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.Catalog;

/// <summary>
///     DMP-side input to <see cref="RecordCatalog" />. Walks the typed <see cref="RecordCollection" />
///     lists and yields <see cref="CatalogEntry" /> values keyed by signature.
/// </summary>
/// <remarks>
///     This table is intentionally broader than current production output coverage: a type
///     can be enumerable before it has both a registered planned encoder and a current
///     <c>PluginConversionPipeline.EnumerateModelsByType</c> dispatch row. Routing-consistency
///     tests guard the required agreement; the tier labels below are migration history only.
/// </remarks>
public sealed class DmpRecordSource
{
    /// <summary>
    ///     Per-record-type extractors. Each yields <c>(FormId, Model)</c> pairs in the same
    ///     order as the current pipeline dispatch. Catalog insertion order, and therefore
    ///     deterministic allocation order, depends on this sequence.
    /// </summary>
    private static readonly Dictionary<string, Func<RecordCollection, IEnumerable<(uint FormId, object Model)>>>
        Extractors = new(StringComparer.Ordinal)
        {
            // Historical migration group: Tier 1 trivial static-data encoders.
            ["GMST"] = c => c.GameSettings.Select(r => (r.FormId, (object)r)),
            ["GLOB"] = c => c.Globals.Select(r => (r.FormId, (object)r)),
            ["WEAP"] = c => c.Weapons.Select(r => (r.FormId, (object)r)),
            ["ARMO"] = c => c.Armor.Select(r => (r.FormId, (object)r)),
            ["AMMO"] = c => c.Ammo.Select(r => (r.FormId, (object)r)),
            ["ALCH"] = c => c.Consumables.Select(r => (r.FormId, (object)r)),
            ["BOOK"] = c => c.Books.Select(r => (r.FormId, (object)r)),
            ["STAT"] = c => c.Statics.Select(r => (r.FormId, (object)r)),
            // Historical migration group: Tier 2 simple FormID-ref encoders (FormIDs emitted verbatim or via WEAP's
            // transitional validFormIds/remapTable pass-through).
            ["DOOR"] = c => c.Doors.Select(r => (r.FormId, (object)r)),
            ["MISC"] = c => c.MiscItems.Select(r => (r.FormId, (object)r)),
            ["KEYM"] = c => c.Keys.Select(r => (r.FormId, (object)r)),
            ["NOTE"] = c => c.Notes.Select(r => (r.FormId, (object)r)),
            ["RCPE"] = c => c.Recipes.Select(r => (r.FormId, (object)r)),
            ["COBJ"] = c => c.ConstructibleObjects.Select(r => (r.FormId, (object)r)),
            ["ARMA"] = c => c.ArmorAddons.Select(r => (r.FormId, (object)r)),
            ["IMOD"] = c => c.WeaponMods.Select(r => (r.FormId, (object)r)),
            ["ENCH"] = c => c.Enchantments.Select(r => (r.FormId, (object)r)),
            ["SPEL"] = c => c.Spells.Select(r => (r.FormId, (object)r)),
            ["EXPL"] = c => c.Explosions.Select(r => (r.FormId, (object)r)),
            ["MGEF"] = c => c.BaseEffects.Select(r => (r.FormId, (object)r)),
            ["PROJ"] = c => c.Projectiles.Select(r => (r.FormId, (object)r)),
            // Historical migration group: Tier 2 character/misc/world/AI expansion.
            ["SOUN"] = c => c.Sounds.Select(r => (r.FormId, (object)r)),
            ["FACT"] = c => c.Factions.Select(r => (r.FormId, (object)r)),
            ["HAIR"] = c => c.Hair.Select(r => (r.FormId, (object)r)),
            ["EYES"] = c => c.Eyes.Select(r => (r.FormId, (object)r)),
            ["HDPT"] = c => c.HeadParts.Select(r => (r.FormId, (object)r)),
            ["BPTD"] = c => c.BodyPartData.Select(r => (r.FormId, (object)r)),
            ["AVIF"] = c => c.ActorValueInfos.Select(r => (r.FormId, (object)r)),
            ["CLAS"] = c => c.Classes.Select(r => (r.FormId, (object)r)),
            ["RACE"] = c => c.Races.Select(r => (r.FormId, (object)r)),
            ["REPU"] = c => c.Reputations.Select(r => (r.FormId, (object)r)),
            ["VTYP"] = c => c.VoiceTypes.Select(r => (r.FormId, (object)r)),
            ["CHAL"] = c => c.Challenges.Select(r => (r.FormId, (object)r)),
            ["INGR"] = c => c.Ingredients.Select(r => (r.FormId, (object)r)),
            ["IPCT"] = c => c.ImpactData.Select(r => (r.FormId, (object)r)),
            ["LTEX"] = c => c.LandTextures.Select(r => (r.FormId, (object)r)),
            ["MICN"] = c => c.MenuIcons.Select(r => (r.FormId, (object)r)),
            ["MUSC"] = c => c.MusicTypes.Select(r => (r.FormId, (object)r)),
            ["RCCT"] = c => c.RecipeCategories.Select(r => (r.FormId, (object)r)),
            ["TXST"] = c => c.TextureSets.Select(r => (r.FormId, (object)r)),
            ["ACTI"] = c => c.Activators.Select(r => (r.FormId, (object)r)),
            ["DEBR"] = c => c.Debris.Select(r => (r.FormId, (object)r)),
            ["CSTY"] = c => c.CombatStyles.Select(r => (r.FormId, (object)r)),
            ["IMAD"] = c => c.ImageSpaceModifiers.Select(r => (r.FormId, (object)r)),
            // Historical migration group: Tier 3 complex-ref encoders.
            ["SCPT"] = c => c.Scripts.Select(r => (r.FormId, (object)r)),
            ["PERK"] = c => c.Perks.Select(r => (r.FormId, (object)r)),
            ["CONT"] = c => c.Containers.Select(r => (r.FormId, (object)r)),
            ["IDLE"] = c => c.IdleAnimations.Select(r => (r.FormId, (object)r)),
            ["TERM"] = c => c.Terminals.Select(r => (r.FormId, (object)r)),
            // LeveledList: one model serves LVLI/LVLN/LVLC — partition by ListType so each
            // signature's GRUP gets only its own records (matches EnumerateModelsByType).
            ["LVLI"] = c => c.LeveledLists
                .Where(r => r.ListType == "LVLI")
                .Select(r => (r.FormId, (object)r)),
            ["LVLN"] = c => c.LeveledLists
                .Where(r => r.ListType == "LVLN")
                .Select(r => (r.FormId, (object)r)),
            ["LVLC"] = c => c.LeveledLists
                .Where(r => r.ListType == "LVLC")
                .Select(r => (r.FormId, (object)r)),
            ["NPC_"] = c => c.Npcs.Select(r => (r.FormId, (object)r)),
            ["CREA"] = c => c.Creatures.Select(r => (r.FormId, (object)r)),
            ["QUST"] = c => c.Quests.Select(r => (r.FormId, (object)r)),
            ["INFO"] = c => c.Dialogues.Select(r => (r.FormId, (object)r)),
            // Historical migration group: Tier 4 cross-record coordination.
            ["PACK"] = c => c.Packages.Select(r => (r.FormId, (object)r)),
            ["CPTH"] = c => c.CameraPaths.Select(r => (r.FormId, (object)r)),
            ["DIAL"] = c => c.DialogTopics.Select(r => (r.FormId, (object)r)),
            ["MESG"] = c => c.Messages.Select(r => (r.FormId, (object)r)),
            // Historical migration group: Tier 5a top-level world/misc encoders. The cell-children
            // record types (REFR/ACHR/ACRE/LAND/NAVM/PGRE) ship in Tier 5b along with
            // the cell-pipeline integration that nests them under CELL Children GRUPs.
            ["WRLD"] = c => c.Worldspaces.Select(r => (r.FormId, (object)r)),
            ["LIGH"] = c => c.Lights.Select(r => (r.FormId, (object)r)),
            ["FURN"] = c => c.Furniture.Select(r => (r.FormId, (object)r)),
            ["WATR"] = c => c.Water.Select(r => (r.FormId, (object)r)),
            ["WTHR"] = c => c.Weather.Select(r => (r.FormId, (object)r)),
            ["LGTM"] = c => c.LightingTemplates.Select(r => (r.FormId, (object)r)),
            ["ECZN"] = c => c.EncounterZones.Select(r => (r.FormId, (object)r)),
            ["LSCT"] = c => c.LoadScreenTypes.Select(r => (r.FormId, (object)r)),
            ["REGN"] = c => c.Regions.Select(r => (r.FormId, (object)r)),
            ["SCOL"] = c => c.StaticCollections.Select(r => (r.FormId, (object)r)),
            // Historical migration group: Tier 5a cleanup.
            ["ALOC"] = c => c.AudioLocationControllers.Select(r => (r.FormId, (object)r)),
            ["CCRD"] = c => c.CaravanCards.Select(r => (r.FormId, (object)r)),
            ["CMNY"] = c => c.CaravanMoney.Select(r => (r.FormId, (object)r)),
            ["CDCK"] = c => c.CaravanDecks.Select(r => (r.FormId, (object)r)),
            ["FLST"] = c => c.FormLists.Select(r => (r.FormId, (object)r)),
            ["PWAT"] = c => c.PlaceableWaters.Select(r => (r.FormId, (object)r)),
            ["TREE"] = c => c.Trees.Select(r => (r.FormId, (object)r)),
            // Historical migration group: Tier 5d final ordinary top-level types.
            ["CLMT"] = c => c.Climate.Select(r => (r.FormId, (object)r)),
            ["GRAS"] = c => c.Grasses.Select(r => (r.FormId, (object)r)),
            ["IMGS"] = c => c.ImageSpaces.Select(r => (r.FormId, (object)r)),
            ["RADS"] = c => c.RadiationStages.Select(r => (r.FormId, (object)r)),
            ["DEHY"] = c => c.DehydrationStages.Select(r => (r.FormId, (object)r)),
            ["HUNG"] = c => c.HungerStages.Select(r => (r.FormId, (object)r)),
            ["SLPD"] = c => c.SleepDeprivationStages.Select(r => (r.FormId, (object)r)),
            // Historical migration group: Tier 5c generic-record types. These share one
            // untyped RecordCollection list and are partitioned by GenericEsmRecord.RecordType,
            // matching PluginConversionPipeline's FLOR/MSTT/ANIO/TACT/ASPC/ADDN filters.
            // Without a row, the DMP contributes no catalog candidate and no plugin GRUP bytes.
            ["FLOR"] = c => GenericsOfType(c, "FLOR"),
            ["MSTT"] = c => GenericsOfType(c, "MSTT"),
            ["ANIO"] = c => GenericsOfType(c, "ANIO"),
            ["TACT"] = c => GenericsOfType(c, "TACT"),
            ["ASPC"] = c => GenericsOfType(c, "ASPC"),
            ["ADDN"] = c => GenericsOfType(c, "ADDN"),
            // Generic-record types wired 2026-08-26 (adversarial recovery audit M1). All five are
            // read out of every dump by RecordParser's generic sweep and were dropped at the writer
            // boundary until they gained an encoder, this row, and a pipeline yield.
            ["LSCR"] = c => GenericsOfType(c, "LSCR"),
            ["CHIP"] = c => GenericsOfType(c, "CHIP"),
            ["IDLM"] = c => GenericsOfType(c, "IDLM"),
            ["CAMS"] = c => GenericsOfType(c, "CAMS"),
            ["MSET"] = c => GenericsOfType(c, "MSET"),
            // Round 3 of the same audit. EFSH/RGDL/CSNO became emittable once ReadEmbeddedStruct
            // handed back raw bytes for >8 B structs: all three carry their payload in a single
            // block whose runtime size matches the file schema exactly, so the existing BE→LE
            // registry converts it. CSNO's model/texture arrays became readable with the
            // 2026-08-31 layout regeneration and now emit too — see CsnoEncoder.
            ["EFSH"] = c => GenericsOfType(c, "EFSH"),
            ["RGDL"] = c => GenericsOfType(c, "RGDL"),
            ["CSNO"] = c => GenericsOfType(c, "CSNO"),
            // Reachable only after pdb_layouts.json gained LF_ARRAY resolution: both types' whole
            // payload is an inline pointer array that used to export as size:0 / kind:"unknown".
            ["IPDS"] = c => GenericsOfType(c, "IPDS"),
            ["DOBJ"] = c => GenericsOfType(c, "DOBJ")
        };

    private readonly RecordCollection _collection;

    public DmpRecordSource(RecordCollection collection)
    {
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
    }

    /// <summary>
    ///     Yields the generic records of one signature out of the shared
    ///     <see cref="RecordCollection.GenericRecords" /> list, in list order.
    /// </summary>
    private static IEnumerable<(uint FormId, object Model)> GenericsOfType(
        RecordCollection collection, string recordType)
    {
        return collection.GenericRecords
            .Where(r => string.Equals(r.RecordType, recordType, StringComparison.Ordinal))
            .Select(r => (r.FormId, (object)r));
    }

    /// <summary>
    ///     Yield one entry per DMP record whose type appears in <paramref name="enabledTypes" />.
    ///     The combine step in <see cref="RecordCatalog" /> matches these against the master
    ///     side by FormID — entries paired with master become
    ///     <see cref="SourceKind.DmpOverride" />, the rest <see cref="SourceKind.DmpNew" />.
    /// </summary>
    public IEnumerable<(string Type, uint FormId, object Model)> Enumerate(IReadOnlySet<string> enabledTypes)
    {
        if (enabledTypes.Count == 0)
        {
            yield break;
        }

        foreach (var type in enabledTypes)
        {
            if (!Extractors.TryGetValue(type, out var extractor))
            {
                continue; // No mapping yet for this type — caller must ensure the planner has an encoder before enabling.
            }

            foreach (var (formId, model) in extractor(_collection))
            {
                yield return (type, formId, model);
            }
        }
    }

    /// <summary>
    ///     Enumerates every typed model known to the catalog. Alias validation uses this
    ///     broader view regardless of the requested planner-coverage subset.
    /// </summary>
    internal IEnumerable<(string Type, uint FormId, object Model)> EnumerateAll()
    {
        foreach (var (type, extractor) in Extractors)
        {
            foreach (var (formId, model) in extractor(_collection))
            {
                yield return (type, formId, model);
            }
        }
    }

    /// <summary>
    ///     True when <see cref="DmpRecordSource" /> knows how to enumerate the given record
    ///     type. Routing-consistency tests use this contract; an enabled unmapped type simply
    ///     contributes no DMP catalog candidates.
    /// </summary>
    public static bool SupportsType(string recordType)
    {
        return Extractors.ContainsKey(recordType);
    }
}
