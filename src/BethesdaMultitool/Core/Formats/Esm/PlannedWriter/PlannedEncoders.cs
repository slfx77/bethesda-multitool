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
///     Every type registered here is emitted by the planner on every build — the legacy
///     emission path and its per-type opt-in were retired 2026-08-11. This factory is the
///     single source of truth for "what the converter can emit"; a type that
///     <c>PluginBuilder.EnumerateModelsByType</c> yields without a row here emits nothing,
///     which <c>PlannerRoutingConsistencyTests</c> guards against.
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
    ///     Used by the planner-state build and by the aggregate
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
        yield return Simple<PlaceableWaterRecord>("PWAT", PwatEncoder.EncodeNew);
        yield return Simple<TreeRecord>("TREE", TreeEncoder.EncodeNew);
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

        // Tier 5c — the generic-record types. These have no typed model: they arrive as
        // GenericEsmRecord from RuntimeGenericReader (Fields keyed by PDB identifier) or
        // the ESM carve path (keyed by subrecord signature), which is why their legacy
        // encoders all take GenericEsmRecord and read through GenericRecordFields.
        // PlannedEncoderRegistry keys on RecordType rather than model CLR type, so several
        // GenericEsmRecord-backed encoders coexist here without colliding.
        //
        // Routing them through the planner is what makes a REFR on a proto-only MSTT/TACT
        // base resolve. Legacy allocates new top-level FormIDs during Phase 3, which runs
        // AFTER BuildPlannerStateIfEnabled, so CellChildVerdictPlanner saw neither the
        // source→emitted mapping nor the emitted set and dropped those refs as
        // refr.dangling-base while their base records emitted perfectly well.
        yield return Simple<GenericEsmRecord>("FLOR", FlorEncoder.EncodeNew);
        yield return Simple<GenericEsmRecord>("MSTT", MsttEncoder.EncodeNew);
        yield return Simple<GenericEsmRecord>("ANIO", AnioEncoder.EncodeNew);
        yield return Simple<GenericEsmRecord>("TACT", TactEncoder.EncodeNew);
        yield return Simple<GenericEsmRecord>("ASPC", AspcEncoder.EncodeNew);
        yield return Simple<GenericEsmRecord>("ADDN", AddnEncoder.EncodeNew);

        // Tier 5d — the last ordinary top-level types still emitted by the legacy Phase-3
        // encode path. All four families are plain model-in/bytes-out encoders, so they need
        // nothing beyond a row here and a matching DmpRecordSource extractor row.
        // RADS/DEHY/HUNG/SLPD share one encoder and one model; PlannedEncoderRegistry keys on
        // RecordType, so four DelegatingPlannedEncoder instances over SurvivalStageRecord
        // coexist the same way the GenericEsmRecord ones above do.
        yield return Simple<ClimateRecord>("CLMT", ClmtEncoder.EncodeNew);
        yield return Simple<GrassRecord>("GRAS", GrasEncoder.EncodeNew);
        yield return Simple<ImageSpaceRecord>("IMGS", ImgsEncoder.EncodeNew);
        yield return Simple<SurvivalStageRecord>("RADS", SurvivalStageEncoder.EncodeNew);
        yield return Simple<SurvivalStageRecord>("DEHY", SurvivalStageEncoder.EncodeNew);
        yield return Simple<SurvivalStageRecord>("HUNG", SurvivalStageEncoder.EncodeNew);
        yield return Simple<SurvivalStageRecord>("SLPD", SurvivalStageEncoder.EncodeNew);

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


        // PGRE deliberately has NO row. It is a cell child, so EsmPlanner.CellPipelineOwnedTypes
        // strips it from the top-level catalog whenever CELL is enabled, and with CELL disabled
        // it has no EnumerateModelsByType yield — a top-level planned encoder for it was
        // unreachable by construction and only served to make PGRE look routed in
        // KnownRecordTypes(). Removed 2026-08-07 along with PlannedPgreEncoder/PgreEncoder;
        // verified byte-neutral (record census identical with and without the row).
        // Captured PGREs emit through the cell pipeline since 2026-08-10: they ride the REFR
        // extraction funnel (EsmWorldExtractor/EsmDescriptorScanner) into cell PlacedObjects,
        // then CellChildAllocator → CellChildVerdictPlanner → PlanCellSectionBuilder →
        // PlannedPlacedRefEncoder/RefrEncoder, exactly like REFR/ACHR/ACRE — still no
        // top-level row here.
    }
}
