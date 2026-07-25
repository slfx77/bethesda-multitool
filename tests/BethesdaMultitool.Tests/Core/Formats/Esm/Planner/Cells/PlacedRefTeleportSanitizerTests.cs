using System.Buffers.Binary;
using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Reporting;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Cells;

public sealed class PlacedRefTeleportSanitizerTests
{
    private const uint TargetRef = 0x0010F076;
    private const uint TargetBase = 0x0004E7F4;

    [Fact]
    public void Existing_Stat_Base_Refr_Is_Not_A_Valid_Xtel_Target()
    {
        var master = new Dictionary<uint, ParsedMainRecord>
        {
            [TargetRef] = Record("REFR", TargetRef, TargetBase),
            [TargetBase] = Record("STAT", TargetBase)
        };
        var stats = new ConversionPipelineStats();

        var sanitized = PlacedRefTeleportSanitizer.Sanitize(
            [Xtel(TargetRef)], Context(master, stats));

        Assert.Empty(sanitized);
        Assert.Equal(1, stats.DropReasonCounts["refr.xtel-target-not-door"]);
    }

    [Fact]
    public void Existing_Door_Base_Refr_Remains_A_Valid_Xtel_Target()
    {
        var master = new Dictionary<uint, ParsedMainRecord>
        {
            [TargetRef] = Record("REFR", TargetRef, TargetBase),
            [TargetBase] = Record("DOOR", TargetBase)
        };

        var sanitized = PlacedRefTeleportSanitizer.Sanitize(
            [Xtel(TargetRef)], Context(master, new ConversionPipelineStats()));

        Assert.Single(sanitized);
    }

    private static CellChildEncodeContext Context(
        IReadOnlyDictionary<uint, ParsedMainRecord> master,
        ConversionPipelineStats stats)
    {
        var plan = new EmitPlan
        {
            Records = ImmutableArray<RecordPlan>.Empty,
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty,
            EmittedFormIds = master.Keys.ToImmutableHashSet(),
            RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty,
            Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
            Meta = new PlanMetadata
            {
                NextObjectId = 0x800,
                PlannerCoverage = ImmutableHashSet<string>.Empty
            }
        };
        var masterRefs = new HashSet<uint> { TargetRef };
        return new CellChildEncodeContext(
            plan,
            master,
            [.. master.Keys],
            new PluginBuildOptions(),
            stats,
            null,
            masterRefs,
            null,
            PlannerXespParentClassifier.BuildIndex(plan, master, masterRefs));
    }

    private static EncodedSubrecord Xtel(uint target)
    {
        var bytes = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, target);
        return new EncodedSubrecord("XTEL", bytes);
    }

    private static ParsedMainRecord Record(string signature, uint formId, uint? name = null)
    {
        var subrecords = new List<ParsedSubrecord>();
        if (name is { } baseFormId)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, baseFormId);
            subrecords.Add(new ParsedSubrecord { Signature = "NAME", Data = bytes });
        }

        return new ParsedMainRecord
        {
            Header = new MainRecordHeader { Signature = signature, FormId = formId },
            Subrecords = subrecords
        };
    }
}