using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Disposition;
using BethesdaMultitool.Core.Formats.Esm.Planner.Disposition.Policies;
using BethesdaMultitool.Core.Formats.Esm.Planner.References;
using BethesdaMultitool.Core.Formats.Esm.Planner.References.Walkers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner;

public sealed class ScriptReferenceSafetyPlannerTests
{
    [Fact]
    public void Build_TreatsEnginePlayerRefAsLiveScro()
    {
        var records = new RecordCollection();
        records.Scripts.Add(new ScriptRecord
        {
            FormId = 0x00100000,
            EditorId = "PlayerScript",
            ReferencedObjects = [0x00000014]
        });

        var plan = BuildPlanner().Build(
            [], records, new HashSet<string> { "SCPT" }, new HashSet<uint>(), null);

        var script = Assert.Single(plan.Records, record => record.Type == "SCPT");
        var reference = Assert.Single(script.References);
        Assert.Equal(ResolvedRefAction.Resolved, reference.Action);
        Assert.Equal(0x00000014u, reference.FinalFormId);
        Assert.Contains(0x00000014u, plan.EmittedFormIds);
    }

    [Fact]
    public void Build_SuppressesNewScriptAndPrunesAliasWhenScroDangles()
    {
        var records = new RecordCollection();
        records.Scripts.Add(new ScriptRecord
        {
            FormId = 0x00100000,
            EditorId = "DanglingScript",
            ReferencedObjects = [0x00ABCDEF]
        });

        var plan = BuildPlanner().Build(
            [], records, new HashSet<string> { "SCPT" }, new HashSet<uint>(), null);

        Assert.DoesNotContain(plan.Records, record => record.Type == "SCPT");
        Assert.DoesNotContain(0x00100000u, plan.SourceToEmittedFormId.Keys);
        var diagnostic = Assert.Single(plan.Diagnostics, diagnostic =>
            diagnostic.Code == "script.suppress-unsafe-reference-table"
            && diagnostic.Message.Contains(
                "[script-source=0x00100000;script-emitted=0x01000800;script-edid=DanglingScript]",
                StringComparison.Ordinal)
            && diagnostic.Message.Contains(
                "SCRO[0][target-source=0x00ABCDEF;target-emitted=<none>;action=DropSubrecord] unresolved",
                StringComparison.Ordinal));
        Assert.Equal("DanglingScript", diagnostic.Metadata!["script-editor-id"]);
        Assert.Equal("0x00100000", diagnostic.Metadata["script-source-form-id"]);
        Assert.Equal("0x01000800", diagnostic.Metadata["script-emitted-form-id"]);
        Assert.Equal("SCRO[0]", diagnostic.Metadata["reference-field"]);
        Assert.Equal("0x00ABCDEF", diagnostic.Metadata["target-source-form-id"]);
        Assert.Null(diagnostic.Metadata["target-emitted-form-id"]);
    }

    [Fact]
    public void Build_SuppressesNewScriptWhenScrvHasNoMatchingLocal()
    {
        var records = new RecordCollection();
        records.Scripts.Add(new ScriptRecord
        {
            FormId = 0x00100000,
            EditorId = "BadLocalScript",
            ReferencedObjects = [0x80000005]
        });

        var plan = BuildPlanner().Build(
            [], records, new HashSet<string> { "SCPT" }, new HashSet<uint>(), null);

        Assert.DoesNotContain(plan.Records, record => record.Type == "SCPT");
        var diagnostic = Assert.Single(plan.Diagnostics, diagnostic =>
            diagnostic.Message.Contains(
                "[script-source=0x00100000;script-emitted=0x01000800;script-edid=BadLocalScript]",
                StringComparison.Ordinal)
            && diagnostic.Message.Contains(
                "SCRV[0][local-id=5] has no matching SLSD", StringComparison.Ordinal));
        Assert.Equal("SCRV[0]", diagnostic.Metadata!["reference-field"]);
        Assert.Equal("5", diagnostic.Metadata["local-variable-id"]);
    }

    private static EsmPlanner BuildPlanner()
    {
        var disposition = new DispositionEngine([new DefaultDispositionPolicy()]);
        var degradation = new DegradationPolicy();
        degradation.SetDefaultForType("SCPT", DanglingAction.DropSubrecord);
        var references = new ReferenceResolver([new ScriptReferenceWalker()], degradation);
        return new EsmPlanner(disposition, new FormIdAllocator(), references);
    }
}
