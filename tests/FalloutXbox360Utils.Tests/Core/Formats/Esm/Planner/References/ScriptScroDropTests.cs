using System.Collections.Generic;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Catalog;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Disposition;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.References;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.References.Walkers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.References;

/// <summary>
///     Pins the v54 SCRO-dangle fix: when an SCPT's referenced FormID is neither in the
///     master set nor in the source→emitted map, the planner drops that SCRO subrecord
///     instead of remapping it to a never-emitted plugin FormID.
/// </summary>
public sealed class ScriptScroDropTests
{
    [Fact]
    public void Dangling_Scro_Becomes_Drop_Subrecord_Action()
    {
        var policy = new DegradationPolicy();
        policy.SetDefaultForType("SCPT", DanglingAction.DropSubrecord);
        var resolver = new ReferenceResolver([new ScriptReferenceWalker()], policy);

        var script = new ScriptRecord
        {
            FormId = 0x0014DA58,
            ReferencedObjects =
            [
                0x000ABCDEu, // Master — resolves.
                0x01FFFFFFu, // Plugin-range dangle — must drop.
            ],
        };
        var entry = new CatalogEntry
        {
            Type = "SCPT",
            Source = SourceKind.DmpOverride,
            MasterFormId = 0x0014DA58,
            DmpFormId = 0x0014DA58,
            Model = script,
        };
        var decision = new DispositionDecision
        {
            Disposition = RecordDisposition.Override,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" },
        };

        var emitted = new HashSet<uint> { 0x0014DA58u, 0x000ABCDEu };
        var resolvedByIndex = resolver.ResolveAll(
            [(entry, decision)], emitted, new Dictionary<uint, uint>());

        var refs = resolvedByIndex[0];
        Assert.Equal(2, refs.Length);

        var resolved = Assert.Single(refs, r => r.FieldPath == "SCRO[0]");
        Assert.Equal(ResolvedRefAction.Resolved, resolved.Action);
        Assert.Equal(0x000ABCDEu, resolved.FinalFormId);

        var dropped = Assert.Single(refs, r => r.FieldPath == "SCRO[1]");
        Assert.Equal(ResolvedRefAction.DropSubrecord, dropped.Action);
        Assert.Null(dropped.FinalFormId);
        Assert.NotNull(dropped.Reason);
    }

    [Fact]
    public void Source_Mapped_Scro_Resolves_Via_Allocation_Table()
    {
        var policy = new DegradationPolicy();
        policy.SetDefaultForType("SCPT", DanglingAction.DropSubrecord);
        var resolver = new ReferenceResolver([new ScriptReferenceWalker()], policy);

        // DMP captured FormID 0x00ABCDEF; allocator gave it 0x01000800. The plan must
        // resolve the SCRO to 0x01000800.
        var script = new ScriptRecord
        {
            FormId = 0x0014DA58,
            ReferencedObjects = [0x00ABCDEFu],
        };
        var entry = new CatalogEntry
        {
            Type = "SCPT",
            Source = SourceKind.DmpOverride,
            MasterFormId = 0x0014DA58,
            DmpFormId = 0x0014DA58,
            Model = script,
        };
        var decision = new DispositionDecision
        {
            Disposition = RecordDisposition.Override,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" },
        };

        var emitted = new HashSet<uint> { 0x0014DA58u, 0x01000800u };
        var sourceToEmitted = new Dictionary<uint, uint> { [0x00ABCDEFu] = 0x01000800u };

        var resolvedByIndex = resolver.ResolveAll(
            [(entry, decision)], emitted, sourceToEmitted);

        var resolvedRef = Assert.Single(resolvedByIndex[0]);
        Assert.Equal(ResolvedRefAction.Resolved, resolvedRef.Action);
        Assert.Equal(0x01000800u, resolvedRef.FinalFormId);
    }
}
