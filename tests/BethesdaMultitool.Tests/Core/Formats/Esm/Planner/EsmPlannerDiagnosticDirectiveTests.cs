using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Catalog;
using BethesdaMultitool.Core.Formats.Esm.Planner.Disposition;
using BethesdaMultitool.Core.Formats.Esm.Planner.Disposition.Policies;
using BethesdaMultitool.Core.Formats.Esm.Planner.References;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner;

public sealed class EsmPlannerDiagnosticDirectiveTests
{
    private const uint FormId = 0x0012795D;

    [Fact]
    public void KeepMasterPolicy_IsFirstPriorityAndMatchesOnlyTheExactFormId()
    {
        var engine = new DispositionEngine(
        [
            new DiagnosticKeepMasterDispositionPolicy([FormId]),
            new ForceGlobSkipPolicy(),
            new DefaultDispositionPolicy()
        ]);
        var requested = new CatalogEntry
        {
            Type = "GLOB",
            Source = SourceKind.DmpOverride,
            MasterFormId = FormId,
            DmpFormId = FormId,
            Model = new GlobalRecord { FormId = FormId },
            Master = BuildMasterGlobal()
        };
        var other = requested with
        {
            MasterFormId = FormId + 1,
            DmpFormId = FormId + 1
        };

        var decisions = engine.Decide([requested, other]);

        Assert.Equal(RecordDisposition.KeepMaster, decisions[0].Decision.Disposition);
        Assert.Equal("DiagnosticKeepMasterDispositionPolicy", decisions[0].Decision.Provenance.PolicyId);
        Assert.Equal(RecordDisposition.Skip, decisions[1].Decision.Disposition);
        Assert.Equal("test.force-skip", decisions[1].Decision.Provenance.PolicyId);
    }

    [Fact]
    public void KeepMasterDirective_WinsBeforeDefaultOverrideAndEmitsNothing()
    {
        var planner = BuildPlanner([FormId]);
        var plan = planner.Build(
            [BuildMasterGlobal()],
            BuildDmpGlobal(),
            new HashSet<string>(StringComparer.Ordinal) { "GLOB" },
            new HashSet<uint> { FormId },
            "FalloutNV.esm",
            diagnosticKeepMasterFormIds: ImmutableHashSet.Create(FormId));

        var record = Assert.Single(plan.Records);
        Assert.Equal(RecordDisposition.KeepMaster, record.Disposition);
        Assert.Equal("DiagnosticKeepMasterDispositionPolicy", record.Provenance.PolicyId);

        var output = new PlanWriter(PlannedEncoders.BuildRegistry())
            .BuildGrupForType("GLOB", plan, new PluginBuildOptions());
        Assert.Empty(output);
    }

    [Fact]
    public void RetainSubrecordDirective_IsImmutableAndPlanWriterKeepsMasterBytes()
    {
        var retentions = ImmutableDictionary<uint, ImmutableHashSet<string>>.Empty.Add(
            FormId, ImmutableHashSet.Create(StringComparer.Ordinal, "FLTV"));
        var plan = BuildPlanner([]).Build(
            [BuildMasterGlobal()],
            BuildDmpGlobal(),
            new HashSet<string>(StringComparer.Ordinal) { "GLOB" },
            new HashSet<uint> { FormId },
            "FalloutNV.esm",
            diagnosticRetainMasterSubrecords: retentions);

        var record = Assert.Single(plan.Records);
        Assert.Equal(RecordDisposition.Override, record.Disposition);
        Assert.Equal(["FLTV"], record.RetainMasterSubrecordSignatures);

        var output = new PlanWriter(PlannedEncoders.BuildRegistry())
            .BuildGrupForType("GLOB", plan, new PluginBuildOptions { CompressRecords = false });

        Assert.Equal(BitConverter.GetBytes(1.5f), ReadRecordSubrecord(output, "FLTV"));
        Assert.Equal(1, CountRecordSubrecords(output, "FLTV"));
    }

    [Fact]
    public void RetainSubrecordDirective_RejectsSignatureAbsentFromMaster()
    {
        var retentions = ImmutableDictionary<uint, ImmutableHashSet<string>>.Empty.Add(
            FormId, ImmutableHashSet.Create(StringComparer.Ordinal, "DNAM"));

        var exception = Assert.Throws<InvalidOperationException>(() => BuildPlanner([]).Build(
            [BuildMasterGlobal()],
            BuildDmpGlobal(),
            new HashSet<string>(StringComparer.Ordinal) { "GLOB" },
            new HashSet<uint> { FormId },
            "FalloutNV.esm",
            diagnosticRetainMasterSubrecords: retentions));

        Assert.Contains("has no DNAM subrecord", exception.Message);
    }

    [Fact]
    public void RetainSubrecordDirective_RejectsSignatureAbsentFromOverrideEncoder()
    {
        var retentions = ImmutableDictionary<uint, ImmutableHashSet<string>>.Empty.Add(
            FormId, ImmutableHashSet.Create(StringComparer.Ordinal, "FNAM"));
        var plan = BuildPlanner([]).Build(
            [BuildMasterGlobal()],
            BuildDmpGlobal(),
            new HashSet<string>(StringComparer.Ordinal) { "GLOB" },
            new HashSet<uint> { FormId },
            "FalloutNV.esm",
            diagnosticRetainMasterSubrecords: retentions);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new PlanWriter(PlannedEncoders.BuildRegistry())
                .BuildGrupForType("GLOB", plan, new PluginBuildOptions()));

        Assert.Contains("encoder produced no such subrecord", exception.Message);
    }

    [Fact]
    public void GomorrahShapedNpcRetention_PreservesMasterRowsAndAcbsIdentityEndToEnd()
    {
        var masterAcbs = BuildAcbs(
            0x00000018,
            20,
            30,
            4,
            0x015F);
        var masterAidt = Enumerable.Range(0x40, 20).Select(static value => (byte)value).ToArray();
        var masterDnam = Enumerable.Range(0x10, 28).Select(static value => (byte)value).ToArray();
        var masterSnamA = BuildSnam(0x000F1001, 1);
        var masterSnamB = BuildSnam(0x000F1002, -2);
        var master = new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "NPC_",
                FormId = FormId,
                Version = 0x000F
            },
            Subrecords =
            [
                new ParsedSubrecord { Signature = "EDID", Data = Encoding.ASCII.GetBytes("vGOMEntranceGuard\0") },
                new ParsedSubrecord { Signature = "ACBS", Data = masterAcbs },
                new ParsedSubrecord { Signature = "SNAM", Data = masterSnamA },
                new ParsedSubrecord { Signature = "SNAM", Data = masterSnamB },
                new ParsedSubrecord { Signature = "AIDT", Data = masterAidt },
                new ParsedSubrecord { Signature = "DNAM", Data = masterDnam }
            ]
        };
        var captured = new NpcRecord
        {
            FormId = FormId,
            EditorId = "vGOMEntranceGuard",
            Stats = new ActorBaseSubrecord(
                0x00000058, 77, 88, 9, 2, 12, 115, 1.25f, -7, 0x835F, 0, false),
            Factions =
            [
                new FactionMembership(0x000F2001, 3),
                new FactionMembership(0x000F2002, 4)
            ],
            AiData = new NpcAiData(3, 4, 80, 100, 6, 0x11223344, 2),
            Skills = Enumerable.Range(0x70, 14).Select(static value => (byte)value).ToArray()
        };
        var retentions = ImmutableDictionary<uint, ImmutableHashSet<string>>.Empty.Add(
            FormId,
            ImmutableHashSet.Create(StringComparer.Ordinal, "AIDT", "SNAM", "DNAM"));
        var dmp = new RecordCollection { Npcs = [captured] };

        var plan = BuildPlanner([]).Build(
            [master], dmp,
            new HashSet<string>(StringComparer.Ordinal) { "NPC_" },
            new HashSet<uint> { FormId },
            "FalloutNV.esm",
            diagnosticRetainMasterSubrecords: retentions);
        var output = new PlanWriter(PlannedEncoders.BuildRegistry())
            .BuildGrupForType("NPC_", plan, new PluginBuildOptions { CompressRecords = false });

        var acbs = ReadRecordSubrecord(output, "ACBS");
        Assert.Equal(0x00000018u, BinaryPrimitives.ReadUInt32LittleEndian(acbs));
        Assert.Equal((ushort)0x015F, BinaryPrimitives.ReadUInt16LittleEndian(acbs.AsSpan(22, 2)));
        Assert.Equal((ushort)77, BinaryPrimitives.ReadUInt16LittleEndian(acbs.AsSpan(4, 2)));
        Assert.Equal((short)9, BinaryPrimitives.ReadInt16LittleEndian(acbs.AsSpan(8, 2)));

        Assert.Equal(masterAidt, ReadRecordSubrecord(output, "AIDT"));
        Assert.Equal(masterDnam, ReadRecordSubrecord(output, "DNAM"));
        Assert.Equal(2, CountRecordSubrecords(output, "SNAM"));
        Assert.Equal(masterSnamA, ReadNthRecordSubrecord(output, "SNAM", 0));
        Assert.Equal(masterSnamB, ReadNthRecordSubrecord(output, "SNAM", 1));
        Assert.True(FindRecordSubrecord(output, "SNAM") < FindRecordSubrecord(output, "AIDT"));
        Assert.True(FindRecordSubrecord(output, "AIDT") < FindRecordSubrecord(output, "DNAM"));
    }

    [Fact]
    public void DiagnosticDirective_RejectsMasterPureAndPartialRetentionOverlap()
    {
        var retentions = ImmutableDictionary<uint, ImmutableHashSet<string>>.Empty.Add(
            FormId, ImmutableHashSet.Create(StringComparer.Ordinal, "FLTV"));

        var exception = Assert.Throws<InvalidOperationException>(() => BuildPlanner([FormId]).Build(
            [BuildMasterGlobal()],
            BuildDmpGlobal(),
            new HashSet<string>(StringComparer.Ordinal) { "GLOB" },
            new HashSet<uint> { FormId },
            "FalloutNV.esm",
            diagnosticKeepMasterFormIds: ImmutableHashSet.Create(FormId),
            diagnosticRetainMasterSubrecords: retentions));

        Assert.Contains("cannot be both master-pure and partially retained", exception.Message);
    }

    [Fact]
    public void KeepMasterDirective_RejectsMasterOnlyRecordWithoutDmpOverride()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => BuildPlanner([FormId]).Build(
            [BuildMasterGlobal()],
            new RecordCollection(),
            new HashSet<string>(StringComparer.Ordinal) { "GLOB" },
            new HashSet<uint> { FormId },
            "FalloutNV.esm",
            diagnosticKeepMasterFormIds: ImmutableHashSet.Create(FormId)));

        Assert.Contains("not an exact DMP override", exception.Message);
    }

    private static EsmPlanner BuildPlanner(IEnumerable<uint> keepMasterFormIds)
    {
        var disposition = new DispositionEngine(
        [
            new DiagnosticKeepMasterDispositionPolicy(keepMasterFormIds),
            new RuntimeStatePolicy(),
            new DefaultDispositionPolicy()
        ]);
        return new EsmPlanner(
            disposition,
            new FormIdAllocator(),
            new ReferenceResolver([], new DegradationPolicy()));
    }

    private static RecordCollection BuildDmpGlobal()
    {
        return new RecordCollection
        {
            Globals =
            [
                new GlobalRecord
                {
                    FormId = FormId,
                    EditorId = "GomorrahDiagnosticGlobal",
                    ValueType = 'f',
                    Value = 9.5f
                }
            ]
        };
    }

    private static ParsedMainRecord BuildMasterGlobal()
    {
        return new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "GLOB",
                FormId = FormId,
                DataSize = 0,
                Flags = 0,
                Timestamp = 0,
                VcsInfo = 0,
                Version = 0x000F
            },
            Subrecords =
            [
                new ParsedSubrecord
                    { Signature = "EDID", Data = Encoding.ASCII.GetBytes("GomorrahDiagnosticGlobal\0") },
                new ParsedSubrecord { Signature = "FNAM", Data = [(byte)'f'] },
                new ParsedSubrecord { Signature = "FLTV", Data = BitConverter.GetBytes(1.5f) }
            ]
        };
    }

    private static byte[] ReadRecordSubrecord(byte[] topLevelGrup, string signature)
    {
        var offset = FindRecordSubrecord(topLevelGrup, signature);
        Assert.True(offset >= 0, $"{signature} not found in emitted record.");
        var length = BinaryPrimitives.ReadUInt16LittleEndian(topLevelGrup.AsSpan(offset + 4, 2));
        return topLevelGrup.AsSpan(offset + 6, length).ToArray();
    }

    private static byte[] ReadNthRecordSubrecord(byte[] topLevelGrup, string signature, int occurrence)
    {
        var seen = 0;
        for (var offset = 48; offset + 6 <= topLevelGrup.Length;)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(topLevelGrup.AsSpan(offset + 4, 2));
            if (Encoding.ASCII.GetString(topLevelGrup, offset, 4) == signature
                && seen++ == occurrence)
            {
                return topLevelGrup.AsSpan(offset + 6, length).ToArray();
            }

            offset += 6 + length;
        }

        return [];
    }

    private static byte[] BuildAcbs(
        uint flags,
        ushort fatigue,
        ushort barterGold,
        short level,
        ushort templateFlags)
    {
        var bytes = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, flags);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), fatigue);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6, 2), barterGold);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(8, 2), level);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12, 2), 10);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14, 2), 100);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16, 4), 0.5f);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20, 2), -3);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), templateFlags);
        return bytes;
    }

    private static byte[] BuildSnam(uint factionFormId, sbyte rank)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, factionFormId);
        bytes[4] = unchecked((byte)rank);
        return bytes;
    }

    private static int CountRecordSubrecords(byte[] topLevelGrup, string signature)
    {
        var count = 0;
        for (var offset = 48; offset + 6 <= topLevelGrup.Length;)
        {
            if (Encoding.ASCII.GetString(topLevelGrup, offset, 4) == signature)
            {
                count++;
            }

            var length = BinaryPrimitives.ReadUInt16LittleEndian(topLevelGrup.AsSpan(offset + 4, 2));
            offset += 6 + length;
        }

        return count;
    }

    private static int FindRecordSubrecord(byte[] topLevelGrup, string signature)
    {
        for (var offset = 48; offset + 6 <= topLevelGrup.Length;)
        {
            if (Encoding.ASCII.GetString(topLevelGrup, offset, 4) == signature)
            {
                return offset;
            }

            var length = BinaryPrimitives.ReadUInt16LittleEndian(topLevelGrup.AsSpan(offset + 4, 2));
            offset += 6 + length;
        }

        return -1;
    }

    private sealed class ForceGlobSkipPolicy : IDispositionPolicy
    {
        public IReadOnlySet<string> RecordTypes { get; } =
            new HashSet<string>(StringComparer.Ordinal) { "GLOB" };

        public DispositionDecision? Decide(CatalogEntry entry)
        {
            return new DispositionDecision
            {
                Disposition = RecordDisposition.Skip,
                Provenance = new PlanProvenance { PolicyId = "test.force-skip", Reason = "test" }
            };
        }
    }
}