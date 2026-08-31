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
///     The legacy emission path and its per-type opt-in were retired 2026-08-11. This is the
///     central encoder catalog, but not a complete reachability oracle: a production top-level
///     type also needs a <c>DmpRecordSource</c> extractor and an
///     <c>PluginConversionPipeline.EnumerateModelsByType</c> entry. Cell-owned types use the
///     separate cell-section path. <c>PlannerRoutingConsistencyTests</c> guards the required
///     agreement between those surfaces.
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
    public static IEnumerable<string> KnownRecordTypes()
    {
        return BuildAll().Select(e => e.RecordType).Distinct(StringComparer.Ordinal);
    }

    /// <summary>
    ///     One-line registration for a simple-ref encoder: New delegates to the existing
    ///     <c>EncodeNew(model)</c> model primitive, Override emits an empty record. This
    ///     reuses encoder code; it does not invoke the retired legacy emission path.
    /// </summary>
    private static DelegatingPlannedEncoder<TModel> Simple<TModel>(
        string recordType, Func<TModel, EncodedRecord> encodeNew) where TModel : class
    {
        return new DelegatingPlannedEncoder<TModel>(recordType, encodeNew);
    }

    /// <summary>
    ///     Enumerate every planned encoder. Tier 1+ extends this list.
    /// </summary>
    public static IEnumerable<IPlannedRecordEncoder> BuildAll()
    {
        // Tier 1 — early static-data encoders. ALCH is the exception to the otherwise
        // reference-free group: its planned wrapper consumes explicit top-level/effect
        // reference decisions before invoking the model serializer.
        yield return new PlannedStatEncoder();
        yield return new PlannedGlobEncoder();
        yield return new PlannedGmstEncoder();
        yield return new PlannedArmoEncoder();
        yield return new PlannedAmmoEncoder();
        yield return new PlannedBookEncoder();
        yield return new PlannedAlchEncoder();

        // Tier 2 — FormID-bearing encoders. Most still emit FormIDs verbatim; WEAP threads
        // transitional whole-plan sets through its existing overload, while ENCH/SPEL consume
        // explicit per-effect resolutions from RecordPlan.
        yield return new PlannedWeapEncoder();
        yield return Simple<DoorRecord>("DOOR", DoorEncoder.EncodeNew);
        yield return Simple<MiscItemRecord>("MISC", MiscEncoder.EncodeNew);
        yield return Simple<KeyRecord>("KEYM", KeymEncoder.EncodeNew);
        yield return Simple<NoteRecord>("NOTE", NoteEncoder.EncodeNew);
        yield return Simple<RecipeRecord>("RCPE", RcpeEncoder.EncodeNew);
        // COBJ is deliberately not production-routed. Current FNV xEdit defines it as a
        // MISC-like base object, while the retained forensic model/byte builder is a
        // cross-generation hybrid containing Skyrim-style recipe fields. Keeping COBJ out
        // of KnownRecordTypes makes Phase 3 warn and skip an unexpected capture instead of
        // emitting schema-incompatible bytes into a FalloutNV.esm-based plugin.
        yield return Simple<ArmaRecord>("ARMA", ArmaEncoder.EncodeNew);
        yield return Simple<WeaponModRecord>("IMOD", ImodEncoder.EncodeNew);
        yield return new PlannedEnchEncoder();
        yield return new PlannedSpelEncoder();
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
        // INGR is deliberately not production-routed. FNV stores Weight in a four-byte
        // DATA subrecord, then requires a separate ENIT block and effect group. The typed
        // model retains only identity, weight and equipment type, so it cannot reconstruct
        // a valid new ingredient. Retail's sole row is even named
        // DoNotCreateNewIngredientsWeArentUsingThemInFallout. Phase 3 keeps a capture visible
        // and warns/skips it.
        yield return Simple<ImpactDataRecord>("IPCT", IpctEncoder.EncodeNew);
        yield return Simple<LandscapeTextureRecord>("LTEX", LtexEncoder.EncodeNew);
        yield return Simple<MenuIconRecord>("MICN", MicnEncoder.EncodeNew);
        yield return Simple<MusicTypeRecord>("MUSC", MuscEncoder.EncodeNew);
        yield return Simple<RecipeCategoryRecord>("RCCT", RcctEncoder.EncodeNew);
        yield return Simple<TextureSetRecord>("TXST", TxstEncoder.EncodeNew);
        yield return Simple<ActivatorRecord>("ACTI", ActiEncoder.EncodeNew);
        yield return Simple<DebrisRecord>("DEBR", DebrEncoder.EncodeNew);
        yield return Simple<CombatStyleRecord>("CSTY", CstyEncoder.EncodeNew);

        // Tier 3 — complex FormID-ref encoders. Planned wrappers reuse the existing
        // EncodeNew(model, validFormIds, remapTable) primitives while FormID resolution
        // comes from the plan's emit set. Reusing those primitives is not legacy routing;
        // every disposition and allocation is already settled in EmitPlan.
        yield return new PlannedImadEncoder();
        yield return new PlannedScptEncoder();
        yield return new PlannedPerkEncoder();
        yield return new PlannedContEncoder();
        yield return new PlannedIdleEncoder();
        yield return new PlannedTermEncoder();
        yield return new PlannedLvliEncoder();
        yield return new PlannedLvliEncoder("LVLN");
        yield return new PlannedLvliEncoder("LVLC");
        yield return new PlannedNpcEncoder();
        yield return new PlannedCreaEncoder();
        yield return new PlannedQustEncoder();
        yield return new PlannedInfoEncoder();

        // Tier 4 — cross-record coordination encoders. PACK still applies PLDT degradation
        // inside its model encoder; planner-side downgrade via
        // ResolvedRefAction.DowngradeContainer remains a separate refinement. REFR/ACHR/ACRE
        // (placed refs) emit under CELL Children GRUPs through the cell-section path.
        yield return new PlannedPackEncoder();
        yield return new PlannedCpthEncoder();
        yield return new PlannedDialEncoder();
        yield return new PlannedMesgEncoder();

        // Tier 5a — remaining top-level world / misc encoders. Cell-owned types
        // (REFR/ACHR/ACRE/PGRE/LAND/NAVM) are planned and serialized through
        // PlanCellSectionBuilder rather than this top-level list.
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
        // the ESM carve path (keyed by subrecord signature), which is why their underlying
        // model encoders take GenericEsmRecord and read through GenericRecordFields.
        // PlannedEncoderRegistry keys on RecordType rather than model CLR type, so several
        // GenericEsmRecord-backed encoders coexist here without colliding.
        //
        // Historical migration note (2026-08-06): adding these rows fixed REFRs on
        // proto-only MSTT/TACT bases. The retired emitter had allocated their FormIDs after
        // planning, so CellChildVerdictPlanner could not see the source→emitted mapping and
        // dropped the refs as refr.dangling-base. Planner-all now allocates them up front.
        yield return Simple<GenericEsmRecord>("FLOR", FlorEncoder.EncodeNew);
        yield return Simple<GenericEsmRecord>("MSTT", MsttEncoder.EncodeNew);
        yield return Simple<GenericEsmRecord>("ANIO", AnioEncoder.EncodeNew);
        yield return Simple<GenericEsmRecord>("TACT", TactEncoder.EncodeNew);
        yield return Simple<GenericEsmRecord>("ASPC", AspcEncoder.EncodeNew);
        yield return Simple<GenericEsmRecord>("ADDN", AddnEncoder.EncodeNew);
        // Wired 2026-08-26 (adversarial recovery audit M1). Same GenericEsmRecord shape; the
        // pipeline yields them after the types their references point at (LSCT for LSCR.WMI1,
        // SOUN for CHIP.YNAM/ZNAM and MSET.HNAM/INAM, IMAD for CAMS.MNAM).
        yield return Simple<GenericEsmRecord>("LSCR", LscrEncoder.EncodeNew);
        yield return Simple<GenericEsmRecord>("CHIP", ChipEncoder.EncodeNew);
        yield return Simple<GenericEsmRecord>("IDLM", IdlmEncoder.EncodeNew);
        yield return Simple<GenericEsmRecord>("CAMS", CamsEncoder.EncodeNew);
        yield return Simple<GenericEsmRecord>("MSET", MsetEncoder.EncodeNew);
        // Round 3 of the same audit — the last M1 types whose payload is reachable without
        // regenerating the PDB layout database.
        yield return Simple<GenericEsmRecord>("EFSH", EfshEncoder.EncodeNew);
        yield return Simple<GenericEsmRecord>("RGDL", RgdlEncoder.EncodeNew);
        yield return Simple<GenericEsmRecord>("CSNO", CsnoEncoder.EncodeNew);
        yield return Simple<GenericEsmRecord>("IPDS", IpdsEncoder.EncodeNew);
        yield return Simple<GenericEsmRecord>("DOBJ", DobjEncoder.EncodeNew);

        // Tier 5d — historical final migration from the retired Phase-3 per-model encoder.
        // All four families are plain model-in/bytes-out encoders, so planner routing needs
        // a row here plus a matching DmpRecordSource extractor row.
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

        // Tier 5b catalog entries retained for isolated encoder/parity use. Production does
        // not dispatch CELL or placed refs as top-level GRUPs: PlanCellSectionBuilder emits
        // the WRLD/CELL hierarchy and its REFR/ACHR/ACRE/PGRE children from CellPlan.
        // LAND and NAVM are also planner-owned there, using PlannedLandEncoder and the
        // specialized NAVM byte-rewriter rather than standard top-level encoders.
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
