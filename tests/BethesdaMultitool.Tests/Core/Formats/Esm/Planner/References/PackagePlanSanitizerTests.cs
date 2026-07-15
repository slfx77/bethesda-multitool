using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.References;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.References;

public sealed class PackagePlanSanitizerTests
{
    private const uint PackFormId = 0x01001000;
    private const uint TargetFormId = 0x01002000;

    [Fact]
    public void Apply_suppresses_new_package_with_zero_structural_target()
    {
        var pack = MakePack(new PackageTarget { Type = 0, FormIdOrType = 0 });
        var records = ImmutableArray.Create(MakePlan("PACK", PackFormId, pack));

        var result = PackagePlanSanitizer.Apply(
            records, ImmutableHashSet.Create(PackFormId),
            ImmutableDictionary<uint, uint>.Empty, [], null);

        Assert.Equal(RecordDisposition.Skip, Assert.Single(result.Records).Disposition);
        Assert.Contains(PackFormId, result.SuppressedNewFormIds);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "references.skip.pack-invalid-target");
    }

    [Fact]
    public void Apply_remaps_live_type_compatible_target_and_keeps_package()
    {
        const uint sourceTarget = 0x00123456;
        var pack = MakePack(new PackageTarget { Type = 0, FormIdOrType = sourceTarget });
        var records = ImmutableArray.Create(
            MakePlan("PACK", PackFormId, pack),
            MakePlan("REFR", TargetFormId, new object()));
        var remap = ImmutableDictionary<uint, uint>.Empty.Add(sourceTarget, TargetFormId);

        var result = PackagePlanSanitizer.Apply(
            records, ImmutableHashSet.Create(PackFormId, TargetFormId), remap, [], null);

        var retained = Assert.Single(result.Records, record => record.Type == "PACK");
        Assert.Equal(RecordDisposition.New, retained.Disposition);
        Assert.Equal(TargetFormId, Assert.IsType<PackageRecord>(retained.Model).Target!.FormIdOrType);
        Assert.Empty(result.SuppressedNewFormIds);
    }

    [Fact]
    public void Apply_suppresses_live_target_of_wrong_record_type()
    {
        var pack = MakePack(new PackageTarget { Type = 0, FormIdOrType = TargetFormId });
        var records = ImmutableArray.Create(
            MakePlan("PACK", PackFormId, pack),
            MakePlan("CELL", TargetFormId, new object()));

        var result = PackagePlanSanitizer.Apply(
            records, ImmutableHashSet.Create(PackFormId, TargetFormId),
            ImmutableDictionary<uint, uint>.Empty, [], null);

        Assert.Equal(
            RecordDisposition.Skip,
            Assert.Single(result.Records, record => record.Type == "PACK").Disposition);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("resolves to CELL", StringComparison.Ordinal));
    }

    private static PackageRecord MakePack(PackageTarget target) => new()
    {
        FormId = PackFormId,
        EditorId = "TestPackage",
        Data = new PackageData(),
        Target = target,
    };

    private static RecordPlan MakePlan(string type, uint formId, object model) => new()
    {
        Type = type,
        Disposition = RecordDisposition.New,
        FormId = formId,
        SourceFormId = formId,
        Model = model,
        References = ImmutableArray<ResolvedRef>.Empty,
        ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
        Provenance = new PlanProvenance { PolicyId = "test", Reason = "package integrity" },
    };
}
