using System.Buffers.Binary;
using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Encoders.ComplexRef;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Reporting;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner;

public sealed class PlannedNpcPackageResolutionTests
{
    [Fact]
    public void NewNpc_KeepsLivePackage_RemapsCapturedPackage_AndDropsUnresolvedPackage()
    {
        const uint liveMaster = 0x000ED239;
        const uint captured = 0x00ABCDEF;
        const uint emitted = 0x01000123;
        const uint dangling = 0x00BADBAD;
        var npc = MakeNpc([liveMaster, captured, dangling]);
        var record = MakeRecord(npc,
        [
            Resolved("PKID[0]", liveMaster, liveMaster),
            Resolved("PKID[1]", captured, emitted),
            Dropped("PKID[2]", dangling),
        ]);
        var plan = MakePlan(record, [liveMaster, emitted],
            ImmutableDictionary<uint, uint>.Empty.Add(captured, emitted));

        var encoded = new PlannedNpcEncoder().Encode(npc, record, new PlanReferenceLookup(record, plan));

        var pkids = encoded.Subrecords
            .Where(subrecord => subrecord.Signature == "PKID")
            .Select(subrecord => BinaryPrimitives.ReadUInt32LittleEndian(subrecord.Bytes))
            .ToArray();
        Assert.Equal([liveMaster, emitted], pkids);
        Assert.Contains(encoded.Warnings, warning => warning.Contains("dropped", StringComparison.Ordinal));
        Assert.Contains(encoded.Warnings, warning => warning.Contains("remapped", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidEmittedFormId_OfWrongType_IsNotAcceptedAsPackage()
    {
        const uint notAPackage = 0x01000444;
        var npc = MakeNpc([notAPackage]);
        var record = MakeRecord(npc, [Resolved("PKID[0]", notAPackage, notAPackage)]);
        var plan = MakePlan(record, [], ImmutableDictionary<uint, uint>.Empty) with
        {
            EmittedFormIds = ImmutableHashSet.Create(notAPackage, npc.FormId),
        };

        var encoded = new PlannedNpcEncoder().Encode(npc, record, new PlanReferenceLookup(record, plan));

        Assert.DoesNotContain(encoded.Subrecords, subrecord => subrecord.Signature == "PKID");
    }

    [Fact]
    public void DroppedAndWrongTypePackagesProduceSurfacedPlannerDiagnostics()
    {
        const uint wrongType = 0x01000444;
        const uint dangling = 0x00BADBAD;
        var npc = MakeNpc([wrongType, dangling]);
        var record = MakeRecord(npc,
        [
            Resolved("PKID[0]", wrongType, wrongType),
            Dropped("PKID[1]", dangling),
        ]);

        var diagnostics = EsmPlanner.BuildPackageReferenceDiagnostics([record], new HashSet<uint>());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "references.drop.pkid-wrong-type");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "references.drop.pkid-dangle");

        var plan = MakePlan(record, [], ImmutableDictionary<uint, uint>.Empty) with
        {
            Diagnostics = diagnostics,
        };
        var sink = new RecordingSink();
        PluginBuilder.ReportPlannerDiagnostics(plan, sink);

        Assert.Equal(2, sink.Events.Count);
        Assert.All(sink.Events, evt => Assert.Equal(ConversionEventSeverity.Decision, evt.Severity));
        Assert.Contains(sink.Events, evt => evt.Code == "references.drop.pkid-wrong-type");
        Assert.Contains(sink.Events, evt => evt.Code == "references.drop.pkid-dangle");
    }

    private static NpcRecord MakeNpc(uint[] packages) => new()
    {
        FormId = 0x010008E0,
        EditorId = "PackageTestNpc",
        FullName = "Package Test NPC",
        Stats = new ActorBaseSubrecord(0, 0, 0, 1, 1, 1, 100, 0f, 0, 0, 0, false),
        Packages = packages.ToList(),
    };

    private static RecordPlan MakeRecord(NpcRecord npc, ImmutableArray<ResolvedRef> references) => new()
    {
        Type = "NPC_",
        Disposition = RecordDisposition.New,
        FormId = npc.FormId,
        SourceFormId = npc.FormId,
        Model = npc,
        References = references,
        ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
        Provenance = new PlanProvenance { PolicyId = "test", Reason = "package sanitation" },
    };

    private static EmitPlan MakePlan(
        RecordPlan record,
        IEnumerable<uint> packages,
        ImmutableDictionary<uint, uint> remap) => new()
    {
        Records = [record],
        SourceToEmittedFormId = remap,
        EmittedFormIds = packages.Append(record.FormId).ToImmutableHashSet(),
        ValidPackageFormIds = packages.ToImmutableHashSet(),
        RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty.Add(record.FormId, 0),
        Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
        Meta = new PlanMetadata
        {
            NextObjectId = 0x124,
            PlannerCoverage = ImmutableHashSet.Create("NPC_"),
        },
    };

    private static ResolvedRef Resolved(string path, uint source, uint target) => new()
    {
        FieldPath = path,
        OriginalFormId = source,
        Action = ResolvedRefAction.Resolved,
        FinalFormId = target,
    };

    private static ResolvedRef Dropped(string path, uint source) => new()
    {
        FieldPath = path,
        OriginalFormId = source,
        Action = ResolvedRefAction.DropSubrecord,
        Reason = "not a live PACK",
    };

    private sealed class RecordingSink : IConversionProgressSink
    {
        public List<ConversionProgressEvent> Events { get; } = [];

        public void OnPhaseStart(string phase, int? totalItems) { }
        public void OnEvent(ConversionProgressEvent evt) => Events.Add(evt);
        public void OnPhaseEnd(string phase, ConversionPipelineStats partialStats) { }
        public void OnComplete(ConversionPipelineStats stats) { }
    }
}
