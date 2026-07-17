using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Catalog;
using BethesdaMultitool.Core.Formats.Esm.Planner.Disposition;
using BethesdaMultitool.Core.Formats.Esm.Planner.Disposition.Policies;
using BethesdaMultitool.Core.Formats.Esm.Planner.References;
using BethesdaMultitool.Core.Formats.Esm.Planner.References.Walkers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner;

public sealed class ImageSpaceModifierScriptClosureTests
{
    private const uint ModifierSourceFormId = 0x000CDA79;
    private const uint ScriptSourceFormId = 0x000CDA50;

    [Fact]
    public void CompleteNewImad_IsCatalogedEmitted_AndKeepsDependentScptLoadable()
    {
        var records = RecordsWith(ImageSpaceModifierTestFactory.Complete(ModifierSourceFormId));

        var plan = BuildPlanner().Build(
            [], records, new HashSet<string> { "IMAD", "SCPT" }, new HashSet<uint>(), null);

        var modifier = Assert.Single(plan.Records, static record => record.Type == "IMAD");
        var script = Assert.Single(plan.Records, static record => record.Type == "SCPT");
        Assert.Equal(plan.SourceToEmittedFormId[ModifierSourceFormId], modifier.FormId);
        Assert.Equal(
            modifier.FormId,
            Assert.Single(script.References, static reference => reference.FieldPath == "SCRO[0]").FinalFormId);

        var writer = new PlanWriter(PlannedEncoders.BuildRegistry());
        var options = new PluginBuildOptions { CompressRecords = false };
        Assert.NotEmpty(writer.BuildGrupForType("IMAD", plan, options));
        Assert.NotEmpty(writer.BuildGrupForType("SCPT", plan, options));
    }

    [Fact]
    public void OneRuntimeImad_ClosesAllSixDependentScriptsOnce()
    {
        var modifier = ImageSpaceModifierTestFactory.Complete(ModifierSourceFormId) with
        {
            FromRuntime = true,
        };
        var scripts = Enumerable.Range(0, 6)
            .Select(index => new ScriptRecord
            {
                FormId = ScriptSourceFormId + (uint)index,
                EditorId = $"HVSimRuntimeClosure{index}",
                ReferencedObjects = [ModifierSourceFormId],
            })
            .ToList();
        var records = new RecordCollection
        {
            ImageSpaceModifiers = [modifier],
            Scripts = scripts,
        };

        var plan = BuildPlanner().Build(
            [], records, new HashSet<string> { "IMAD", "SCPT" }, new HashSet<uint>(), null);

        var emittedModifier = Assert.Single(plan.Records, static record => record.Type == "IMAD");
        var emittedScripts = plan.Records.Where(static record => record.Type == "SCPT").ToArray();
        Assert.Equal(6, emittedScripts.Length);
        Assert.Equal(6, emittedScripts.Select(static record => record.FormId).Distinct().Count());
        Assert.All(emittedScripts, script => Assert.Equal(
            emittedModifier.FormId,
            Assert.Single(script.References,
                static reference => reference.FieldPath == "SCRO[0]").FinalFormId));
    }

    [Fact]
    public void IncompleteNewImad_IsNotAllocated_AndDependentScptIsSuppressed()
    {
        var complete = ImageSpaceModifierTestFactory.Complete(ModifierSourceFormId);
        var incomplete = complete with
        {
            OrderedSubrecords = complete.OrderedSubrecords
                .Where(static sub => sub.Signature != "NAM3")
                .ToArray(),
        };

        var plan = BuildPlanner().Build(
            [], RecordsWith(incomplete), new HashSet<string> { "IMAD", "SCPT" }, new HashSet<uint>(), null);

        Assert.DoesNotContain(plan.Records, static record => record.Type is "IMAD" or "SCPT");
        Assert.DoesNotContain(ModifierSourceFormId, plan.SourceToEmittedFormId.Keys);
        Assert.DoesNotContain(ScriptSourceFormId, plan.SourceToEmittedFormId.Keys);
        Assert.Contains(plan.Diagnostics, diagnostic =>
            diagnostic.Code == "script.suppress-unsafe-reference-table"
            && diagnostic.Message.Contains($"0x{ModifierSourceFormId:X8}", StringComparison.Ordinal));
    }

    [Fact]
    public void DmpRecordSource_EnumeratesImadModels()
    {
        var model = ImageSpaceModifierTestFactory.Complete();
        var source = new DmpRecordSource(new RecordCollection { ImageSpaceModifiers = [model] });

        var entry = Assert.Single(source.Enumerate(new HashSet<string> { "IMAD" }));

        Assert.True(DmpRecordSource.SupportsType("IMAD"));
        Assert.Equal("IMAD", entry.Type);
        Assert.Equal(model.FormId, entry.FormId);
        Assert.Same(model, entry.Model);
    }

    private static RecordCollection RecordsWith(
        BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc.ImageSpaceModifierRecord modifier)
    {
        return new RecordCollection
        {
            ImageSpaceModifiers = [modifier],
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = ScriptSourceFormId,
                    EditorId = "HVSimEnterScript",
                    ReferencedObjects = [ModifierSourceFormId],
                },
            ],
        };
    }

    private static EsmPlanner BuildPlanner()
    {
        var disposition = new DispositionEngine(
        [
            new ImageSpaceModifierDispositionPolicy(),
            new ScriptDispositionPolicy(),
            new DefaultDispositionPolicy(),
        ]);
        var degradation = new DegradationPolicy();
        degradation.SetDefaultForType("IMAD", DanglingAction.DropSubrecord);
        degradation.SetDefaultForType("SCPT", DanglingAction.DropSubrecord);
        var references = new ReferenceResolver(
        [
            new ImageSpaceModifierReferenceWalker(),
            new ScriptReferenceWalker(),
        ], degradation);
        return new EsmPlanner(disposition, new FormIdAllocator(), references);
    }
}
