using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Output;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Reporting;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner;

public sealed class ScriptVariableAugmentationTests
{
    private const uint ScriptFormId = 0x0010E1D3;

    [Fact]
    public void MasterOnlyScript_AppendsFreshLocalsWithoutChangingBytecodeReferencesOrExistingIndex()
    {
        var schr = Enumerable.Range(0, 20).Select(static value => (byte)(0x80 + value)).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(schr.AsSpan(12, 4), 1);
        var existingSlsd = Enumerable.Range(0, 24).Select(static value => (byte)(0x20 + value)).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(existingSlsd, 4);
        existingSlsd[16] = 1;
        var scda = new byte[] { 0x1D, 0x00, 0x7A, 0x51, 0xC3 };
        var scro = BitConverter.GetBytes(0x00000014u);
        const string source = "scn RetailScript\r\nshort RetailFlag\r\n\r\nBegin GameMode\r\nEnd";
        var master = MakeMaster(
            Sub("EDID", StringBytes("RetailScript")),
            Sub("SCHR", schr),
            Sub("SCDA", scda),
            Sub("SCTX", StringBytes(source)),
            Sub("SLSD", existingSlsd),
            Sub("SCVR", StringBytes("RetailFlag")),
            Sub("SCRO", scro));
        var originalPayloads = master.Subrecords.ToDictionary(
            static subrecord => subrecord.Signature,
            static subrecord => subrecord.Data.ToArray(),
            StringComparer.Ordinal);

        var plan = ScriptVariableAugmentationPlanner.Apply(
            MakePlan(master),
            [
                new ScriptVariableAugmentation(
                    ScriptFormId, new ScriptVariableInfo(9, "ProtoInteger", 1),
                    ScriptVariableDeclarationKind.Short),
                new ScriptVariableAugmentation(
                    ScriptFormId, new ScriptVariableInfo(10, "ProtoFloat", 0),
                    ScriptVariableDeclarationKind.Float),
                new ScriptVariableAugmentation(
                    ScriptFormId, new ScriptVariableInfo(11, "ProtoReference", 0),
                    ScriptVariableDeclarationKind.Reference),
            ]);

        var plannedScript = Assert.Single(plan.Records);
        Assert.Equal(RecordDisposition.Override, plannedScript.Disposition);
        Assert.Null(plannedScript.Model); // A master-only SCPT does not need a synthetic DMP model.
        Assert.Equal([9u, 10u, 11u], plannedScript.ScriptVariableAugmentations
            .Select(static augmentation => augmentation.Variable.Index));

        var sink = new RecordingSink();
        var grup = new PlanWriter(PlannedEncoders.BuildRegistry(), sink).BuildGrupForType(
            "SCPT", plan, new PluginBuildOptions { CompressRecords = false });
        var tes4 = PluginRecordByteBuilder.BuildNewRecordBytes("TES4", 0, 0, []);
        var emitted = Assert.Single(
            EsmParser.EnumerateRecords([.. tes4, .. grup]),
            static record => record.Header.Signature == "SCPT");

        Assert.Equal(
            ["EDID", "SCHR", "SCDA", "SCTX", "SLSD", "SCVR", "SLSD", "SCVR", "SLSD", "SCVR", "SLSD", "SCVR", "SCRO"],
            emitted.Subrecords.Select(static subrecord => subrecord.Signature));
        Assert.Equal(originalPayloads["EDID"], emitted.Subrecords.Single(s => s.Signature == "EDID").Data);
        Assert.Equal(scda, emitted.Subrecords.Single(s => s.Signature == "SCDA").Data);
        Assert.Equal(scro, emitted.Subrecords.Single(s => s.Signature == "SCRO").Data);

        var emittedSchr = emitted.Subrecords.Single(s => s.Signature == "SCHR").Data;
        var expectedSchr = schr.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(expectedSchr.AsSpan(12, 4), 4);
        Assert.Equal(expectedSchr, emittedSchr);

        var slsds = emitted.Subrecords.Where(static subrecord => subrecord.Signature == "SLSD").ToArray();
        Assert.Equal(existingSlsd, slsds[0].Data);
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(slsds[0].Data));
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(slsds[1].Data));
        Assert.Equal(1, slsds[1].Data[16]);
        Assert.Equal(10u, BinaryPrimitives.ReadUInt32LittleEndian(slsds[2].Data));
        Assert.Equal(0, slsds[2].Data[16]);
        Assert.Equal(11u, BinaryPrimitives.ReadUInt32LittleEndian(slsds[3].Data));
        Assert.Equal(0, slsds[3].Data[16]);
        Assert.All(slsds[1].Data.Where(static (_, index) => index is not (0 or 1 or 2 or 3 or 16)),
            static value => Assert.Equal(0, value));

        var emittedSource = emitted.Subrecords.Single(s => s.Signature == "SCTX").DataAsString;
        Assert.Equal(
            "scn RetailScript\r\nshort RetailFlag\r\n\r\n"
            + "short ProtoInteger\r\nfloat ProtoFloat\r\nref ProtoReference\r\nBegin GameMode\r\nEnd",
            emittedSource);

        var provenance = Assert.Single(sink.Events,
            static evt => evt.Code == ScriptEmissionProvenanceReporter.EventCode);
        Assert.Equal(ScriptFormId, provenance.FormId);
        Assert.Null(provenance.Metadata!["source-form-id"]);
        Assert.Equal("augmentation", provenance.Metadata["source-origin"]);
        Assert.Equal("false", provenance.Metadata["bytecode-changed-from-source"]);
        Assert.Equal("true", provenance.Metadata["tables-changed-from-source"]);
    }

    [Fact]
    public void ExistingOrLowerIndex_IsRejectedBeforeWriting()
    {
        var schr = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(schr.AsSpan(12, 4), 1);
        var existingSlsd = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(existingSlsd, 7);
        var master = MakeMaster(
            Sub("EDID", StringBytes("RetailScript")),
            Sub("SCHR", schr),
            Sub("SCTX", StringBytes("scn RetailScript\nBegin GameMode\nEnd")),
            Sub("SLSD", existingSlsd),
            Sub("SCVR", StringBytes("RetailFlag")));

        var error = Assert.Throws<InvalidOperationException>(() =>
            ScriptVariableAugmentationPlanner.Apply(
                MakePlan(master),
                [new ScriptVariableAugmentation(
                    ScriptFormId, new ScriptVariableInfo(6, "ProtoFlag", 1),
                    ScriptVariableDeclarationKind.Short)]));

        Assert.Contains("non-fresh index 6", error.Message, StringComparison.Ordinal);
        Assert.Contains("highest master index is 7", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingMasterSource_AllowsConcreteLocalWithoutBorrowingOrInventingText()
    {
        var master = MakeMaster(
            Sub("EDID", StringBytes("RetailScript")),
            Sub("SCHR", new byte[20]),
            Sub("SCDA", [0x1D, 0x00, 0x00, 0x00]));

        var plan = ScriptVariableAugmentationPlanner.Apply(
            MakePlan(master),
            [new ScriptVariableAugmentation(
                ScriptFormId, new ScriptVariableInfo(1, "ProtoFlag", 1),
                ScriptVariableDeclarationKind.Short)]);
        var grup = new PlanWriter(PlannedEncoders.BuildRegistry(), new RecordingSink()).BuildGrupForType(
            "SCPT", plan, new PluginBuildOptions { CompressRecords = false });
        var tes4 = PluginRecordByteBuilder.BuildNewRecordBytes("TES4", 0, 0, []);
        var emitted = Assert.Single(
            EsmParser.EnumerateRecords([.. tes4, .. grup]),
            static record => record.Header.Signature == "SCPT");

        Assert.DoesNotContain(emitted.Subrecords, static subrecord => subrecord.Signature == "SCTX");
        Assert.Equal(
            "ProtoFlag",
            emitted.Subrecords.Single(static subrecord => subrecord.Signature == "SCVR").DataAsString);
    }

    [Fact]
    public void MissingMasterSource_AllowsStorageOnlyLocalWithoutBorrowingOrInventingText()
    {
        var master = MakeMaster(
            Sub("EDID", StringBytes("RetailScript")),
            Sub("SCHR", new byte[20]),
            Sub("SCDA", [0x1D, 0x00, 0x00, 0x00]));

        var plan = ScriptVariableAugmentationPlanner.Apply(
            MakePlan(master),
            [new ScriptVariableAugmentation(
                ScriptFormId,
                new ScriptVariableInfo(1, "ProtoFlag", 1),
                ScriptVariableDeclarationKind.Integer)]);
        var grup = new PlanWriter(PlannedEncoders.BuildRegistry(), new RecordingSink()).BuildGrupForType(
            "SCPT", plan, new PluginBuildOptions { CompressRecords = false });
        var tes4 = PluginRecordByteBuilder.BuildNewRecordBytes("TES4", 0, 0, []);
        var emitted = Assert.Single(
            EsmParser.EnumerateRecords([.. tes4, .. grup]),
            static record => record.Header.Signature == "SCPT");

        Assert.DoesNotContain(emitted.Subrecords, static subrecord => subrecord.Signature == "SCTX");
        Assert.Single(emitted.Subrecords, static subrecord => subrecord.Signature == "SLSD");
        Assert.Equal(
            "ProtoFlag",
            emitted.Subrecords.Single(static subrecord => subrecord.Signature == "SCVR").DataAsString);
    }

    [Fact]
    public void MasterSourceAugmentation_PreservesWindows1252Bytes()
    {
        var schr = new byte[20];
        var sourceBytes = new byte[]
        {
            (byte)'s', (byte)'c', (byte)'n', (byte)' ', (byte)'R', (byte)'e', (byte)'t', (byte)'a',
            (byte)'i', (byte)'l', (byte)'S', (byte)'c', (byte)'r', (byte)'i', (byte)'p', (byte)'t',
            (byte)'\n', (byte)';', (byte)' ', 0x93, (byte)'q', (byte)'u', (byte)'o', (byte)'t',
            (byte)'e', 0x94, (byte)'\n', (byte)'B', (byte)'e', (byte)'g', (byte)'i', (byte)'n',
            (byte)' ', (byte)'G', (byte)'a', (byte)'m', (byte)'e', (byte)'M', (byte)'o', (byte)'d',
            (byte)'e', (byte)'\n', (byte)'E', (byte)'n', (byte)'d', 0,
        };
        var master = MakeMaster(
            Sub("EDID", StringBytes("RetailScript")),
            Sub("SCHR", schr),
            Sub("SCTX", sourceBytes));

        var plan = ScriptVariableAugmentationPlanner.Apply(
            MakePlan(master),
            [new ScriptVariableAugmentation(
                ScriptFormId, new ScriptVariableInfo(1, "ProtoFlag", 1),
                ScriptVariableDeclarationKind.Short)]);

        var grup = new PlanWriter(PlannedEncoders.BuildRegistry(), new RecordingSink()).BuildGrupForType(
            "SCPT", plan, new PluginBuildOptions { CompressRecords = false });
        var tes4 = PluginRecordByteBuilder.BuildNewRecordBytes("TES4", 0, 0, []);
        var emitted = Assert.Single(
            EsmParser.EnumerateRecords([.. tes4, .. grup]),
            static record => record.Header.Signature == "SCPT");
        var emittedSource = emitted.Subrecords.Single(static subrecord => subrecord.Signature == "SCTX").Data;

        Assert.Contains((byte)0x93, emittedSource);
        Assert.Contains((byte)0x94, emittedSource);
        Assert.DoesNotContain((byte)'?', emittedSource);
        Assert.Equal(0, emittedSource[^1]);
    }

    [Theory]
    [InlineData("Short", "short")]
    [InlineData("Long", "long")]
    [InlineData("Int", "int")]
    public void MasterSourceAugmentation_PreservesExactIntegerDeclarationKeyword(
        string declarationKindName,
        string expectedKeyword)
    {
        var declarationKind = Enum.Parse<ScriptVariableDeclarationKind>(declarationKindName);
        var master = MakeMaster(
            Sub("EDID", StringBytes("RetailScript")),
            Sub("SCHR", new byte[20]),
            Sub("SCTX", StringBytes("scn RetailScript\nBegin GameMode\nEnd")));
        var plan = ScriptVariableAugmentationPlanner.Apply(
            MakePlan(master),
            [new ScriptVariableAugmentation(
                ScriptFormId,
                new ScriptVariableInfo(1, "ProtoFlag", 1),
                declarationKind)]);

        var grup = new PlanWriter(PlannedEncoders.BuildRegistry(), new RecordingSink()).BuildGrupForType(
            "SCPT", plan, new PluginBuildOptions { CompressRecords = false });
        var tes4 = PluginRecordByteBuilder.BuildNewRecordBytes("TES4", 0, 0, []);
        var emitted = Assert.Single(
            EsmParser.EnumerateRecords([.. tes4, .. grup]),
            static record => record.Header.Signature == "SCPT");

        Assert.Contains(
            $"{expectedKeyword} ProtoFlag\nBegin GameMode",
            emitted.Subrecords.Single(static subrecord => subrecord.Signature == "SCTX").DataAsString,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Integer", 1, "int")]
    [InlineData("FloatOrReference", 0, "float")]
    public void StorageOnlyDeclaration_AppendsExactSlsdAndCanonicalFreshDeclaration(
        string declarationKindName,
        byte serializedType,
        string expectedKeyword)
    {
        var declarationKind = Enum.Parse<ScriptVariableDeclarationKind>(declarationKindName);
        var masterSource = StringBytes("scn RetailScript\nBegin GameMode\nEnd");
        var master = MakeMaster(
            Sub("EDID", StringBytes("RetailScript")),
            Sub("SCHR", new byte[20]),
            Sub("SCTX", masterSource),
            Sub("SCDA", [0x1D, 0x00, 0x00, 0x00]));

        var plan = ScriptVariableAugmentationPlanner.Apply(
            MakePlan(master),
            [new ScriptVariableAugmentation(
                ScriptFormId,
                new ScriptVariableInfo(1, "ProtoFlag", serializedType),
                declarationKind)]);
        var grup = new PlanWriter(PlannedEncoders.BuildRegistry(), new RecordingSink()).BuildGrupForType(
            "SCPT", plan, new PluginBuildOptions { CompressRecords = false });
        var tes4 = PluginRecordByteBuilder.BuildNewRecordBytes("TES4", 0, 0, []);
        var emitted = Assert.Single(
            EsmParser.EnumerateRecords([.. tes4, .. grup]),
            static record => record.Header.Signature == "SCPT");

        Assert.Equal(
            $"scn RetailScript\n{expectedKeyword} ProtoFlag\nBegin GameMode\nEnd",
            emitted.Subrecords.Single(static subrecord => subrecord.Signature == "SCTX").DataAsString);
        Assert.Equal(
            [0x1D, 0x00, 0x00, 0x00],
            emitted.Subrecords.Single(static subrecord => subrecord.Signature == "SCDA").Data);
        var slsd = emitted.Subrecords.Single(static subrecord => subrecord.Signature == "SLSD").Data;
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(slsd));
        Assert.Equal(serializedType, slsd[16]);
        Assert.Equal(
            "ProtoFlag",
            emitted.Subrecords.Single(static subrecord => subrecord.Signature == "SCVR").DataAsString);
    }

    [Fact]
    public void MixedConcreteAndStorageOnlyDeclarations_DeclareEveryFreshLocal()
    {
        var masterSource = StringBytes("scn RetailScript\r\nBegin GameMode\r\nEnd");
        var master = MakeMaster(
            Sub("EDID", StringBytes("RetailScript")),
            Sub("SCHR", new byte[20]),
            Sub("SCTX", masterSource),
            Sub("SCDA", [0x1D, 0x00, 0x00, 0x00]));
        var plan = ScriptVariableAugmentationPlanner.Apply(
            MakePlan(master),
            [
                new ScriptVariableAugmentation(
                    ScriptFormId,
                    new ScriptVariableInfo(1, "KnownFlag", 1),
                    ScriptVariableDeclarationKind.Short),
                new ScriptVariableAugmentation(
                    ScriptFormId,
                    new ScriptVariableInfo(2, "OpaqueValue", 0),
                    ScriptVariableDeclarationKind.FloatOrReference),
            ]);

        var sink = new RecordingSink();
        var grup = new PlanWriter(PlannedEncoders.BuildRegistry(), sink).BuildGrupForType(
            "SCPT", plan, new PluginBuildOptions { CompressRecords = false });
        var tes4 = PluginRecordByteBuilder.BuildNewRecordBytes("TES4", 0, 0, []);
        var emitted = Assert.Single(
            EsmParser.EnumerateRecords([.. tes4, .. grup]),
            static record => record.Header.Signature == "SCPT");

        Assert.Equal(
            "scn RetailScript\r\nshort KnownFlag\r\nfloat OpaqueValue\r\nBegin GameMode\r\nEnd",
            emitted.Subrecords.Single(static subrecord => subrecord.Signature == "SCTX").DataAsString);
        Assert.Equal(
            ["KnownFlag", "OpaqueValue"],
            emitted.Subrecords.Where(static subrecord => subrecord.Signature == "SCVR")
                .Select(static subrecord => subrecord.DataAsString));
        var provenance = Assert.Single(
            sink.Events,
            static evt => evt.Code == ScriptEmissionProvenanceReporter.EventCode);
        Assert.Equal("master-plus-declarations", provenance.Metadata!["sctx-proof-kind"]);
        Assert.Equal(
            "master-base-plus-fresh-local-declarations",
            provenance.Metadata["sctx-scda-semantic-match"]);
        Assert.Equal("2", provenance.Metadata["augmentation-declaration-count"]);
    }

    private static EmitPlan MakePlan(ParsedMainRecord master)
    {
        var record = new RecordPlan
        {
            Type = "SCPT",
            Disposition = RecordDisposition.KeepMaster,
            FormId = ScriptFormId,
            Model = null,
            Master = master,
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "master-only" },
        };
        return new EmitPlan
        {
            Records = [record],
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty,
            EmittedFormIds = ImmutableHashSet.Create(ScriptFormId),
            ValidScriptFormIds = ImmutableHashSet.Create(ScriptFormId),
            RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty.Add(ScriptFormId, 0),
            Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
            Meta = new PlanMetadata
            {
                NextObjectId = 0x800,
                PlannerCoverage = ImmutableHashSet.Create("SCPT"),
            },
        };
    }

    private static ParsedMainRecord MakeMaster(params ParsedSubrecord[] subrecords) => new()
    {
        Header = new MainRecordHeader
        {
            Signature = "SCPT",
            DataSize = 0,
            Flags = 0,
            FormId = ScriptFormId,
            Timestamp = 0,
            VcsInfo = 15,
            Version = 0,
        },
        Subrecords = [.. subrecords],
    };

    private static ParsedSubrecord Sub(string signature, byte[] data) => new()
    {
        Signature = signature,
        Data = data,
    };

    private static byte[] StringBytes(string value) => [.. Encoding.Latin1.GetBytes(value), 0];

    private sealed class RecordingSink : IConversionProgressSink
    {
        public List<ConversionProgressEvent> Events { get; } = [];

        public void OnPhaseStart(string phase, int? totalItems) { }
        public void OnEvent(ConversionProgressEvent evt) => Events.Add(evt);
        public void OnPhaseEnd(string phase, ConversionPipelineStats partialStats) { }
        public void OnComplete(ConversionPipelineStats stats) { }
    }
}
