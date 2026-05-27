using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Dialogue;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.AI;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     v6 tests for the script + dialogue + quest + package encoders. Each test verifies a
///     specific subrecord byte layout against the PDB-confirmed schema definitions in
///     <see cref="FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema.SubrecordDialogueSchemas" />.
/// </summary>
public class ScriptDialogueEncoderTests
{
    // ====================================================================================
    // SCPT — Script
    // ====================================================================================

    [Fact]
    public void ScptEncoder_EncodeNew_EmitsEdidAndSchrInOrder()
    {
        var script = new ScriptRecord
        {
            FormId = 0x800,
            EditorId = "MyScript",
            VariableCount = 3,
            RefObjectCount = 2,
            CompiledSize = 16,
            LastVariableId = 3,
            IsQuestScript = true,
            IsMagicEffectScript = false,
            IsCompiled = true
        };

        var encoded = ScptEncoder.EncodeNew(script);

        Assert.Equal("EDID", encoded.Subrecords[0].Signature);
        Assert.Equal("SCHR", encoded.Subrecords[1].Signature);

        var schr = encoded.Subrecords[1].Bytes;
        Assert.Equal(20, schr.Length);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(schr.AsSpan(0, 4)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(schr.AsSpan(4, 4)));
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(schr.AsSpan(8, 4)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(schr.AsSpan(12, 4)));
        Assert.Equal(1, schr[16]); // IsQuestScript
        Assert.Equal(0, schr[17]); // IsMagicEffectScript
        Assert.Equal(1, schr[18]); // IsCompiled
        Assert.Equal(0, schr[19]); // padding
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
        // Latin-1 string + null terminator.
        Assert.Equal(script.SourceText.Length + 1, sctx.Bytes.Length);
        Assert.Equal(0, sctx.Bytes[^1]);
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
        Assert.Equal(1, data.Bytes[0]);    // TopicType
        Assert.Equal(0x02, data.Bytes[1]); // Flags
    }

    [Fact]
    public void DialEncoder_EncodeNew_AllOptionals_EmitsFullQstiPnamTnam()
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

        Assert.Contains(encoded.Subrecords, s => s.Signature == "FULL");
        var qsti = Assert.Single(encoded.Subrecords, s => s.Signature == "QSTI");
        Assert.Equal(0x100u, BinaryPrimitives.ReadUInt32LittleEndian(qsti.Bytes));

        var pnam = Assert.Single(encoded.Subrecords, s => s.Signature == "PNAM");
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(pnam.Bytes));

        var tnam = Assert.Single(encoded.Subrecords, s => s.Signature == "TNAM");
        Assert.Equal(0x200u, BinaryPrimitives.ReadUInt32LittleEndian(tnam.Bytes));
    }

    [Fact]
    public void DialEncoder_EncodeNew_OmitsOptionalsWhenAbsent()
    {
        var dial = new DialogTopicRecord { FormId = 0x900, EditorId = "Topic" };
        var encoded = DialEncoder.EncodeNew(dial);

        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "FULL");
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "QSTI");
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "PNAM");
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "TNAM");
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
        Assert.Equal(0, data.Bytes[0]);    // DialType — default
        Assert.Equal(0, data.Bytes[1]);    // NextSpeaker — default
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
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(trdt0.AsSpan(0, 4)));    // EmotionType
        Assert.Equal(50, BinaryPrimitives.ReadInt32LittleEndian(trdt0.AsSpan(4, 4)));     // EmotionValue
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(trdt0.AsSpan(8, 4)));    // ConvTopic (zero)
        Assert.Equal(1, trdt0[12]);                                                        // ResponseNumber
        Assert.Equal(0x0000BEEFu, BinaryPrimitives.ReadUInt32LittleEndian(trdt0.AsSpan(16, 4))); // Sound
        Assert.Equal(0, trdt0[20]);                                                        // UseEmotionAnim
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
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(schr.Bytes.AsSpan(0, 4)));  // VariableCount
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(schr.Bytes.AsSpan(4, 4)));  // RefObjectCount
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(schr.Bytes.AsSpan(8, 4)));  // CompiledSize
        Assert.Equal(1, schr.Bytes[18]); // IsCompiled — set because CompiledData has bytes

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

        var data = BuildSubrecordStream(
            bigEndianSizes: true,
            ("SCHR", new byte[20]),
            ("SCDA", littleEndianScda));

        var scripts = DialogueResultScriptParser.ParseResultScriptsFromSubrecords(
            data,
            data.Length,
            isBigEndian: true,
            editorId: null,
            formId: 0x01003FED,
            resolveFormName: _ => null);

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
        Assert.Equal(75, data.Bytes[1]);   // Priority
        Assert.Equal(0, data.Bytes[2]);    // pad
        Assert.Equal(0, data.Bytes[3]);    // pad
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
        foreach (var (signature, data) in subrecords)
        {
            var signatureBytes = System.Text.Encoding.ASCII.GetBytes(signature);
            if (bigEndianSizes)
            {
                Array.Reverse(signatureBytes);
            }

            bytes.AddRange(signatureBytes);
            Span<byte> lengthBytes = stackalloc byte[2];
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
