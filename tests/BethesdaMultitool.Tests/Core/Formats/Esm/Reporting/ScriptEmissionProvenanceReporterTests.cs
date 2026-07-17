using System.Security.Cryptography;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Reporting;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using EsmStringUtils = BethesdaMultitool.Core.Utils.EsmStringUtils;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Reporting;

public sealed class ScriptEmissionProvenanceReporterTests
{
    [Fact]
    public void NewRuntimeScript_ReportsExactFinalPayloadsAndSemanticChanges()
    {
        var source = new ScriptRecord
        {
            FormId = 0x00123456,
            EditorId = "CapturedScript",
            SourceText = "scn CapturedScript\r\nshort Flag\r\n; “same dump”",
            SourceTextOrigin = ScriptSourceTextOrigin.RuntimeSameObject,
            CompiledData = [0x00, 0x1D, 0x00, 0x00],
            CompiledSize = 4,
            VariableCount = 1,
            RefObjectCount = 1,
            ExecutableBundleFromRuntime = true,
            DecompiledText = "ScriptName CapturedScript",
            IsBigEndian = true,
            Variables = [new ScriptVariableInfo(7, "Flag", 1)],
            ReferencedObjects = [0x00111111],
        };
        var sctx = GameTextBytes(source.SourceText);
        var scda = new byte[] { 0x1D, 0x00, 0x00, 0x00 };
        var slsd = new byte[24];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(slsd, 7);
        slsd[16] = 1;
        var emitted = new EncodedSubrecord[]
        {
            new("SCTX", sctx),
            new("SCDA", scda),
            new("SLSD", slsd),
            new("SCVR", StringBytes("Flag")),
            new("SCRO", BitConverter.GetBytes(0x00222222u)),
        };
        var sink = new RecordingSink();

        ScriptEmissionProvenanceReporter.ReportNewScript(
            sink, source, 0x01000800, emitted);

        var evt = Assert.Single(sink.Events);
        Assert.Equal(ScriptEmissionProvenanceReporter.EventCode, evt.Code);
        Assert.Equal(0x01000800u, evt.FormId);
        Assert.Equal("0x00123456", evt.Metadata!["source-form-id"]);
        Assert.Equal("0x01000800", evt.Metadata["emitted-form-id"]);
        Assert.Equal("CapturedScript", evt.Metadata["editor-id"]);
        Assert.Equal("runtime-same-object", evt.Metadata["source-origin"]);
        Assert.Equal("captured-exact", evt.Metadata["sctx-proof-kind"]);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(sctx)), evt.Metadata["sctx-sha256"]);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(sctx)), evt.Metadata["expected-sctx-sha256"]);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(sctx)), evt.Metadata["base-sctx-sha256"]);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.SourceText))),
            evt.Metadata["captured-source-utf8-sha256"]);
        Assert.Equal("0", evt.Metadata["augmentation-declaration-count"]);
        Assert.Null(evt.Metadata["augmentation-declarations-base64"]);
        Assert.Null(evt.Metadata["augmentation-declarations-sha256"]);
        Assert.Equal(source.SourceText.Length.ToString(), evt.Metadata["sctx-decoded-length"]);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(scda)), evt.Metadata["scda-sha256"]);
        Assert.Equal("4", evt.Metadata["scda-length"]);
        Assert.Equal("true", evt.Metadata["bytecode-changed-from-source"]);
        Assert.Equal("true", evt.Metadata["tables-changed-from-source"]);
        Assert.Equal("proven-zero-nontolerated-mismatches", evt.Metadata["sctx-scda-semantic-match"]);
        Assert.Equal("0", evt.Metadata["sctx-scda-match-count"]);
        Assert.Equal("0", evt.Metadata["sctx-scda-tolerated-count"]);
        Assert.Equal(string.Empty, evt.Metadata["sctx-scda-tolerated-categories"]);
    }

    [Fact]
    public void CapturedProof_ExpectedHashComesFromSourceText_NotEmittedPayload()
    {
        var source = new ScriptRecord
        {
            FormId = 0x00123456,
            EditorId = "CapturedScript",
            SourceText = "scn CapturedScript\r\n; it’s captured",
            SourceTextOrigin = ScriptSourceTextOrigin.RuntimeSameObject,
        };
        var emittedSctx = GameTextBytes("scn DifferentScript");
        var sink = new RecordingSink();

        ScriptEmissionProvenanceReporter.ReportNewScript(
            sink,
            source,
            0x01000800,
            [new EncodedSubrecord("SCTX", emittedSctx)]);

        var metadata = Assert.Single(sink.Events).Metadata!;
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(GameTextBytes(source.SourceText))),
            metadata["expected-sctx-sha256"]);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.SourceText))),
            metadata["captured-source-utf8-sha256"]);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(emittedSctx)),
            metadata["sctx-sha256"]);
        Assert.NotEqual(metadata["expected-sctx-sha256"], metadata["sctx-sha256"]);
        Assert.Equal("source-only-no-scda", metadata["sctx-scda-semantic-match"]);
        Assert.Null(metadata["sctx-scda-match-count"]);
        Assert.Null(metadata["sctx-scda-tolerated-count"]);
        Assert.Null(metadata["sctx-scda-tolerated-categories"]);
    }

    [Fact]
    public void Augmentation_DoesNotInventCapturedScriptIdentity()
    {
        var scda = new byte[] { 0x1D, 0x00 };
        var originalSlsd = new byte[24];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(originalSlsd, 7);
        originalSlsd[16] = 1;
        const string masterSource = "scn RetailScript\r\nBegin GameMode\r\nEnd";
        const string augmentedSource =
            "scn RetailScript\r\nshort RecoveredFlag\r\nBegin GameMode\r\nEnd";
        var master = new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "SCPT",
                FormId = 0x0000ABCD,
                DataSize = 0,
            },
            Subrecords =
            [
                Sub("EDID", StringBytes("RetailScript")),
                Sub("SCDA", scda),
                Sub("SCTX", GameTextBytes(masterSource)),
                Sub("SLSD", originalSlsd),
                Sub("SCVR", StringBytes("RetailFlag")),
            ],
        };
        var addedSlsd = new byte[24];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(addedSlsd, 8);
        addedSlsd[16] = 1;
        var emitted = master.Subrecords
            .Select(subrecord => subrecord.Signature == "SCTX"
                ? Sub("SCTX", GameTextBytes(augmentedSource))
                : subrecord)
            .Concat([
                Sub("SLSD", addedSlsd),
                Sub("SCVR", StringBytes("RecoveredFlag")),
            ])
            .ToArray();
        var augmentation = new ScriptVariableAugmentation(
            master.Header.FormId,
            new ScriptVariableInfo(8, "RecoveredFlag", 1),
            ScriptVariableDeclarationKind.Short);
        var sink = new RecordingSink();

        ScriptEmissionProvenanceReporter.ReportAugmentedMasterScript(
            sink, master, emitted, [augmentation]);

        var evt = Assert.Single(sink.Events);
        Assert.Null(evt.Metadata!["source-form-id"]);
        Assert.Equal("0x0000ABCD", evt.Metadata["emitted-form-id"]);
        Assert.Equal("augmentation", evt.Metadata["source-origin"]);
        Assert.Equal("master-plus-declarations", evt.Metadata["sctx-proof-kind"]);
        Assert.Null(evt.Metadata["captured-source-utf8-sha256"]);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(GameTextBytes(masterSource))),
            evt.Metadata["base-sctx-sha256"]);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(GameTextBytes(augmentedSource))),
            evt.Metadata["expected-sctx-sha256"]);
        Assert.Equal("1", evt.Metadata["augmentation-declaration-count"]);
        var declarationBytes = Convert.FromBase64String(
            evt.Metadata["augmentation-declarations-base64"]!);
        Assert.Equal("short RecoveredFlag", EsmStringUtils.DecodeGameText(declarationBytes));
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(declarationBytes)),
            evt.Metadata["augmentation-declarations-sha256"]);
        Assert.Equal("false", evt.Metadata["bytecode-changed-from-source"]);
        Assert.Equal("true", evt.Metadata["tables-changed-from-source"]);
        Assert.Equal("master-base-plus-fresh-local-declarations", evt.Metadata["sctx-scda-semantic-match"]);
        Assert.Null(evt.Metadata["sctx-scda-match-count"]);
        Assert.Null(evt.Metadata["sctx-scda-tolerated-count"]);
        Assert.Null(evt.Metadata["sctx-scda-tolerated-categories"]);
    }

    [Fact]
    public void StorageOnlyAugmentation_ReportsCanonicalFreshDeclaration()
    {
        var source = GameTextBytes("scn RetailScript\r\nBegin GameMode\r\nEnd");
        var scda = new byte[] { 0x1D, 0x00 };
        var master = new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "SCPT",
                FormId = 0x0000ABCD,
                DataSize = 0,
            },
            Subrecords =
            [
                Sub("EDID", StringBytes("RetailScript")),
                Sub("SCDA", scda),
                Sub("SCTX", source),
            ],
        };
        var addedSlsd = new byte[24];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(addedSlsd, 8);
        addedSlsd[16] = 1;
        var augmentedSource = GameTextBytes(
            "scn RetailScript\r\nint RecoveredFlag\r\nBegin GameMode\r\nEnd");
        var emitted = master.Subrecords
            .Select(subrecord => subrecord.Signature == "SCTX"
                ? Sub("SCTX", augmentedSource)
                : subrecord)
            .Concat(
        [
            Sub("SLSD", addedSlsd),
            Sub("SCVR", StringBytes("RecoveredFlag")),
        ]).ToArray();
        var augmentation = new ScriptVariableAugmentation(
            master.Header.FormId,
            new ScriptVariableInfo(8, "RecoveredFlag", 1),
            ScriptVariableDeclarationKind.Integer);
        var sink = new RecordingSink();

        ScriptEmissionProvenanceReporter.ReportAugmentedMasterScript(
            sink, master, emitted, [augmentation]);

        var metadata = Assert.Single(sink.Events).Metadata!;
        var sourceHash = Convert.ToHexString(SHA256.HashData(augmentedSource));
        Assert.Equal("augmentation", metadata["source-origin"]);
        Assert.Equal("master-plus-declarations", metadata["sctx-proof-kind"]);
        Assert.Equal(sourceHash, metadata["sctx-sha256"]);
        Assert.Equal(sourceHash, metadata["expected-sctx-sha256"]);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(source)), metadata["base-sctx-sha256"]);
        Assert.Equal("1", metadata["augmentation-declaration-count"]);
        Assert.Equal(
            Convert.ToBase64String(EsmStringUtils.EncodeGameText("int RecoveredFlag")),
            metadata["augmentation-declarations-base64"]);
        Assert.Equal("false", metadata["bytecode-changed-from-source"]);
        Assert.Equal("true", metadata["tables-changed-from-source"]);
        Assert.Equal(
            "master-base-plus-fresh-local-declarations",
            metadata["sctx-scda-semantic-match"]);
    }

    [Fact]
    public void Augmentation_RejectsSctxThatOmitsFreshDeclaration()
    {
        var source = GameTextBytes("scn RetailScript\nBegin GameMode\nEnd");
        var master = AugmentationMaster(source, [0x1D, 0x00]);
        var emitted = master.Subrecords.Concat(
        [
            Sub("SLSD", LocalSlsd(8, 1)),
            Sub("SCVR", StringBytes("RecoveredFlag")),
        ]).ToArray();
        var augmentation = new ScriptVariableAugmentation(
            master.Header.FormId,
            new ScriptVariableInfo(8, "RecoveredFlag", 1),
            ScriptVariableDeclarationKind.Integer);

        var error = Assert.Throws<InvalidOperationException>(() =>
            ScriptEmissionProvenanceReporter.ReportAugmentedMasterScript(
                new RecordingSink(), master, emitted, [augmentation]));

        Assert.Contains("emitted SCTX", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Augmentation_RejectsChangedMasterBytecode()
    {
        var master = AugmentationMaster(
            GameTextBytes("scn RetailScript\nBegin GameMode\nEnd"),
            [0x1D, 0x00]);
        var emitted = master.Subrecords
            .Select(subrecord => subrecord.Signature switch
            {
                "SCTX" => Sub("SCTX", GameTextBytes(
                    "scn RetailScript\nint RecoveredFlag\nBegin GameMode\nEnd")),
                "SCDA" => Sub("SCDA", [0x1D, 0x01]),
                _ => subrecord,
            })
            .Concat(
            [
                Sub("SLSD", LocalSlsd(8, 1)),
                Sub("SCVR", StringBytes("RecoveredFlag")),
            ]).ToArray();
        var augmentation = new ScriptVariableAugmentation(
            master.Header.FormId,
            new ScriptVariableInfo(8, "RecoveredFlag", 1),
            ScriptVariableDeclarationKind.Integer);

        var error = Assert.Throws<InvalidOperationException>(() =>
            ScriptEmissionProvenanceReporter.ReportAugmentedMasterScript(
                new RecordingSink(), master, emitted, [augmentation]));

        Assert.Contains("changed retained master SCDA", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompiledCapturedSource_WithSemanticMismatch_FailsClosed()
    {
        var source = new ScriptRecord
        {
            FormId = 0x00123456,
            EditorId = "StaleScript",
            SourceText = "scn StaleScript\r\nBegin OnTriggerEnter ARef\r\nEnd",
            SourceTextOrigin = ScriptSourceTextOrigin.RuntimeSameObject,
            CompiledData = [0x00, 0x1D, 0x00, 0x00],
            CompiledSize = 4,
            ExecutableBundleFromRuntime = true,
            DecompiledText = "ScriptName StaleScript\r\nBegin OnTriggerEnter BRef\r\nEnd",
            IsBigEndian = true,
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ScriptEmissionProvenanceReporter.ReportNewScript(
                new RecordingSink(),
                source,
                0x01000800,
                [
                    new EncodedSubrecord("SCTX", GameTextBytes(source.SourceText)),
                    new EncodedSubrecord("SCDA", [0x1D, 0x00, 0x00, 0x00]),
                ]));

        Assert.Contains("shared emission contract rejected", exception.Message, StringComparison.Ordinal);
        Assert.Contains("mismatch", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompiledCapturedSource_WithWrongDeclarationStorage_FailsClosed()
    {
        var source = new ScriptRecord
        {
            FormId = 0x00123456,
            EditorId = "CapturedScript",
            SourceText = "scn CapturedScript\r\nfloat Flag",
            SourceTextOrigin = ScriptSourceTextOrigin.RuntimeSameObject,
            CompiledData = [0x00, 0x1D, 0x00, 0x00],
            CompiledSize = 4,
            VariableCount = 1,
            ExecutableBundleFromRuntime = true,
            DecompiledText = "ScriptName CapturedScript",
            IsBigEndian = true,
            Variables = [new ScriptVariableInfo(7, "Flag", 1)],
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ScriptEmissionProvenanceReporter.ReportNewScript(
                new RecordingSink(),
                source,
                0x01000800,
                [
                    new EncodedSubrecord("SCTX", GameTextBytes(source.SourceText)),
                    new EncodedSubrecord("SCDA", [0x1D, 0x00, 0x00, 0x00]),
                ]));

        Assert.Contains("shared emission contract rejected", exception.Message, StringComparison.Ordinal);
        Assert.Contains("storage", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ParsedMainRecord AugmentationMaster(byte[] source, byte[] scda) => new()
    {
        Header = new MainRecordHeader
        {
            Signature = "SCPT",
            FormId = 0x0000ABCD,
            DataSize = 0,
        },
        Subrecords =
        [
            Sub("EDID", StringBytes("RetailScript")),
            Sub("SCDA", scda),
            Sub("SCTX", source),
        ],
    };

    private static byte[] LocalSlsd(uint index, byte type)
    {
        var slsd = new byte[24];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(slsd, index);
        slsd[16] = type;
        return slsd;
    }

    private static ParsedSubrecord Sub(string signature, byte[] data) => new()
    {
        Signature = signature,
        Data = data,
    };

    private static byte[] StringBytes(string value) =>
        GameTextBytes(value);

    private static byte[] GameTextBytes(string value) =>
        [.. EsmStringUtils.EncodeGameText(value), 0];

    private sealed class RecordingSink : IConversionProgressSink
    {
        public List<ConversionProgressEvent> Events { get; } = [];

        public void OnPhaseStart(string phase, int? totalItems) { }
        public void OnEvent(ConversionProgressEvent evt) => Events.Add(evt);
        public void OnPhaseEnd(string phase, ConversionPipelineStats partialStats) { }
        public void OnComplete(ConversionPipelineStats stats) { }
    }
}
