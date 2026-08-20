using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Planner.Disposition;
using BethesdaMultitool.Core.Formats.Esm.Planner.Disposition.Policies;
using BethesdaMultitool.Core.Formats.Esm.Planner.References;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner;

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
            [],
            new RecordCollection(),
            new HashSet<string>(),
            new HashSet<uint>(),
            null);

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
            [],
            new RecordCollection(),
            new HashSet<string> { "WEAP" },
            new HashSet<uint>(),
            "test.esm");

        Assert.Empty(plan.Records);
        Assert.Empty(plan.EmittedFormIds);
        Assert.Equal("test.esm", plan.Meta.MasterPath);
        Assert.Contains("WEAP", plan.Meta.PlannerCoverage);
    }

    [Fact]
    public void Build_With_Cell_Coverage_And_Incomplete_Cell_Inputs_Fails_Early()
    {
        var planner = BuildPlanner();

        var exception = Assert.Throws<InvalidOperationException>(() => planner.Build(
            [],
            new RecordCollection(),
            new HashSet<string> { "CELL" },
            new HashSet<uint>(),
            "test.esm"));

        Assert.Contains("masterCellContexts", exception.Message, StringComparison.Ordinal);
        Assert.Contains("masterRecordsByFormId", exception.Message, StringComparison.Ordinal);
        Assert.Contains("cellChildAllocator", exception.Message, StringComparison.Ordinal);
        Assert.Contains("masterRefFormIds", exception.Message, StringComparison.Ordinal);
        Assert.Contains("cellVerdictInputs", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_With_Complete_Cell_Inputs_Settles_New_Cell_For_Writing()
    {
        var allocator = new FormIdAllocator();
        var planner = BuildPlanner(allocator);
        var dmp = new RecordCollection
        {
            Cells = [new CellRecord { FormId = 0xFF000801, EditorId = "NewInterior" }]
        };
        var masterIndex = MasterRecordIndex.Build([], []);

        var plan = planner.Build(
            [],
            dmp,
            new HashSet<string> { "CELL" },
            new HashSet<uint>(),
            "test.esm",
            new Dictionary<uint, PcEsmCellContext>(),
            new Dictionary<uint, ParsedMainRecord>(),
            allocator,
            masterRefFormIds: new HashSet<uint>(),
            cellVerdictInputs: new CellVerdictInputs { MasterIndex = masterIndex });

        var cell = Assert.Single(plan.CellsByFormId.Values);
        Assert.Equal(CellMergeMode.LoadedReplacement, cell.Mode);
        Assert.True(cell.Emits);
    }

    [Fact]
    public void Build_Preserves_NextObjectId_When_No_Allocations_Happen()
    {
        var allocator = new FormIdAllocator();
        var planner = BuildPlanner(allocator);

        var plan = planner.Build(
            [],
            new RecordCollection(),
            new HashSet<string>(),
            new HashSet<uint>(),
            null);

        Assert.Equal(0x800u, plan.Meta.NextObjectId);
    }

    [Fact]
    public void Build_Maps_Aliased_Dmp_Source_To_Master_Without_Plugin_Allocation()
    {
        const uint sourceFormId = 0x001251C2;
        const uint masterFormId = 0x0013408C;
        var allocator = new FormIdAllocator();
        var planner = BuildPlanner(allocator);
        var master = new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "GMST",
                FormId = masterFormId,
                Version = 15
            }
        };
        var dmp = new RecordCollection
        {
            GameSettings =
            [
                new GameSettingRecord
                {
                    FormId = sourceFormId,
                    EditorId = "SharedEditorId",
                    IntValue = 1
                }
            ]
        };

        var plan = planner.Build(
            [master],
            dmp,
            new HashSet<string> { "GMST" },
            new HashSet<uint> { masterFormId },
            null,
            masterFormIdAliases: new Dictionary<uint, uint> { [sourceFormId] = masterFormId });

        var record = Assert.Single(plan.Records);
        Assert.Equal(RecordDisposition.Override, record.Disposition);
        Assert.Equal(masterFormId, record.FormId);
        Assert.Equal(sourceFormId, record.SourceFormId);
        Assert.Equal(masterFormId, plan.SourceToEmittedFormId[sourceFormId]);
        Assert.Equal(0x800u, allocator.NextObjectId);
    }

    [Fact]
    public void Build_Preserves_Alias_Mapping_When_Exact_Master_Capture_Wins()
    {
        const uint aliasFormId = 0x001251C2;
        const uint masterFormId = 0x0013408C;
        var allocator = new FormIdAllocator();
        var planner = BuildPlanner(allocator);
        var master = MasterGameSetting(masterFormId);
        var dmp = new RecordCollection
        {
            GameSettings =
            [
                GameSetting(aliasFormId, "SharedEditorId", 1),
                GameSetting(masterFormId, "SharedEditorId", 2)
            ]
        };

        var plan = planner.Build(
            [master],
            dmp,
            new HashSet<string> { "GMST" },
            new HashSet<uint> { masterFormId },
            null,
            masterFormIdAliases: new Dictionary<uint, uint> { [aliasFormId] = masterFormId });

        var record = Assert.Single(plan.Records);
        Assert.Equal(masterFormId, record.SourceFormId);
        Assert.Equal(masterFormId, plan.SourceToEmittedFormId[aliasFormId]);
        Assert.Equal(0x800u, allocator.NextObjectId);
    }

    [Fact]
    public void Build_Maps_Every_Validated_Alias_When_Several_Share_One_Master()
    {
        const uint firstAliasFormId = 0x001251C2;
        const uint secondAliasFormId = 0x001251C3;
        const uint masterFormId = 0x0013408C;
        var allocator = new FormIdAllocator();
        var planner = BuildPlanner(allocator);
        var dmp = new RecordCollection
        {
            GameSettings =
            [
                GameSetting(firstAliasFormId, "SharedEditorId", 1),
                GameSetting(secondAliasFormId, "SharedEditorId", 2)
            ]
        };

        var plan = planner.Build(
            [MasterGameSetting(masterFormId)],
            dmp,
            new HashSet<string> { "GMST" },
            new HashSet<uint> { masterFormId },
            null,
            masterFormIdAliases: new Dictionary<uint, uint>
            {
                [firstAliasFormId] = masterFormId,
                [secondAliasFormId] = masterFormId
            });

        Assert.Single(plan.Records);
        Assert.Equal(masterFormId, plan.SourceToEmittedFormId[firstAliasFormId]);
        Assert.Equal(masterFormId, plan.SourceToEmittedFormId[secondAliasFormId]);
        Assert.Equal(0x800u, allocator.NextObjectId);
    }

    [Fact]
    public void Build_Carries_Disabled_Scpt_Master_Alias_For_PlannerOwned_Npc_Scri()
    {
        const uint npcFormId = 0x00014000;
        const uint sourceScriptFormId = 0x001251C2;
        const uint masterScriptFormId = 0x0013408C;
        var planner = BuildPlanner();
        var masterNpc = new ParsedMainRecord
        {
            Header = new MainRecordHeader { Signature = "NPC_", FormId = npcFormId, Version = 15 }
        };
        var masterScript = new ParsedMainRecord
        {
            Header = new MainRecordHeader { Signature = "SCPT", FormId = masterScriptFormId, Version = 15 }
        };
        var dmp = new RecordCollection
        {
            Npcs = [new NpcRecord { FormId = npcFormId, Script = sourceScriptFormId }],
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = sourceScriptFormId,
                    EditorId = "SharedScript"
                }
            ]
        };

        var plan = planner.Build(
            [masterNpc, masterScript],
            dmp,
            new HashSet<string> { "NPC_" },
            new HashSet<uint> { npcFormId, masterScriptFormId },
            null,
            masterFormIdAliases: new Dictionary<uint, uint>
            {
                [sourceScriptFormId] = masterScriptFormId
            });

        Assert.Equal(masterScriptFormId, plan.SourceToEmittedFormId[sourceScriptFormId]);
    }

    [Fact]
    public void Build_AliasedTruncatedScriptKeepsMasterWithoutAllocatingDuplicate()
    {
        const uint sourceScriptFormId = 0x000B16CE;
        const uint masterScriptFormId = 0x001209D1;
        var allocator = new FormIdAllocator();
        var planner = BuildPlanner(allocator);
        var masterScript = new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "SCPT",
                FormId = masterScriptFormId,
                Version = 15
            },
            Subrecords =
            [
                new ParsedSubrecord { Signature = "SCDA", Data = new byte[5092] }
            ]
        };
        var dmp = new RecordCollection
        {
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = sourceScriptFormId,
                    EditorId = "VNPCFollowersQuestSCRIPT",
                    CompiledData = new byte[924]
                }
            ]
        };

        var plan = planner.Build(
            [masterScript],
            dmp,
            new HashSet<string> { "SCPT" },
            new HashSet<uint> { masterScriptFormId },
            null,
            masterFormIdAliases: new Dictionary<uint, uint>
            {
                [sourceScriptFormId] = masterScriptFormId
            });

        var record = Assert.Single(plan.Records);
        Assert.Equal(RecordDisposition.KeepMaster, record.Disposition);
        Assert.Equal(masterScriptFormId, record.FormId);
        Assert.Equal(sourceScriptFormId, record.SourceFormId);
        Assert.Equal(masterScriptFormId, plan.SourceToEmittedFormId[sourceScriptFormId]);
        Assert.Equal(0x800u, allocator.NextObjectId);
    }

    private static ParsedMainRecord MasterGameSetting(uint formId)
    {
        return new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "GMST",
                FormId = formId,
                Version = 15
            }
        };
    }

    private static GameSettingRecord GameSetting(uint formId, string editorId, int value)
    {
        return new GameSettingRecord
        {
            FormId = formId,
            EditorId = editorId,
            IntValue = value
        };
    }

    private static EsmPlanner BuildPlanner(FormIdAllocator? allocator = null)
    {
        var disposition = new DispositionEngine(
            [new ScriptDispositionPolicy(), new DefaultDispositionPolicy()]);
        var degradation = new DegradationPolicy();
        var references = new ReferenceResolver([], degradation);

        return new EsmPlanner(disposition, allocator ?? new FormIdAllocator(), references);
    }
}