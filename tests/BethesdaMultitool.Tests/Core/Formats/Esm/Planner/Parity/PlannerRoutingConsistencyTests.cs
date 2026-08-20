using BethesdaMultitool.Core.Formats.Esm.PlannedWriter;
using BethesdaMultitool.Core.Formats.Esm.Planner.Catalog;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
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