using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter;
using BethesdaMultitool.Core.Formats.Esm.Planner.Catalog;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Parity;

/// <summary>
///     Guards the three independent production surfaces a top-level record must appear in
///     before the planner can emit it. Nothing derives any of them from another:
///     <list type="number">
///         <item>
///             <description>
///                 <see cref="PlannedEncoders.KnownRecordTypes" /> — the planner's complete
///                 encoder and allocation catalog.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <see cref="PluginConversionPipeline.EmittableTopLevelRecordTypes" /> — the Phase-3 loop
///                 only iterates what <c>EnumerateModelsByType</c> yields, so a type missing here
///                 never reaches the planner dispatch at all.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <see cref="DmpRecordSource.SupportsType" /> — with no extractor row the catalog
///                 holds master-only entries, every one resolves to KeepMaster, and the planner
///                 writes an EMPTY GRUP. This is the dangerous one: an otherwise supported type becomes
///                 a total content drop with zero warnings and zero stats.
///             </description>
///         </item>
///     </list>
/// </summary>
public sealed class PlannerRoutingConsistencyTests
{
    /// <summary>
    ///     Types registered as planned encoders that deliberately do not route through the
    ///     top-level Phase-3 path, with the reason each is exempt.
    /// </summary>
    private static readonly Dictionary<string, string> TopLevelRoutingExemptions = new(StringComparer.Ordinal)
    {
        ["CELL"] =
            "Sentinel: activates the cell hierarchy via EsmAssembler, explicitly excluded at the planner dispatch.",
        ["REFR"] = "Cell child: emits under CELL Children GRUPs, never a top-level GRUP.",
        ["ACHR"] = "Cell child: emits under CELL Children GRUPs, never a top-level GRUP.",
        ["ACRE"] = "Cell child: emits under CELL Children GRUPs, never a top-level GRUP.",
        ["DIAL"] = "DialogGrupBuilder owns DIAL/INFO emission; the plan is consumed only as preallocatedNewFormIds.",
        ["INFO"] = "DialogGrupBuilder owns DIAL/INFO emission; the plan is consumed only as preallocatedNewFormIds."
    };

    [Fact]
    public void FnvSchemaIncompatibleCobj_RemainsForensicButIsNotProductionPlanned()
    {
        Assert.DoesNotContain("COBJ", PlannedEncoders.KnownRecordTypes());

        // Retain discovery/parser reachability so an unexpected capture is visible. Because
        // there is no planned encoder, the production Phase-3 guard reports and skips it.
        Assert.True(DmpRecordSource.SupportsType("COBJ"));
        Assert.Contains("COBJ", PluginConversionPipeline.EmittableTopLevelRecordTypes);
    }

    [Fact]
    public void IncompleteFnvIngredient_RemainsForensicButIsNotProductionPlanned()
    {
        Assert.DoesNotContain("INGR", PlannedEncoders.KnownRecordTypes());

        // Retain ESM/DMP discovery and the Phase-3 diagnostic. The model does not carry
        // FNV's required ENIT/effect group, so emitting a new record would be lossy.
        Assert.True(DmpRecordSource.SupportsType("INGR"));
        Assert.Contains("INGR", PluginConversionPipeline.EmittableTopLevelRecordTypes);
    }

    [Fact]
    public void Every_Planned_Encoder_Type_Has_A_Dmp_Extractor_Row()
    {
        // Without this the planner emits an empty GRUP and every captured record of the type
        // silently disappears — see the class summary.
        var missing = PlannedEncoders.KnownRecordTypes()
            .Where(type => !TopLevelRoutingExemptions.ContainsKey(type))
            .Where(type => !DmpRecordSource.SupportsType(type))
            .OrderBy(type => type, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Planned encoders with no DmpRecordSource.Extractors row (planner would emit an EMPTY GRUP " +
            $"under planner emission): {string.Join(", ", missing)}");
    }

    [Fact]
    public void Every_Planned_Encoder_Type_Is_Reachable_From_The_Phase3_Loop()
    {
        var missing = PlannedEncoders.KnownRecordTypes()
            .Where(type => !TopLevelRoutingExemptions.ContainsKey(type))
            .Where(type => !PluginConversionPipeline.EmittableTopLevelRecordTypes.Contains(type))
            .OrderBy(type => type, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Planned encoders not yielded by EnumerateModelsByType (the planner dispatch is never " +
            $"reached, so the type is dropped with no diagnostic): {string.Join(", ", missing)}");
    }

    [Fact]
    public void Every_Planned_Encoder_Type_Retains_A_Direct_Model_Encoder()
    {
        var registry = RecordEncoderRegistry.CreateDefault();

        var missing = PlannedEncoders.KnownRecordTypes()
            .Where(type => !TopLevelRoutingExemptions.ContainsKey(type))
            .Where(type => registry.Get(type) is null)
            .OrderBy(type => type, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Planned encoders with no direct RecordEncoderRegistry model primitive: " +
            string.Join(", ", missing));
    }

    /// <summary>
    ///     Types the generic runtime sweep (<c>RecordParserContext.MergeRuntimeGenericRecords</c>
    ///     via <c>RuntimeGenericReader</c>) reads out of dumps but that the pipeline's top-level
    ///     loop does not yield, each with the reason it is allowed to stay unrouted. Every entry
    ///     is a captured record class that today silently never reaches the output ESM — the M1
    ///     guard below keeps this set explicit instead of silent.
    /// </summary>
    private static readonly Dictionary<string, string> GenericSweepEmissionExemptions = new(StringComparer.Ordinal)
    {
        // LSCR / CHIP / IDLM / CAMS / MSET left this list 2026-08-26: all five now have an encoder,
        // a DmpRecordSource row, a planned-encoder row, a registry row, and an
        // EnumerateModelsByType yield. MSET additionally gained RuntimeMediaSetReader so its six
        // pointer-backed layer names can be recovered at all.
        // EFSH / RGDL / CSNO left this list 2026-08-26 (round 3): each carries its payload in one
        // block whose runtime size matches the file schema exactly, so the existing BE→LE registry
        // converts it and no new decode was needed.
        //
        // IPDS and DOBJ left this list once pdb_layouts.json was regenerated with LF_ARRAY leaves
        // resolved: their sole payload fields (BGSImpactDataSet.ppImpactData @44,
        // BGSDefaultObjectManager.pObjectArray @40) used to export as size:0 / kind:"unknown" and
        // were dropped by GetReadableFields before any reader ran. Both are now inline pointer
        // arrays whose slot counts match their file schemas exactly (12 materials, 34 defaults).
        ["AMEF"] =
            "zero records corpus-wide across all 32 dumps (census2026-08-25) — nothing to route",
        ["SKIL"] = "not part of the FNV file format — xEdit wbDefinitionsFNV has no record block",
        ["CLOT"] = "not part of the FNV file format — xEdit wbDefinitionsFNV has no record block",
        ["LVSP"] = "not part of the FNV file format — xEdit wbDefinitionsFNV has no record block",
        // TLOD (0x44) left this list 2026-08-25: I4c PDB-verified that the engine registers
        // TESObjectLAND (runtime terrain) under TLOD_ID, so 0x44 is now a SpecializedFormType
        // (RuntimeWorldReader) and no longer flows through the generic sweep.
        ["TES4"] = "file header form — Tes4HeaderBuilder synthesizes the plugin header; never routed as a record",
        ["NAVI"] = "emitted outside the top-level loop — EsmAssembler's NAVI fallback builds it from emitted NAVMs",
        ["NAVM"] = "cell child: emits under CELL Children GRUPs via the NAVM byte-rewriter, never a top-level GRUP",
        ["PMIS"] = "placed-ref type — routes through cell children, not top-level yields",
        ["PGRE"] = "placed-ref type — routes through cell children, not top-level yields",
        ["PBEA"] = "placed-ref type — routes through cell children, not top-level yields",
        ["PFLA"] = "placed-ref type — routes through cell children, not top-level yields"
    };

    /// <summary>
    ///     M1 guard: every FormType the generic runtime sweep can read out of a dump must
    ///     either be yielded by the pipeline's top-level loop or sit on
    ///     <see cref="GenericSweepEmissionExemptions" /> with a written reason. The oracle
    ///     mirrors the sweep's own gates: a PDB layout exists, no specialized reader claims
    ///     the FormType, and <c>RuntimeGenericReader</c>'s readable-field early-out passes.
    ///     Without this, a captured record class disappears with zero diagnostics — the
    ///     catalog never sees a model the pipeline never yields.
    /// </summary>
    [Fact]
    public void Every_Generic_Sweep_FormType_Is_Yielded_Or_Named_Exempt()
    {
        var missing = new List<string>();
        foreach (var formType in PdbStructLayouts.Layouts.Keys.OrderBy(b => b))
        {
            if (PdbStructLayouts.HasSpecializedReader(formType))
            {
                continue; // A typed reader owns it; the generic sweep skips it.
            }

            if (PdbStructLayouts.GetReadableFields(formType).Count == 0)
            {
                continue; // RuntimeGenericReader early-outs; no record is ever produced.
            }

            // ASPC is intercepted inside RuntimeStructReader.ReadGenericRecord and routed to
            // the specialized acoustic-space reader (it is deliberately NOT in
            // SpecializedFormTypes — see that method's doc comment). The 0x0E byte is
            // hardcoded here because the AspcFormType const is private.
            if (formType == 0x0E)
            {
                continue;
            }

            var signature = RuntimeBuildOffsets.GetRecordTypeCode(formType);
            Assert.True(signature is not null,
                $"FormType 0x{formType:X2} has a PDB layout the generic sweep can read but no " +
                "ENUM_FORM_ID signature in RuntimeBuildOffsets.GetRecordTypeCode — extend the " +
                "mapping so its routing can be audited.");

            if (PluginConversionPipeline.EmittableTopLevelRecordTypes.Contains(signature!)
                || GenericSweepEmissionExemptions.ContainsKey(signature!))
            {
                continue;
            }

            missing.Add($"{signature} (0x{formType:X2})");
        }

        Assert.True(
            missing.Count == 0,
            "FormTypes the generic runtime sweep reads but the pipeline never yields and no " +
            "exemption names (captured records of these types silently vanish): " +
            string.Join(", ", missing));
    }

    /// <summary>
    ///     Anti-staleness inverse of the M1 guard: once a type gains a top-level yield its
    ///     exemption must be deleted, so the exemption list cannot silently outlive the gap
    ///     it documents.
    /// </summary>
    [Fact]
    public void Generic_Sweep_Exemptions_Name_Only_Types_The_Pipeline_Does_Not_Yield()
    {
        var wired = GenericSweepEmissionExemptions.Keys
            .Where(type => PluginConversionPipeline.EmittableTopLevelRecordTypes.Contains(type))
            .OrderBy(type => type, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            wired.Count == 0,
            "Exempted generic-sweep types that EnumerateModelsByType now yields — remove their " +
            $"GenericSweepEmissionExemptions entries: {string.Join(", ", wired)}");
    }

    [Fact]
    public void Routing_Exemptions_Name_Only_Real_Planned_Encoder_Types()
    {
        // Keeps the exemption list from silently outliving the encoders it excuses.
        var known = PlannedEncoders.KnownRecordTypes().ToHashSet(StringComparer.Ordinal);
        var stale = TopLevelRoutingExemptions.Keys
            .Where(type => !known.Contains(type))
            .OrderBy(type => type, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            stale.Count == 0,
            $"Exemptions for record types that are no longer registered planned encoders: {string.Join(", ", stale)}");
    }
}