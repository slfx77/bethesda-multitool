using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Magic;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Encoders;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Encoders.ComplexRef;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Encoders.Trivial;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Encoders.World;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.AI;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Character;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Item;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Magic;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.World;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter;

/// <summary>
///     Central factory listing every <see cref="IPlannedRecordEncoder" /> the planner-side
///     pipeline currently supports. Each tier adds rows here as encoders ship.
/// </summary>
/// <remarks>
///     <c>PluginBuildOptions.PlannerEnabledRecordTypes</c> selects which subset of these
///     encoders are actually exercised per build. An entry here doesn't enable the planner
///     for that type — the build options do. This factory is the single source of truth for
///     "what types CAN the planner emit," used by <c>PluginBuilder</c> to detect mis-configured
///     option sets (planner-enabled type with no registered encoder).
/// </remarks>
public static class PlannedEncoders
{
    /// <summary>
    ///     Build a fresh <see cref="PlannedEncoderRegistry" />. Cheap — encoders are stateless.
    /// </summary>
    public static PlannedEncoderRegistry BuildRegistry()
    {
        return new PlannedEncoderRegistry(BuildAll());
    }

    /// <summary>
    ///     Distinct record-type signatures the planner pipeline can emit. Derived from
    ///     <see cref="BuildAll" /> so newly-registered encoders are picked up automatically.
    ///     Used by the CLI's <c>--planner-types all</c> resolution and by the aggregate
    ///     parity harness to enumerate the encoder coverage.
    /// </summary>
    public static IEnumerable<string> KnownRecordTypes() =>
        BuildAll().Select(e => e.RecordType).Distinct(StringComparer.Ordinal);

    /// <summary>
    ///     One-line registration for a simple-ref encoder: New delegates to the legacy
    ///     <c>EncodeNew(model)</c> primitive, Override emits an empty record.
    /// </summary>
    private static DelegatingPlannedEncoder<TModel> Simple<TModel>(
        string recordType, Func<TModel, EncodedRecord> encodeNew) where TModel : class =>
        new(recordType, encodeNew);

    /// <summary>
    ///     Enumerate every planned encoder. Tier 1+ extends this list.
    /// </summary>
    public static IEnumerable<IPlannedRecordEncoder> BuildAll()
    {
        // Tier 1 — trivial static-data encoders. No outgoing FormID resolution (or only
        // verbatim FormID pass-through, matching legacy behavior byte-for-byte).
        yield return new PlannedStatEncoder();
        yield return new PlannedGlobEncoder();
        yield return new PlannedGmstEncoder();
        yield return new PlannedArmoEncoder();
        yield return new PlannedAmmoEncoder();
        yield return new PlannedBookEncoder();
        yield return new PlannedAlchEncoder();

        // Tier 2 — simple FormID-ref encoders. Most emit FormIDs verbatim without
        // validation; WEAP threads the plan's emit set + remap table through to
        // its legacy EncodeNew(weap, validFormIds, remapTable) overload.
        yield return new PlannedWeapEncoder();
        yield return Simple<DoorRecord>("DOOR", DoorEncoder.EncodeNew);
        yield return Simple<MiscItemRecord>("MISC", MiscEncoder.EncodeNew);
        yield return Simple<KeyRecord>("KEYM", KeymEncoder.EncodeNew);
        yield return Simple<NoteRecord>("NOTE", NoteEncoder.EncodeNew);
        yield return Simple<RecipeRecord>("RCPE", RcpeEncoder.EncodeNew);
        yield return Simple<ConstructibleObjectRecord>("COBJ", CobjEncoder.EncodeNew);
        yield return Simple<ArmaRecord>("ARMA", ArmaEncoder.EncodeNew);
        yield return Simple<WeaponModRecord>("IMOD", ImodEncoder.EncodeNew);
        yield return Simple<EnchantmentRecord>("ENCH", EnchEncoder.EncodeNew);
        yield return Simple<SpellRecord>("SPEL", SpelEncoder.EncodeNew);
        yield return Simple<ExplosionRecord>("EXPL", ExplEncoder.EncodeNew);
        yield return Simple<BaseEffectRecord>("MGEF", MgefEncoder.EncodeNew);
        yield return Simple<ProjectileRecord>("PROJ", ProjEncoder.EncodeNew);

        // Tier 2 expansion — character/misc/world/AI trivials. Same delegate pattern as
        // the simple-ref encoders above.
        yield return Simple<SoundRecord>("SOUN", SounEncoder.EncodeNew);
        yield return Simple<FactionRecord>("FACT", FactEncoder.EncodeNew);
        yield return Simple<HairRecord>("HAIR", HairEncoder.EncodeNew);
        yield return Simple<EyesRecord>("EYES", EyesEncoder.EncodeNew);
        yield return Simple<HeadPartRecord>("HDPT", HdptEncoder.EncodeNew);
        yield return Simple<BodyPartDataRecord>("BPTD", BptdEncoder.EncodeNew);
        yield return Simple<ActorValueInfoRecord>("AVIF", AvifEncoder.EncodeNew);
        yield return Simple<ClassRecord>("CLAS", ClasEncoder.EncodeNew);
        yield return Simple<RaceRecord>("RACE", RaceEncoder.EncodeNew);
        yield return Simple<ReputationRecord>("REPU", RepuEncoder.EncodeNew);
        yield return Simple<VoiceTypeRecord>("VTYP", VtypEncoder.EncodeNew);
        yield return Simple<ChallengeRecord>("CHAL", ChalEncoder.EncodeNew);
        yield return Simple<IngredientRecord>("INGR", IngrEncoder.EncodeNew);
        yield return Simple<ImpactDataRecord>("IPCT", IpctEncoder.EncodeNew);
        yield return Simple<LandscapeTextureRecord>("LTEX", LtexEncoder.EncodeNew);
        yield return Simple<MenuIconRecord>("MICN", MicnEncoder.EncodeNew);
        yield return Simple<MusicTypeRecord>("MUSC", MuscEncoder.EncodeNew);
        yield return Simple<RecipeCategoryRecord>("RCCT", RcctEncoder.EncodeNew);
        yield return Simple<TextureSetRecord>("TXST", TxstEncoder.EncodeNew);
        yield return Simple<ActivatorRecord>("ACTI", ActiEncoder.EncodeNew);
        yield return Simple<DebrisRecord>("DEBR", DebrEncoder.EncodeNew);
        yield return Simple<CombatStyleRecord>("CSTY", CstyEncoder.EncodeNew);

        // Tier 3 — complex FormID-ref encoders. Transitional pass-through to legacy
        // EncodeNew(model, validFormIds, remapTable); FormID resolution comes from the
        // plan's emit set. End-to-end parity for records that reference engine-hardcoded
        // FormIDs or master-child FormIDs (player ref, placed refs) needs additional plan
        // plumbing — synthetic tests with no outgoing refs still pass byte-for-byte.
        yield return new PlannedImadEncoder();
        yield return new PlannedScptEncoder();
        yield return new PlannedPerkEncoder();
        yield return new PlannedContEncoder();
        yield return new PlannedIdleEncoder();
        yield return new PlannedTermEncoder();
        yield return new PlannedLvliEncoder("LVLI");
        yield return new PlannedLvliEncoder("LVLN");
        yield return new PlannedLvliEncoder("LVLC");
        yield return new PlannedNpcEncoder();
        yield return new PlannedCreaEncoder();
        yield return new PlannedQustEncoder();
        yield return new PlannedInfoEncoder();

        // Tier 4 — cross-record coordination encoders. PACK PLDT degradation still
        // happens inside legacy EncodeNew transitionally; planner-side downgrade via
        // ResolvedRefAction.DowngradeContainer is a Tier 4 follow-up. REFR/ACHR/ACRE
        // (placed refs) emit under CELL Children GRUPs and ship in Tier 5.
        yield return new PlannedPackEncoder();
        yield return new PlannedCpthEncoder();
        yield return new PlannedDialEncoder();
        yield return new PlannedMesgEncoder();

        // Tier 5a — remaining top-level world / misc encoders. Cell-children types
        // (REFR/ACHR/ACRE/LAND/NAVM/PGRE) ship in Tier 5b once cell-pipeline integration
        // routes their emission through the planner.
        yield return Simple<WorldspaceRecord>("WRLD", WrldEncoder.EncodeNew);
        yield return Simple<LightRecord>("LIGH", LighEncoder.EncodeNew);
        yield return Simple<FurnitureRecord>("FURN", FurnEncoder.EncodeNew);
        yield return Simple<WaterRecord>("WATR", WatrEncoder.EncodeNew);
        yield return Simple<WeatherRecord>("WTHR", WthrEncoder.EncodeNew);
        yield return Simple<LightingTemplateRecord>("LGTM", LgtmEncoder.EncodeNew);
        yield return Simple<EncounterZoneRecord>("ECZN", EczEncoder.EncodeNew);
        yield return Simple<LoadScreenTypeRecord>("LSCT", LsctEncoder.EncodeNew);
        yield return Simple<RegionRecord>("REGN", RegnEncoder.EncodeNew);
        yield return new PlannedScolEncoder();
        yield return Simple<AudioLocationControllerRecord>("ALOC", AlocEncoder.EncodeNew);
        yield return Simple<CaravanCardRecord>("CCRD", CcrdEncoder.EncodeNew);
        yield return Simple<CaravanMoneyRecord>("CMNY", CmnyEncoder.EncodeNew);
        yield return Simple<CaravanDeckRecord>("CDCK", CdckEncoder.EncodeNew);
        yield return Simple<FormListRecord>("FLST", FlstEncoder.EncodeNew);

        // Tier 5b kickoff — CELL + placed-reference (REFR/ACHR/ACRE) encoders. These are
        // registered but not yet invoked by any dispatch path: cell-children records
        // (REFR/ACHR/ACRE) emit through CellGrupBuilder's persistent/temporary/VWD
        // children GRUPs and CELL emits through the WRLD cell-block hierarchy. Routing
        // those through the planner is the cell-pipeline integration that finishes Tier
        // 5b. LAND/NAVM/NAVI are not yet ported — they lack standard IRecordEncoder
        // paths and emit via specialized builders (LandOverrideBuilder, NavInfoMapBuilder,
        // etc.) that need their own planner-aware abstractions.
        yield return new PlannedCellEncoder();
        yield return new PlannedPlacedReferenceEncoder("REFR");
        yield return new PlannedPlacedReferenceEncoder("ACHR");
        yield return new PlannedPlacedReferenceEncoder("ACRE");

        // Tier 7a — PGRE (placed grenade). Structural mirror of the placed-reference
        // encoders; cell-children dispatch routing is a follow-up that needs PGRE→parent
        // cell mapping on the model. Registered now so the encoder ships with the
        // primitive in place when routing lands.
        yield return new PlannedPgreEncoder();
    }
}
