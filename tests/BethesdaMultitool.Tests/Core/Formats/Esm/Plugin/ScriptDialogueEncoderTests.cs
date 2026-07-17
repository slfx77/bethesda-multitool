using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.AI;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     v6 tests for the script + dialogue + quest + package encoders. Each test verifies a
///     specific subrecord byte layout against the PDB-confirmed schema definitions in
///     <see cref="BethesdaMultitool.Core.Formats.Esm.Conversion.Schema.SubrecordDialogueSchemas" />.
/// </summary>
public class ScriptDialogueEncoderTests
{
    // ====================================================================================
    // SCPT — Script
    // ====================================================================================

    [Fact]
    public void ScptEncoder_EncodeNew_EmitsEdidAndSchrInOrder()
    {
        // SCHR canonical ESM layout per fopdoc:
        //   offset 0..3:   Unused (zero-filled padding)
        //   offset 4..7:   RefCount
        //   offset 8..11:  CompiledSize
        //   offset 12..15: VariableCount (= Variables.Count emitted as SLSDs)
        //   offset 16..17: Type uint16 (0=Object, 1=Quest, 0x100=Effect)
        //   offset 18..19: Flags uint16 (0x0001=Enabled)
        var script = new ScriptRecord
        {
            FormId = 0x800,
            EditorId = "MyScript",
            RefObjectCount = 2,
            CompiledSize = 16,
            IsQuestScript = true,
            IsMagicEffectScript = false,
            IsCompiled = true,
            ReferencedObjects = [0x00000014, 0x00000007],
            // 3 local variables — the encoder's VariableCount field is derived from
            // Variables.Count, not from the runtime VariableCount property, so the SLSD
            // entries we emit always match the engine's VariableCount expectation.
            Variables =
            [
                new ScriptVariableInfo(1, "var1", 0),
                new ScriptVariableInfo(2, "var2", 0),
                new ScriptVariableInfo(3, "var3", 0)
            ]
        };

        var encoded = ScptEncoder.EncodeNew(script);

        Assert.Equal("EDID", encoded.Subrecords[0].Signature);
        Assert.Equal("SCHR", encoded.Subrecords[1].Signature);

        var schr = encoded.Subrecords[1].Bytes;
        Assert.Equal(20, schr.Length);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(schr.AsSpan(0, 4))); // Unused
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(schr.AsSpan(4, 4))); // RefCount
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(schr.AsSpan(8, 4))); // CompiledSize
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(schr.AsSpan(12, 4))); // VariableCount
        Assert.Equal(0x0001, BinaryPrimitives.ReadUInt16LittleEndian(schr.AsSpan(16, 2))); // Type = Quest
        Assert.Equal(0x0001, BinaryPrimitives.ReadUInt16LittleEndian(schr.AsSpan(18, 2))); // Flags = Enabled
    }

    [Fact]
    public void ScptEncoder_EncodeNew_WithCompiledDataAndSourceText_EmitsScdaAndSctx()
    {
        var script = new ScriptRecord
        {
            FormId = 0x800,
            EditorId = "S",
            CompiledData = [0x10, 0x20, 0x30, 0x40],
            SourceText = "ScriptName MyScript\nBegin GameMode\nEnd"
        };

        var encoded = ScptEncoder.EncodeNew(script);

        var scda = Assert.Single(encoded.Subrecords, s => s.Signature == "SCDA");
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0x40 }, scda.Bytes);

        var sctx = Assert.Single(encoded.Subrecords, s => s.Signature == "SCTX");
        // Windows-1252 game text + null terminator.
        Assert.Equal(script.SourceText.Length + 1, sctx.Bytes.Length);
        Assert.Equal(0, sctx.Bytes[^1]);
    }

    [Fact]
    public void ScptEncoder_EncodeNew_SourceOnlyScriptUsesExactDeclaredNameAndPreservesSctx()
    {
        const string source =
            "; captured debug source\r\nScriptName ExactCaseScript\r\nshort recoveredFlag\r\n";
        var script = new ScriptRecord
        {
            FormId = 0x801,
            SourceText = source
        };

        var encoded = ScptEncoder.EncodeNew(script);

        var edid = Assert.Single(encoded.Subrecords, subrecord => subrecord.Signature == "EDID");
        Assert.Equal("ExactCaseScript\0", Encoding.Latin1.GetString(edid.Bytes));
        var sctx = Assert.Single(encoded.Subrecords, subrecord => subrecord.Signature == "SCTX");
        Assert.Equal(source + "\0", Encoding.Latin1.GetString(sctx.Bytes));
        Assert.DoesNotContain(encoded.Warnings, warning =>
            warning.Contains("no EditorId", StringComparison.Ordinal));
        Assert.Equal(["ExactCaseScript"], encoded.EmittedScriptPaths);
    }

    [Fact]
    public void ScptEncoder_EncodeNew_SctxRoundTripsWindows1252ExtensionBytes()
    {
        var capturedSource = Enumerable.Range(0x80, 0x20)
            .Select(static value => (byte)value)
            .ToArray();
        var script = new ScriptRecord
        {
            FormId = 0x800,
            EditorId = "S",
            SourceText = BethesdaMultitool.Core.Utils.EsmStringUtils.DecodeGameText(capturedSource)
        };

        var encoded = ScptEncoder.EncodeNew(script);

        var sctx = Assert.Single(encoded.Subrecords, s => s.Signature == "SCTX");
        Assert.Equal([.. capturedSource, 0], sctx.Bytes);
    }

    [Fact]
    public void ScptEncoder_EncodeNew_VariablesEmittedAsSlsdScvrPairs()
    {
        var script = new ScriptRecord
        {
            FormId = 0x800,
            EditorId = "S",
            Variables =
            {
                new ScriptVariableInfo(1, "iCount", 1),
                new ScriptVariableInfo(2, "fTimer", 0)
            }
        };

        var encoded = ScptEncoder.EncodeNew(script);

        var slsdRecords = encoded.Subrecords.Where(s => s.Signature == "SLSD").ToList();
        var scvrRecords = encoded.Subrecords.Where(s => s.Signature == "SCVR").ToList();

        Assert.Equal(2, slsdRecords.Count);
        Assert.Equal(2, scvrRecords.Count);

        // First SLSD layout (PDB SCRIPT_LOCAL): Index@0, padding@4-7, Value@8-15, IsInteger@16, padding@17-23.
        Assert.Equal(24, slsdRecords[0].Bytes.Length);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(slsdRecords[0].Bytes.AsSpan(0, 4)));
        Assert.Equal(1, slsdRecords[0].Bytes[16]); // IsInteger

        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(slsdRecords[1].Bytes.AsSpan(0, 4)));
        Assert.Equal(0, slsdRecords[1].Bytes[16]); // float
    }

    [Fact]
    public void ScptEncoder_EncodeNew_ScroAndScrvBranchOnHighBit()
    {
        var script = new ScriptRecord
        {
            FormId = 0x800,
            EditorId = "S",
            Variables = { new ScriptVariableInfo(5, "target", 0) },
            ReferencedObjects = { 0x12345678, 0x80000005, 0xABCDEF }
        };

        var encoded = ScptEncoder.EncodeNew(script);

        var scro = encoded.Subrecords.Where(s => s.Signature == "SCRO").ToList();
        var scrv = encoded.Subrecords.Where(s => s.Signature == "SCRV").ToList();

        Assert.Equal(2, scro.Count);
        Assert.Single(scrv);

        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(scro[0].Bytes));
        Assert.Equal(0xABCDEFu, BinaryPrimitives.ReadUInt32LittleEndian(scro[1].Bytes));
        // SCRV index has the high bit stripped.
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(scrv[0].Bytes));
    }

    [Fact]
    public void ScptEncoder_EncodeNew_SuppressesWholeScriptWhenScroDoesNotResolve()
    {
        var script = new ScriptRecord
        {
            FormId = 0x800,
            EditorId = "UnsafeScript",
            ReferencedObjects = [0x00ABCDEF]
        };

        var encoded = ScptEncoder.EncodeNew(script, new HashSet<uint>(), new Dictionary<uint, uint>());

        Assert.Empty(encoded.Subrecords);
        Assert.Contains(encoded.Warnings, warning =>
            warning.Contains("SCRO[0] 0x00ABCDEF does not resolve", StringComparison.Ordinal));
    }

    [Fact]
    public void ScptEncoder_EncodeNew_SuppressesWholeScriptWhenScrvHasNoLocal()
    {
        var script = new ScriptRecord
        {
            FormId = 0x800,
            EditorId = "UnsafeScript",
            ReferencedObjects = [0x80000005]
        };

        var encoded = ScptEncoder.EncodeNew(script);

        Assert.Empty(encoded.Subrecords);
        Assert.Contains(encoded.Warnings, warning =>
            warning.Contains("SCRV[0] variable 5 has no matching SLSD", StringComparison.Ordinal));
    }

    // ====================================================================================
    // DIAL — Dialog Topic
    // ====================================================================================

    [Fact]
    public void DialEncoder_EncodeNew_EmitsRequiredEdidAndData()
    {
        var dial = new DialogTopicRecord
        {
            FormId = 0x900,
            EditorId = "GREETING",
            TopicType = 1,
            Flags = 0x02
        };

        var encoded = DialEncoder.EncodeNew(dial);

        Assert.Equal("EDID", encoded.Subrecords[0].Signature);
        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");
        Assert.Equal(2, data.Bytes.Length);
        Assert.Equal(1, data.Bytes[0]); // TopicType
        Assert.Equal(0x02, data.Bytes[1]); // Flags
    }

    [Fact]
    public void DialEncoder_EncodeNew_AllOptionals_CanonicalOrderNoTnam()
    {
        var dial = new DialogTopicRecord
        {
            FormId = 0x900,
            EditorId = "Topic",
            FullName = "Hello there",
            QuestFormId = 0x100,
            SpeakerFormId = 0x200,
            Priority = 1.5f
        };

        var encoded = DialEncoder.EncodeNew(dial);

        // xEdit-canonical FNV order: EDID, QSTI, FULL, PNAM, DATA — DATA last, QSTI
        // before FULL. The engine's sequential DIAL reader misparses any other order
        // (FNVEdit: "unexpected (or out of order) subrecord").
        var signatures = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Equal(["EDID", "QSTI", "FULL", "PNAM", "DATA"], signatures);

        var qsti = Assert.Single(encoded.Subrecords, s => s.Signature == "QSTI");
        Assert.Equal(0x100u, BinaryPrimitives.ReadUInt32LittleEndian(qsti.Bytes));

        var pnam = Assert.Single(encoded.Subrecords, s => s.Signature == "PNAM");
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(pnam.Bytes));

        // FNV DIAL has no TNAM; the captured speaker link is dropped with a warning.
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "TNAM");
        Assert.Contains(encoded.Warnings, w => w.Contains("TNAM"));
    }

    [Fact]
    public void DialEncoder_EncodeNew_MinimalTopic_EmitsRequiredPnamWithGeckDefault()
    {
        var dial = new DialogTopicRecord { FormId = 0x900, EditorId = "Topic" };
        var encoded = DialEncoder.EncodeNew(dial);

        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "FULL");
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "QSTI");
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "TNAM");

        // PNAM is REQUIRED per xEdit (default 50); DATA must be the last subrecord.
        var pnam = Assert.Single(encoded.Subrecords, s => s.Signature == "PNAM");
        Assert.Equal(50f, BinaryPrimitives.ReadSingleLittleEndian(pnam.Bytes));
        Assert.Equal("DATA", encoded.Subrecords[^1].Signature);
    }

    // ====================================================================================
    // INFO — Dialogue Response
    // ====================================================================================

    [Fact]
    public void InfoEncoder_EncodeNew_DataIsFourBytesWithFlags()
    {
        var info = new DialogueRecord
        {
            FormId = 0x901,
            InfoFlags = 0x01,
            InfoFlagsExt = 0x02
        };

        var encoded = InfoEncoder.EncodeNew(info);

        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");
        Assert.Equal(4, data.Bytes.Length);
        Assert.Equal(0, data.Bytes[0]); // DialType — default
        Assert.Equal(0, data.Bytes[1]); // NextSpeaker — default
        Assert.Equal(0x01, data.Bytes[2]); // Flags
        Assert.Equal(0x02, data.Bytes[3]); // Flags2
    }

    [Fact]
    public void InfoEncoder_EncodeNew_ResponsesEmitTrdtAndNam1Pairs()
    {
        var info = new DialogueRecord
        {
            FormId = 0x901,
            Responses =
            {
                new DialogueResponse
                {
                    Text = "Hello.",
                    EmotionType = 5,
                    EmotionValue = 50,
                    ResponseNumber = 1,
                    SoundFormId = 0x0000BEEF
                },
                new DialogueResponse
                {
                    Text = "Goodbye.",
                    EmotionType = 0,
                    EmotionValue = 0,
                    ResponseNumber = 2
                }
            }
        };

        var encoded = InfoEncoder.EncodeNew(info);

        var trdtRecords = encoded.Subrecords.Where(s => s.Signature == "TRDT").ToList();
        var nam1Records = encoded.Subrecords.Where(s => s.Signature == "NAM1").ToList();
        Assert.Equal(2, trdtRecords.Count);
        Assert.Equal(2, nam1Records.Count);

        var trdt0 = trdtRecords[0].Bytes;
        Assert.Equal(24, trdt0.Length);
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(trdt0.AsSpan(0, 4))); // EmotionType
        Assert.Equal(50, BinaryPrimitives.ReadInt32LittleEndian(trdt0.AsSpan(4, 4))); // EmotionValue
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(trdt0.AsSpan(8, 4))); // ConvTopic (zero)
        Assert.Equal(1, trdt0[12]); // ResponseNumber
        Assert.Equal(0x0000BEEFu, BinaryPrimitives.ReadUInt32LittleEndian(trdt0.AsSpan(16, 4))); // Sound
        Assert.Equal(0, trdt0[20]); // UseEmotionAnim
    }

    [Fact]
    public void InfoEncoder_EncodeNew_CtdaConditionLayout()
    {
        var info = new DialogueRecord
        {
            FormId = 0x901,
            Conditions =
            {
                new DialogueCondition
                {
                    Type = 0x80,
                    ComparisonValue = 1.0f,
                    FunctionIndex = 0x48,
                    Parameter1 = 0x12345,
                    Parameter2 = 0x6789,
                    RunOn = 1,
                    Reference = 0xABCDE
                }
            }
        };

        var encoded = InfoEncoder.EncodeNew(info);

        var ctda = Assert.Single(encoded.Subrecords, s => s.Signature == "CTDA");
        Assert.Equal(28, ctda.Bytes.Length);
        Assert.Equal(0x80, ctda.Bytes[0]);
        Assert.Equal(1.0f, BinaryPrimitives.ReadSingleLittleEndian(ctda.Bytes.AsSpan(4, 4)));
        Assert.Equal((ushort)0x48, BinaryPrimitives.ReadUInt16LittleEndian(ctda.Bytes.AsSpan(8, 2)));
        Assert.Equal(0x12345u, BinaryPrimitives.ReadUInt32LittleEndian(ctda.Bytes.AsSpan(12, 4)));
        Assert.Equal(0x6789u, BinaryPrimitives.ReadUInt32LittleEndian(ctda.Bytes.AsSpan(16, 4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(ctda.Bytes.AsSpan(20, 4)));
        Assert.Equal(0xABCDEu, BinaryPrimitives.ReadUInt32LittleEndian(ctda.Bytes.AsSpan(24, 4)));
    }

    [Fact]
    public void InfoEncoder_EncodeNew_LinkTopicsEmittedAsTcltTclfName()
    {
        var info = new DialogueRecord
        {
            FormId = 0x901,
            LinkToTopics = { 0x100, 0x200 },
            LinkFromTopics = { 0x300 },
            AddTopics = { 0x400, 0x500 }
        };

        var encoded = InfoEncoder.EncodeNew(info);

        Assert.Equal(2, encoded.Subrecords.Count(s => s.Signature == "TCLT"));
        Assert.Single(encoded.Subrecords, s => s.Signature == "TCLF");
        Assert.Equal(2, encoded.Subrecords.Count(s => s.Signature == "NAME"));
    }

    [Fact]
    public void InfoEncoder_EncodeNew_FollowUpInfosEmittedAsTcfu()
    {
        var info = new DialogueRecord
        {
            FormId = 0x901,
            FollowUpInfos = { 0x00112233, 0x00445566 }
        };

        var encoded = InfoEncoder.EncodeNew(info);

        var tcfuRecords = encoded.Subrecords.Where(s => s.Signature == "TCFU").ToList();
        Assert.Equal(2, tcfuRecords.Count);
        Assert.Equal(0x00112233u, BinaryPrimitives.ReadUInt32LittleEndian(tcfuRecords[0].Bytes));
        Assert.Equal(0x00445566u, BinaryPrimitives.ReadUInt32LittleEndian(tcfuRecords[1].Bytes));
    }

    [Fact]
    public void InfoEncoder_EncodeNew_ResultScript_EmitsSchrAndScda()
    {
        var info = new DialogueRecord
        {
            FormId = 0x901,
            HasResultScript = true,
            ResultScripts =
            {
                new DialogueResultScript
                {
                    SourceText = "set foo to 1",
                    CompiledData = [0xAA, 0xBB],
                    ReferencedObjects = { 0x1111 }
                }
            }
        };

        var encoded = InfoEncoder.EncodeNew(info);

        var schr = encoded.Subrecords.First(s => s.Signature == "SCHR");
        Assert.Equal(20, schr.Bytes.Length);
        // SCHR canonical ESM layout per fopdoc — INFO result scripts declare VariableCount=0
        // because they don't carry their own SLSD/SCVR list; Type=0 (Object) and Flags=0x0001
        // (Enabled, because CompiledData is non-empty so the engine treats this as compiled).
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(schr.Bytes.AsSpan(0, 4))); // Unused
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(schr.Bytes.AsSpan(4, 4))); // RefCount
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(schr.Bytes.AsSpan(8, 4))); // CompiledSize
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(schr.Bytes.AsSpan(12, 4))); // VariableCount
        Assert.Equal(0x0000, BinaryPrimitives.ReadUInt16LittleEndian(schr.Bytes.AsSpan(16, 2))); // Type = Object
        Assert.Equal(0x0001, BinaryPrimitives.ReadUInt16LittleEndian(schr.Bytes.AsSpan(18, 2))); // Flags = Enabled

        var scda = Assert.Single(encoded.Subrecords, s => s.Signature == "SCDA");
        Assert.Equal(new byte[] { 0xAA, 0xBB }, scda.Bytes);

        var sctx = Assert.Single(encoded.Subrecords, s => s.Signature == "SCTX");
        Assert.NotEmpty(sctx.Bytes);

        var scro = Assert.Single(encoded.Subrecords, s => s.Signature == "SCRO");
        Assert.Equal(0x1111u, BinaryPrimitives.ReadUInt32LittleEndian(scro.Bytes));
    }

    [Fact]
    public void DialogueResultScriptParser_DoesNotMarkLittleEndianScdaBigEndianFromRecordWrapper()
    {
        byte[] littleEndianScda =
        [
            0x15, 0x00, 0x0B, 0x00,
            0x66,
            0x00, 0x00,
            0x06, 0x00,
            0x20, 0x6E, 0x01, 0x00, 0x00, 0x00
        ];
        var schr = new byte[20];
        BinaryPrimitives.WriteUInt32BigEndian(schr.AsSpan(8), (uint)littleEndianScda.Length);

        var data = BuildSubrecordStream(
            true,
            ("SCHR", schr),
            ("SCDA", littleEndianScda));

        var scripts = DialogueResultScriptParser.ParseResultScriptsFromSubrecords(
            data,
            data.Length,
            true,
            null,
            0x01003FED,
            _ => null);

        var script = Assert.Single(scripts);
        Assert.False(script.IsBigEndianBytecode);

        var encoded = InfoEncoder.EncodeNew(new DialogueRecord
        {
            FormId = 0x01003FED,
            ResultScripts = { script }
        });
        var scda = Assert.Single(encoded.Subrecords, sub => sub.Signature == "SCDA");
        Assert.Equal(littleEndianScda, scda.Bytes);
    }

    [Fact]
    public void DialogueResultScriptParser_PreservesLocalsAndOrderedMixedReferenceTable()
    {
        var slsd = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(slsd, 7);
        slsd[16] = 1;
        byte[] formId = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(formId, 0x00001234);
        byte[] variableId = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(variableId, 7);
        byte[] littleEndianScda = [0x1D, 0x00, 0x00, 0x00];
        var schr = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(schr.AsSpan(4), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(schr.AsSpan(8), (uint)littleEndianScda.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(schr.AsSpan(12), 1);
        var data = BuildSubrecordStream(
            false,
            ("SCHR", schr),
            ("SCDA", littleEndianScda),
            ("SLSD", slsd),
            ("SCVR", Encoding.ASCII.GetBytes("GameScore\0")),
            ("SCRV", variableId),
            ("SCRO", formId),
            ("SCRO", formId),
            ("SCRV", variableId));

        var script = Assert.Single(DialogueResultScriptParser.ParseResultScriptsFromSubrecords(
            data,
            data.Length,
            false,
            "PrototypeInfo",
            0x01003FED,
            _ => null));

        Assert.Equal(new ScriptVariableInfo(7, "GameScore", 1), Assert.Single(script.Variables));
        Assert.Equal(
            [0x80000007u, 0x00001234u, 0x00001234u, 0x80000007u],
            script.ReferencedObjects);
    }

    [Fact]
    public void DialogueResultScriptParser_BindsSctxToItsActiveBlock()
    {
        var data = BuildSubrecordStream(
            false,
            ("SCHR", new byte[20]),
            ("SCTX", Encoding.ASCII.GetBytes("first\0")),
            ("NEXT", []),
            ("SCHR", new byte[20]),
            ("SCTX", Encoding.ASCII.GetBytes("second\0")));

        var scripts = DialogueResultScriptParser.ParseResultScriptsFromSubrecords(
            data, data.Length, false, null, 0x01000001, _ => null, isDmpDerived: true);

        Assert.Equal(2, scripts.Count);
        Assert.Equal("first", scripts[0].SourceText);
        Assert.Equal("second", scripts[1].SourceText);
        Assert.True(scripts[0].HasNextSeparator);
    }

    [Fact]
    public void DialogueResultScriptParser_DoesNotLendPrecedingSourceOnlyBlockToScda()
    {
        byte[] compiled = [0x1D, 0x00, 0x00, 0x00];
        var schr = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(schr.AsSpan(8), (uint)compiled.Length);
        var data = BuildSubrecordStream(
            false,
            ("SCTX", Encoding.ASCII.GetBytes("orphan source\0")),
            ("SCHR", schr),
            ("SCDA", compiled));

        var scripts = DialogueResultScriptParser.ParseResultScriptsFromSubrecords(
            data, data.Length, false, null, 0x01000002, _ => null, isDmpDerived: true);

        Assert.Equal(2, scripts.Count);
        Assert.Equal("orphan source", scripts[0].SourceText);
        Assert.Null(scripts[1].SourceText);
        Assert.Equal(compiled, scripts[1].CompiledData);
    }

    [Fact]
    public void DialogueResultScriptParser_RepeatedSctxMarksBundleIncomplete()
    {
        var data = BuildSubrecordStream(
            false,
            ("SCHR", new byte[20]),
            ("SCTX", Encoding.ASCII.GetBytes("first\0")),
            ("SCTX", Encoding.ASCII.GetBytes("second\0")));

        var script = Assert.Single(DialogueResultScriptParser.ParseResultScriptsFromSubrecords(
            data, data.Length, false, null, 0x01000003, _ => null, isDmpDerived: true));

        Assert.True(script.IsIncompleteExecutableBundle);
        Assert.Equal("first", script.SourceText);
    }

    [Fact]
    public void DialogueResultScriptParser_MergeKeepsScdaLocalsAndReferenceTableAtomic()
    {
        var primary = new DialogueResultScript
        {
            SourceText = "set Quest.GameScore to Quest.GameScore + 1",
            CompiledData = [0x01, 0x02],
            Variables = [new ScriptVariableInfo(7, "localState", 1)],
            ReferencedObjects = [0x80000007, 0x00001234, 0x00001234],
            IsBigEndianBytecode = true
        };
        var secondary = new DialogueResultScript
        {
            SourceText = "different fragment",
            CompiledData = [0xAA],
            Variables = [new ScriptVariableInfo(9, "other", 0)],
            ReferencedObjects = [0x00005678, 0x80000009],
            HasNextSeparator = true
        };

        var merged = Assert.Single(DialogueResultScriptParser.MergeResultScripts(
            [primary],
            [secondary]));

        Assert.Equal(primary.SourceText, merged.SourceText);
        Assert.Equal(primary.CompiledData, merged.CompiledData);
        Assert.Equal(primary.Variables, merged.Variables);
        Assert.Equal(primary.ReferencedObjects, merged.ReferencedObjects);
        Assert.True(merged.IsBigEndianBytecode);
        Assert.True(merged.HasNextSeparator);
    }

    [Fact]
    public void DialogueResultScriptParser_MergeNeverBorrowsSourceOnlySiblingByOrdinal()
    {
        var compiled = new DialogueResultScript
        {
            CompiledData = [0x01, 0x02],
            Variables = [new ScriptVariableInfo(7, "localState", 1)],
            ReferencedObjects = [0x00001234]
        };
        var sourceOnly = new DialogueResultScript
        {
            SourceText = "set localState to 1"
        };

        var merged = Assert.Single(DialogueResultScriptParser.MergeResultScripts(
            [compiled],
            [sourceOnly]));

        Assert.Null(merged.SourceText);
        Assert.Equal(compiled.CompiledData, merged.CompiledData);
        Assert.Equal(compiled.Variables, merged.Variables);
        Assert.Equal(compiled.ReferencedObjects, merged.ReferencedObjects);
    }

    [Fact]
    public void DialogueResultScriptParser_MergeDoesNotAttachSourceFromDifferentCompiledBundle()
    {
        var selected = new DialogueResultScript
        {
            CompiledData = [0x01, 0x02],
            ReferencedObjects = [0x00001234]
        };
        var conflicting = new DialogueResultScript
        {
            SourceText = "belongs to the other bytecode",
            CompiledData = [0xAA],
            Variables = [new ScriptVariableInfo(9, "other", 0)],
            ReferencedObjects = [0x00005678]
        };

        var merged = Assert.Single(DialogueResultScriptParser.MergeResultScripts(
            [selected],
            [conflicting]));

        Assert.Null(merged.SourceText);
        Assert.Equal(selected.CompiledData, merged.CompiledData);
        Assert.Equal(selected.ReferencedObjects, merged.ReferencedObjects);
    }

    [Fact]
    public void DialogueResultScriptParser_MergePropagatesIncompleteExecutableBundle()
    {
        var compiled = new DialogueResultScript
        {
            CompiledData = [0x01, 0x02]
        };
        var incompleteSibling = new DialogueResultScript
        {
            SourceText = "set localState to 1",
            IsIncompleteExecutableBundle = true
        };

        var merged = Assert.Single(DialogueResultScriptParser.MergeResultScripts(
            [compiled],
            [incompleteSibling]));

        Assert.True(merged.IsIncompleteExecutableBundle);
    }

    [Fact]
    public void InfoEncoder_EncodeNew_ResultScriptEmitsVariableTableAndMixedReferencesInOrder()
    {
        var info = new DialogueRecord
        {
            FormId = 0x01003FED,
            ResultScripts =
            [
                new DialogueResultScript
                {
                    CompiledData = [0x1D, 0x00, 0x00, 0x00],
                    Variables = [new ScriptVariableInfo(7, "GameScore", 1)],
                    ReferencedObjects = [0x80000007, 0x00001234, 0x00001234]
                }
            ]
        };

        var encoded = InfoEncoder.EncodeNew(info);
        var firstSchr = encoded.Subrecords.First(subrecord => subrecord.Signature == "SCHR");
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(firstSchr.Bytes.AsSpan(12, 4)));
        var scriptSubrecords = encoded.Subrecords
            .SkipWhile(subrecord => subrecord.Signature != "SCHR")
            .TakeWhile(subrecord => subrecord.Signature != "NEXT")
            .ToList();
        Assert.Equal(
            ["SCHR", "SCDA", "SLSD", "SCVR", "SCRV", "SCRO", "SCRO"],
            scriptSubrecords.Select(subrecord => subrecord.Signature));
        Assert.Equal(
            7u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                Assert.Single(scriptSubrecords, subrecord => subrecord.Signature == "SLSD").Bytes));
        Assert.Equal(2, scriptSubrecords.Count(subrecord => subrecord.Signature == "SCRO"));
    }

    [Theory]
    [InlineData(1u, 0, 0)]
    [InlineData(0u, 1, 1)]
    public void EsmScriptBlockReader_ReadsScriptLocalTypeAtOffset16(
        uint decoyValueAtOffset12,
        byte isIntegerAtOffset16,
        byte expectedType)
    {
        var slsd = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(slsd, 9);
        BinaryPrimitives.WriteUInt32LittleEndian(slsd.AsSpan(12), decoyValueAtOffset12);
        slsd[16] = isIntegerAtOffset16;
        var subrecords = new List<ParsedSubrecord>
        {
            new() { Signature = "SLSD", Data = slsd },
            new() { Signature = "SCVR", Data = Encoding.ASCII.GetBytes("state\0") }
        };

        var variable = Assert.Single(EsmScriptBlockReader.ReadScriptVariables(
            subrecords, 0, subrecords.Count));
        Assert.Equal(9u, variable.Index);
        Assert.Equal("state", variable.Name);
        Assert.Equal(expectedType, variable.Type);
    }

    [Fact]
    public void InfoEncoder_EncodeNew_OmitsEdidWhenAbsent()
    {
        var info = new DialogueRecord { FormId = 0x901 };
        var encoded = InfoEncoder.EncodeNew(info);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "EDID");
    }

    // ====================================================================================
    // QUST — Quest
    // ====================================================================================

    [Fact]
    public void QustEncoder_EncodeNew_DataIs8BytesWithFlagsAndDelay()
    {
        var quest = new QuestRecord
        {
            FormId = 0xA00,
            EditorId = "Q1",
            Flags = 0x05,
            Priority = 75,
            QuestDelay = 2.5f
        };

        var encoded = QustEncoder.EncodeNew(quest);

        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");
        Assert.Equal(8, data.Bytes.Length);
        Assert.Equal(0x05, data.Bytes[0]); // Flags
        Assert.Equal(75, data.Bytes[1]); // Priority
        Assert.Equal(0, data.Bytes[2]); // pad
        Assert.Equal(0, data.Bytes[3]); // pad
        Assert.Equal(2.5f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(4, 4)));
    }

    [Fact]
    public void QustEncoder_EncodeNew_StagesEmittedAsIndxQsdtCnamBlocks()
    {
        var quest = new QuestRecord
        {
            FormId = 0xA00,
            EditorId = "Q1",
            Stages =
            {
                new QuestStage { Index = 10, LogEntry = "Started.", Flags = 0x01 },
                new QuestStage { Index = 100, LogEntry = "Completed.", Flags = 0x02 }
            }
        };

        var encoded = QustEncoder.EncodeNew(quest);

        // Order: DATA, INDX#1, QSDT#1, CNAM#1, INDX#2, QSDT#2, CNAM#2.
        var sigOrder = encoded.Subrecords.Select(s => s.Signature).ToList();
        var dataIdx = sigOrder.IndexOf("DATA");
        Assert.True(sigOrder[dataIdx + 1] == "INDX");
        Assert.True(sigOrder[dataIdx + 2] == "QSDT");
        Assert.True(sigOrder[dataIdx + 3] == "CNAM");

        var indxRecords = encoded.Subrecords.Where(s => s.Signature == "INDX").ToList();
        Assert.Equal(2, indxRecords.Count);
        Assert.Equal(10, BinaryPrimitives.ReadInt16LittleEndian(indxRecords[0].Bytes));
        Assert.Equal(100, BinaryPrimitives.ReadInt16LittleEndian(indxRecords[1].Bytes));

        var qsdtRecords = encoded.Subrecords.Where(s => s.Signature == "QSDT").ToList();
        Assert.Equal(2, qsdtRecords.Count);
        Assert.Equal(0x01, qsdtRecords[0].Bytes[0]);
        Assert.Equal(0x02, qsdtRecords[1].Bytes[0]);
    }

    [Fact]
    public void QustEncoder_EncodeNew_ObjectivesEmittedAsQobjNnamPairs()
    {
        var quest = new QuestRecord
        {
            FormId = 0xA00,
            EditorId = "Q1",
            Objectives =
            {
                new QuestObjective { Index = 10, DisplayText = "Find the artifact." },
                new QuestObjective { Index = 20, DisplayText = "Return to base." }
            }
        };

        var encoded = QustEncoder.EncodeNew(quest);

        var qobj = encoded.Subrecords.Where(s => s.Signature == "QOBJ").ToList();
        var nnam = encoded.Subrecords.Where(s => s.Signature == "NNAM").ToList();

        Assert.Equal(2, qobj.Count);
        Assert.Equal(2, nnam.Count);
        Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(qobj[0].Bytes));
        Assert.Equal(20, BinaryPrimitives.ReadInt32LittleEndian(qobj[1].Bytes));
    }

    [Fact]
    public void QustEncoder_EncodeNew_ScriptEmittedAsScriBeforeFull()
    {
        var quest = new QuestRecord
        {
            FormId = 0xA00,
            EditorId = "Q1",
            FullName = "My Quest",
            Script = 0x12345
        };

        var encoded = QustEncoder.EncodeNew(quest);

        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        var scriIdx = sigs.IndexOf("SCRI");
        var fullIdx = sigs.IndexOf("FULL");
        Assert.True(scriIdx >= 0 && fullIdx >= 0);
        Assert.True(scriIdx < fullIdx, "SCRI must precede FULL per fopdoc canonical order.");

        var scri = encoded.Subrecords[scriIdx];
        Assert.Equal(0x12345u, BinaryPrimitives.ReadUInt32LittleEndian(scri.Bytes));
    }

    // ====================================================================================
    // PACK — AI Package
    // ====================================================================================

    [Fact]
    public void PackEncoder_EncodeNew_PkdtIs12BytesWithPdbLayout()
    {
        var pack = new PackageRecord
        {
            FormId = 0xB00,
            EditorId = "Pkg1",
            Data = new PackageData
            {
                Type = 5,
                GeneralFlags = 0x12345678,
                FalloutBehaviorFlags = 0xCAFE,
                TypeSpecificFlags = 0xBEEF
            }
        };

        var encoded = PackEncoder.EncodeNew(pack);

        var pkdt = Assert.Single(encoded.Subrecords, s => s.Signature == "PKDT");
        Assert.Equal(12, pkdt.Bytes.Length);
        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(pkdt.Bytes.AsSpan(0, 4)));
        Assert.Equal((byte)5, pkdt.Bytes[4]);
        Assert.Equal((ushort)0xCAFE, BinaryPrimitives.ReadUInt16LittleEndian(pkdt.Bytes.AsSpan(6, 2)));
        Assert.Equal((ushort)0xBEEF, BinaryPrimitives.ReadUInt16LittleEndian(pkdt.Bytes.AsSpan(8, 2)));
    }

    [Fact]
    public void PackEncoder_EncodeNew_PsdtIs8BytesWithSignedSchedule()
    {
        var pack = new PackageRecord
        {
            FormId = 0xB00,
            EditorId = "Pkg",
            Data = new PackageData { Type = 0 },
            Schedule = new PackageSchedule
            {
                Month = -1,
                DayOfWeek = 3,
                Date = 0,
                Time = 22,
                Duration = 8
            }
        };

        var encoded = PackEncoder.EncodeNew(pack);

        var psdt = Assert.Single(encoded.Subrecords, s => s.Signature == "PSDT");
        Assert.Equal(8, psdt.Bytes.Length);
        Assert.Equal((sbyte)-1, unchecked((sbyte)psdt.Bytes[0]));
        Assert.Equal((byte)3, psdt.Bytes[1]);
        Assert.Equal((byte)0, psdt.Bytes[2]);
        Assert.Equal((byte)22, psdt.Bytes[3]);
        Assert.Equal(8, BinaryPrimitives.ReadInt32LittleEndian(psdt.Bytes.AsSpan(4, 4)));
    }

    [Fact]
    public void PackEncoder_EncodeNew_TargetAndLocationLayouts()
    {
        var pack = new PackageRecord
        {
            FormId = 0xB00,
            EditorId = "Pkg",
            Data = new PackageData { Type = 0 },
            Target = new PackageTarget
            {
                Type = 1,
                FormIdOrType = 0xDEADBEEF,
                CountDistance = -7,
                AcquireRadius = 100.0f
            },
            Location = new PackageLocation
            {
                Type = 2,
                Union = 0x1234,
                Radius = 50
            }
        };

        var encoded = PackEncoder.EncodeNew(pack);

        var ptdt = Assert.Single(encoded.Subrecords, s => s.Signature == "PTDT");
        Assert.Equal(16, ptdt.Bytes.Length);
        Assert.Equal((byte)1, ptdt.Bytes[0]);
        Assert.Equal(0xDEADBEEFu, BinaryPrimitives.ReadUInt32LittleEndian(ptdt.Bytes.AsSpan(4, 4)));
        Assert.Equal(-7, BinaryPrimitives.ReadInt32LittleEndian(ptdt.Bytes.AsSpan(8, 4)));
        Assert.Equal(100.0f, BinaryPrimitives.ReadSingleLittleEndian(ptdt.Bytes.AsSpan(12, 4)));

        var pldt = Assert.Single(encoded.Subrecords, s => s.Signature == "PLDT");
        Assert.Equal(12, pldt.Bytes.Length);
        Assert.Equal((byte)2, pldt.Bytes[0]);
        Assert.Equal(0x1234u, BinaryPrimitives.ReadUInt32LittleEndian(pldt.Bytes.AsSpan(4, 4)));
        Assert.Equal(50, BinaryPrimitives.ReadInt32LittleEndian(pldt.Bytes.AsSpan(8, 4)));
    }

    [Fact]
    public void PackEncoder_EncodeNew_Pkw3WeaponDataLayout()
    {
        var pack = new PackageRecord
        {
            FormId = 0xB00,
            EditorId = "Pkg",
            Data = new PackageData { Type = 16 }, // UseWeapon
            UseWeaponData = new PackageUseWeaponData
            {
                AlwaysHit = true,
                DoNoDamage = false,
                Crouch = true,
                HoldFire = false,
                VolleyFire = true,
                RepeatFire = false,
                BurstCount = 3,
                VolleyShotsMin = 5,
                VolleyShotsMax = 10,
                VolleyWaitMin = 1.5f,
                VolleyWaitMax = 3.0f,
                WeaponFormId = 0xABCDEF
            }
        };

        var encoded = PackEncoder.EncodeNew(pack);

        var pkw3 = Assert.Single(encoded.Subrecords, s => s.Signature == "PKW3");
        Assert.Equal(24, pkw3.Bytes.Length);
        Assert.Equal(1, pkw3.Bytes[0]); // AlwaysHit
        Assert.Equal(0, pkw3.Bytes[1]); // DoNoDamage
        Assert.Equal(1, pkw3.Bytes[2]); // Crouch
        Assert.Equal(0, pkw3.Bytes[3]); // HoldFire
        Assert.Equal(1, pkw3.Bytes[4]); // VolleyFire
        Assert.Equal(0, pkw3.Bytes[5]); // RepeatFire
        Assert.Equal((ushort)3, BinaryPrimitives.ReadUInt16LittleEndian(pkw3.Bytes.AsSpan(6, 2)));
        Assert.Equal((ushort)5, BinaryPrimitives.ReadUInt16LittleEndian(pkw3.Bytes.AsSpan(8, 2)));
        Assert.Equal((ushort)10, BinaryPrimitives.ReadUInt16LittleEndian(pkw3.Bytes.AsSpan(10, 2)));
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(pkw3.Bytes.AsSpan(12, 4)));
        Assert.Equal(3.0f, BinaryPrimitives.ReadSingleLittleEndian(pkw3.Bytes.AsSpan(16, 4)));
        Assert.Equal(0xABCDEFu, BinaryPrimitives.ReadUInt32LittleEndian(pkw3.Bytes.AsSpan(20, 4)));
    }

    [Fact]
    public void PackEncoder_EncodeNew_PatrolPkptEmittedWhenFlagsSet()
    {
        var pack = new PackageRecord
        {
            FormId = 0xB00,
            EditorId = "Pkg",
            Data = new PackageData { Type = 13 },
            IsRepeatable = true,
            IsStartingLocationLinkedRef = false
        };

        var encoded = PackEncoder.EncodeNew(pack);

        var pkpt = Assert.Single(encoded.Subrecords, s => s.Signature == "PKPT");
        Assert.Equal(2, pkpt.Bytes.Length);
        Assert.Equal(1, pkpt.Bytes[0]);
        Assert.Equal(0, pkpt.Bytes[1]);
    }

    private static byte[] BuildSubrecordStream(bool bigEndianSizes, params (string Signature, byte[] Data)[] subrecords)
    {
        var bytes = new List<byte>();
        Span<byte> lengthBytes = stackalloc byte[2];
        foreach (var (signature, data) in subrecords)
        {
            var signatureBytes = Encoding.ASCII.GetBytes(signature);
            if (bigEndianSizes)
            {
                Array.Reverse(signatureBytes);
            }

            bytes.AddRange(signatureBytes);
            if (bigEndianSizes)
            {
                BinaryPrimitives.WriteUInt16BigEndian(lengthBytes, (ushort)data.Length);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(lengthBytes, (ushort)data.Length);
            }

            bytes.AddRange(lengthBytes.ToArray());
            bytes.AddRange(data);
        }

        return bytes.ToArray();
    }

    [Fact]
    public void PackEncoder_EncodeNew_EmitsBehaviorMarkers()
    {
        var pack = new PackageRecord
        {
            FormId = 0xB00,
            EditorId = "Pkg",
            Data = new PackageData { Type = 8 },
            HasEatMarker = true,
            HasUseItemMarker = true,
            HasAmbushMarker = true
        };

        var encoded = PackEncoder.EncodeNew(pack);

        var markerSubrecords = encoded.Subrecords
            .Where(s => s.Signature is "PKED" or "PUID" or "PKAM")
            .ToList();
        Assert.Equal(["PKED", "PUID", "PKAM"], markerSubrecords.Select(s => s.Signature));
        Assert.All(markerSubrecords, marker => Assert.Empty(marker.Bytes));
    }

    [Fact]
    public void PackEncoder_EncodeNew_NoPkdtData_WarnsAndEmitsZeroFilled()
    {
        var pack = new PackageRecord { FormId = 0xB00, EditorId = "Pkg" };
        var encoded = PackEncoder.EncodeNew(pack);

        var pkdt = Assert.Single(encoded.Subrecords, s => s.Signature == "PKDT");
        Assert.Equal(12, pkdt.Bytes.Length);
        Assert.All(pkdt.Bytes, b => Assert.Equal(0, b));
        Assert.NotEmpty(encoded.Warnings);
    }
}
