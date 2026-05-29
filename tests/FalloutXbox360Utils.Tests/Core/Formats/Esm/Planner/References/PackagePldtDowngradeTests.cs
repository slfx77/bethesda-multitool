using System.Collections.Generic;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Catalog;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Disposition;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.References;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.References.Walkers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.References;

/// <summary>
///     Pins the v51 PACK PLDT degradation fix in planner terms: when PLDT.Union references
///     a FormID that isn't in the emit set, the resolver returns
///     <see cref="ResolvedRefAction.DowngradeContainer" /> rather than dropping or null-ing.
/// </summary>
public sealed class PackagePldtDowngradeTests
{
    [Fact]
    public void Dangling_Pldt_Union_Triggers_Container_Downgrade()
    {
        var policy = new DegradationPolicy();
        policy.SetDefaultForType("PACK", DanglingAction.DropSubrecord);
        policy.SetRule(
            "PACK",
            "PLDT.Union",
            DanglingAction.DowngradeContainer(new ContainerDowngrade
            {
                ContainerSignature = "PLDT",
                FromShape = "Type 0",
                ToShape = "Type 2",
            }));
        var resolver = new ReferenceResolver([new PackageReferenceWalker()], policy);

        var pack = new PackageRecord
        {
            FormId = 0x000ABCDE,
            Location = new PackageLocation { Type = 0, Union = 0x01FFFFFF }, // Dangle.
        };
        var entry = new CatalogEntry
        {
            Type = "PACK",
            Source = SourceKind.DmpOverride,
            MasterFormId = 0x000ABCDE,
            DmpFormId = 0x000ABCDE,
            Model = pack,
        };
        var decision = new DispositionDecision
        {
            Disposition = RecordDisposition.Override,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" },
        };
        var emitted = new HashSet<uint> { 0x000ABCDE };

        var resolved = resolver.ResolveAll([(entry, decision)], emitted, new Dictionary<uint, uint>());
        var refs = resolved[0];

        var pldtRef = Assert.Single(refs, r => r.FieldPath == "PLDT.Union");
        Assert.Equal(ResolvedRefAction.DowngradeContainer, pldtRef.Action);
        Assert.NotNull(pldtRef.Downgrade);
        Assert.Equal("PLDT", pldtRef.Downgrade.ContainerSignature);
    }

    [Fact]
    public void Resolved_Pldt_Union_Does_Not_Downgrade()
    {
        var policy = new DegradationPolicy();
        policy.SetRule(
            "PACK",
            "PLDT.Union",
            DanglingAction.DowngradeContainer(new ContainerDowngrade
            {
                ContainerSignature = "PLDT",
                FromShape = "Type 0",
                ToShape = "Type 2",
            }));
        var resolver = new ReferenceResolver([new PackageReferenceWalker()], policy);

        var pack = new PackageRecord
        {
            FormId = 0x000ABCDE,
            Location = new PackageLocation { Type = 0, Union = 0x000ABCDF }, // In emit set.
        };
        var entry = new CatalogEntry
        {
            Type = "PACK",
            Source = SourceKind.DmpOverride,
            MasterFormId = 0x000ABCDE,
            DmpFormId = 0x000ABCDE,
            Model = pack,
        };
        var decision = new DispositionDecision
        {
            Disposition = RecordDisposition.Override,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" },
        };
        var emitted = new HashSet<uint> { 0x000ABCDE, 0x000ABCDF };

        var resolved = resolver.ResolveAll([(entry, decision)], emitted, new Dictionary<uint, uint>());
        var pldtRef = Assert.Single(resolved[0], r => r.FieldPath == "PLDT.Union");

        Assert.Equal(ResolvedRefAction.Resolved, pldtRef.Action);
        Assert.Equal(0x000ABCDFu, pldtRef.FinalFormId);
    }
}
