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
        Assert.Contains(plan.Diagnostics, diagnostic =>
            diagnostic.Code == "script.suppress-unsafe-reference-table"
            && diagnostic.Message.Contains("SCRO[0]=0x00ABCDEF unresolved", StringComparison.Ordinal));
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
        Assert.Contains(plan.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("SCRV[0]=5 has no matching SLSD", StringComparison.Ordinal));
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
