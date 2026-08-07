using BethesdaMultitool.Core.Formats.Esm.PlannedWriter;
using BethesdaMultitool.Core.Formats.Esm.Planner.Catalog;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Parity;

/// <summary>
///     Guards the four independent per-type tables a record has to appear in before
///     <c>--planner-types all</c> can actually emit it. Nothing derives any of them from any
///     other, and the failure when they disagree is silent:
///     <list type="number">
///         <item>
///             <description>
///                 <see cref="PlannedEncoders.KnownRecordTypes" /> — what <c>all</c> expands to,
///                 and therefore what <c>PluginBuildOptions.PlannerEnabledRecordTypes</c> routes
///                 to the planner.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <see cref="PluginBuilder.EmittableTopLevelRecordTypes" /> — the Phase-3 loop
///                 only iterates what <c>EnumerateModelsByType</c> yields, so a type missing here
///                 never reaches the planner dispatch at all.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <see cref="RecordEncoderRegistry" /> — the legacy encoder lookup runs BEFORE
///                 the planner branch and <c>continue</c>s the whole type when it misses.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <see cref="DmpRecordSource.SupportsType" /> — with no extractor row the catalog
///                 holds master-only entries, every one resolves to KeepMaster, and the planner
///                 writes an EMPTY GRUP. This is the dangerous one: a working legacy type becomes
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
        ["CELL"] = "Sentinel: activates the cell hierarchy via EsmAssembler, explicitly excluded at the planner dispatch.",
        ["REFR"] = "Cell child: emits under CELL Children GRUPs, never a top-level GRUP.",
        ["ACHR"] = "Cell child: emits under CELL Children GRUPs, never a top-level GRUP.",
        ["ACRE"] = "Cell child: emits under CELL Children GRUPs, never a top-level GRUP.",
        ["PGRE"] = "Encoder shipped ahead of routing — no PGRE→parent-cell mapping on the model yet.",
        ["DIAL"] = "DialogGrupBuilder owns DIAL/INFO emission; the plan is consumed only as preallocatedNewFormIds.",
        ["INFO"] = "DialogGrupBuilder owns DIAL/INFO emission; the plan is consumed only as preallocatedNewFormIds."
    };

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
            $"under --planner-types all): {string.Join(", ", missing)}");
    }

    [Fact]
    public void Every_Planned_Encoder_Type_Is_Reachable_From_The_Phase3_Loop()
    {
        var missing = PlannedEncoders.KnownRecordTypes()
            .Where(type => !TopLevelRoutingExemptions.ContainsKey(type))
            .Where(type => !PluginBuilder.EmittableTopLevelRecordTypes.Contains(type))
            .OrderBy(type => type, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Planned encoders not yielded by EnumerateModelsByType (the planner dispatch is never " +
            $"reached, so the type is dropped with no diagnostic): {string.Join(", ", missing)}");
    }

    [Fact]
    public void Every_Planned_Encoder_Type_Also_Has_A_Legacy_Encoder()
    {
        var registry = RecordEncoderRegistry.CreateDefault();

        var missing = PlannedEncoders.KnownRecordTypes()
            .Where(type => !TopLevelRoutingExemptions.ContainsKey(type))
            .Where(type => registry.Get(type) is null)
            .OrderBy(type => type, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Planned encoders with no RecordEncoderRegistry entry (the legacy lookup runs first and " +
            $"skips the type before the planner branch): {string.Join(", ", missing)}");
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
