using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Disposition;
using BethesdaMultitool.Core.Formats.Esm.Planner.Disposition.Policies;
using BethesdaMultitool.Core.Formats.Esm.Planner.References;
using BethesdaMultitool.Core.Formats.Esm.Planner.References.Walkers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner;

public sealed class InlineScriptReferenceSafetyPlannerTests
{
    private const uint SourceFormId = 0x00100100;

    [Theory]
    [InlineData("INFO")]
    [InlineData("PACK")]
    [InlineData("TERM")]
    public void Build_SuppressesUnsafeNewOwnerAndPrunesAllocation(string recordType)
    {
        var records = BuildRecords(recordType);

        var plan = BuildPlanner().Build(
            [], records, new HashSet<string> { recordType }, new HashSet<uint>(), null);

        Assert.DoesNotContain(plan.Records, record => record.Type == recordType);
        Assert.DoesNotContain(SourceFormId, plan.SourceToEmittedFormId.Keys);
        var diagnostic = Assert.Single(plan.Diagnostics, diagnostic =>
            diagnostic.Code == "inline-script.suppress-unsafe-owner"
            && diagnostic.RecordType == recordType);
        Assert.Equal("Skip", diagnostic.Metadata?["owner-disposition"]);
        Assert.Contains("does not resolve", diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("INFO")]
    [InlineData("PACK")]
    [InlineData("TERM")]
    public void Build_UnsafeOverrideRetainsMaster(string recordType)
    {
        var records = BuildRecords(recordType);
        var master = new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = recordType,
                FormId = SourceFormId
            }
        };

        var plan = BuildPlanner().Build(
            [master],
            records,
            new HashSet<string> { recordType },
            new HashSet<uint> { SourceFormId },
            null);

        var retained = Assert.Single(plan.Records, record => record.Type == recordType);
        Assert.Equal(RecordDisposition.KeepMaster, retained.Disposition);
        var diagnostic = Assert.Single(plan.Diagnostics, diagnostic =>
            diagnostic.Code == "inline-script.suppress-unsafe-owner"
            && diagnostic.RecordType == recordType);
        Assert.Equal("KeepMaster", diagnostic.Metadata?["owner-disposition"]);
        Assert.Contains("Retained master", diagnostic.Message, StringComparison.Ordinal);
    }

    private static RecordCollection BuildRecords(string recordType)
    {
        var script = new DialogueResultScript
        {
            CompiledData = [0x01],
            ReferencedObjects = [0x00ABCDEF]
        };
        var records = new RecordCollection();
        switch (recordType)
        {
            case "INFO":
                records.Dialogues.Add(new DialogueRecord
                {
                    FormId = SourceFormId,
                    ResultScripts = [script]
                });
                break;
            case "PACK":
                records.Packages.Add(new PackageRecord
                {
                    FormId = SourceFormId,
                    Data = new PackageData(),
                    OnBegin = new PackageEventAction { Scripts = [script] }
                });
                break;
            case "TERM":
                records.Terminals.Add(new TerminalRecord
                {
                    FormId = SourceFormId,
                    MenuItems =
                    [
                        new TerminalMenuItem
                        {
                            CompiledData = script.CompiledData,
                            ReferencedObjects = script.ReferencedObjects
                        }
                    ]
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(recordType));
        }

        return records;
    }

    private static EsmPlanner BuildPlanner()
    {
        var disposition = new DispositionEngine([new DefaultDispositionPolicy()]);
        var degradation = new DegradationPolicy();
        degradation.SetDefaultForType("INFO", DanglingAction.DropSubrecord);
        degradation.SetDefaultForType("PACK", DanglingAction.DropSubrecord);
        degradation.SetDefaultForType("TERM", DanglingAction.DropSubrecord);
        var references = new ReferenceResolver(
            [new InfoReferenceWalker(), new PackageReferenceWalker(), new TerminalReferenceWalker()],
            degradation);
        return new EsmPlanner(disposition, new FormIdAllocator(), references);
    }
}