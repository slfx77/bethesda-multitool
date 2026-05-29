using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Disposition;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Disposition.Policies;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.References;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner;

/// <summary>
///     Tier 0 baseline: with no record types enabled, the planner produces an empty plan.
///     With a type enabled but no DMP records of that type and no encoders registered, the
///     planner still produces an empty plan (no Records, no diagnostics, no allocations).
/// </summary>
public sealed class EsmPlannerTier0Tests
{
    [Fact]
    public void Build_With_Empty_EnabledTypes_Returns_Empty_Plan()
    {
        var planner = BuildPlanner();

        var plan = planner.Build(
            masterRecords: [],
            dmpRecords: new RecordCollection(),
            enabledTypes: new HashSet<string>(),
            masterFormIds: new HashSet<uint>(),
            masterPath: null);

        Assert.Empty(plan.Records);
        Assert.Empty(plan.EmittedFormIds);
        Assert.Empty(plan.SourceToEmittedFormId);
        Assert.Empty(plan.Diagnostics);
        Assert.Empty(plan.Meta.PlannerCoverage);
    }

    [Fact]
    public void Build_With_EnabledType_But_No_Inputs_Returns_Empty_Plan()
    {
        var planner = BuildPlanner();

        var plan = planner.Build(
            masterRecords: [],
            dmpRecords: new RecordCollection(),
            enabledTypes: new HashSet<string> { "WEAP" },
            masterFormIds: new HashSet<uint>(),
            masterPath: "test.esm");

        Assert.Empty(plan.Records);
        Assert.Empty(plan.EmittedFormIds);
        Assert.Equal("test.esm", plan.Meta.MasterPath);
        Assert.Contains("WEAP", plan.Meta.PlannerCoverage);
    }

    [Fact]
    public void Build_Preserves_NextObjectId_When_No_Allocations_Happen()
    {
        var allocator = new FormIdAllocator(0x800);
        var planner = BuildPlanner(allocator);

        var plan = planner.Build(
            masterRecords: [],
            dmpRecords: new RecordCollection(),
            enabledTypes: new HashSet<string>(),
            masterFormIds: new HashSet<uint>(),
            masterPath: null);

        Assert.Equal(0x800u, plan.Meta.NextObjectId);
    }

    private static EsmPlanner BuildPlanner(FormIdAllocator? allocator = null)
    {
        var disposition = new DispositionEngine([new DefaultDispositionPolicy()]);
        var degradation = new DegradationPolicy();
        var references = new ReferenceResolver([], degradation);

        return new EsmPlanner(disposition, allocator ?? new FormIdAllocator(0x800), references);
    }
}
