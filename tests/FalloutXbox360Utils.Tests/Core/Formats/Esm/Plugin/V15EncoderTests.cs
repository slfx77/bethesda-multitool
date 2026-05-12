using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Dialogue;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     v15 tests:
///     - QustEncoder per-stage CTDA + CIS1/CIS2 emission
///     - QustEncoder per-target CTDA emission via QSTA
///     - ArmaEncoder ETYP + REPL emission
/// </summary>
public class V15EncoderTests
{
    // ====================================================================================
    // QustEncoder per-stage conditions
    // ====================================================================================

    [Fact]
    public void QustEncoder_EncodeNew_StageCtdaBetweenQsdtAndCnam()
    {
        var quest = new QuestRecord
        {
            FormId = 0x2000,
            EditorId = "TestQuest",
            Stages =
            [
                new QuestStage
                {
                    Index = 10,
                    Flags = 0x01,
                    LogEntry = "Started",
                    Conditions =
                    [
                        new DialogueCondition { Type = 0x20, FunctionIndex = 76 }
                    ]
                }
            ]
        };

        var encoded = QustEncoder.EncodeNew(quest);
        var sigs = encoded.Subrecords
            .Where(s => s.Signature is "INDX" or "QSDT" or "CTDA" or "CNAM")
            .Select(s => s.Signature).ToList();

        Assert.Equal(["INDX", "QSDT", "CTDA", "CNAM"], sigs);
    }

    [Fact]
    public void QustEncoder_EncodeNew_StageCis1Cis2EmittedWithStageCtda()
    {
        var quest = new QuestRecord
        {
            FormId = 0x2000,
            EditorId = "T",
            Stages =
            [
                new QuestStage
                {
                    Index = 5,
                    Conditions =
                    [
                        new DialogueCondition
                        {
                            Type = 0x20,
                            FunctionIndex = 449,
                            Parameter1String = "stagevar",
                            Parameter2String = "stage2"
                        }
                    ]
                }
            ]
        };

        var encoded = QustEncoder.EncodeNew(quest);
        var sigs = encoded.Subrecords
            .Where(s => s.Signature is "INDX" or "CTDA" or "CIS1" or "CIS2")
            .Select(s => s.Signature).ToList();

        Assert.Equal(["INDX", "CTDA", "CIS1", "CIS2"], sigs);
    }

    // ====================================================================================
    // QustEncoder per-target conditions
    // ====================================================================================

    [Fact]
    public void QustEncoder_EncodeNew_ObjectiveTargetEmitsQstaThenCtda()
    {
        var quest = new QuestRecord
        {
            FormId = 0x2000,
            EditorId = "T",
            Objectives =
            [
                new QuestObjective
                {
                    Index = 100,
                    DisplayText = "Kill Benny",
                    Targets =
                    [
                        new QuestObjectiveTarget
                        {
                            TargetFormId = 0xDEAD,
                            Flags = 0x01,
                            Conditions =
                            [
                                new DialogueCondition { Type = 0x20, FunctionIndex = 1 }
                            ]
                        }
                    ]
                }
            ]
        };

        var encoded = QustEncoder.EncodeNew(quest);
        var sigs = encoded.Subrecords
            .Where(s => s.Signature is "QOBJ" or "NNAM" or "QSTA" or "CTDA")
            .Select(s => s.Signature).ToList();

        Assert.Equal(["QOBJ", "NNAM", "QSTA", "CTDA"], sigs);
    }

    [Fact]
    public void QustEncoder_EncodeNew_QstaLayout()
    {
        var quest = new QuestRecord
        {
            FormId = 0x2000,
            EditorId = "T",
            Objectives =
            [
                new QuestObjective
                {
                    Index = 0,
                    Targets =
                    [
                        new QuestObjectiveTarget { TargetFormId = 0xABCDEFu, Flags = 0x42 }
                    ]
                }
            ]
        };

        var encoded = QustEncoder.EncodeNew(quest);
        var qsta = Assert.Single(encoded.Subrecords, s => s.Signature == "QSTA").Bytes;

        Assert.Equal(8, qsta.Length);
        Assert.Equal(0xABCDEFu, BinaryPrimitives.ReadUInt32LittleEndian(qsta.AsSpan(0, 4)));
        Assert.Equal(0x42, qsta[4]);
        Assert.Equal(0, qsta[5]);
        Assert.Equal(0, qsta[6]);
        Assert.Equal(0, qsta[7]);
    }

    [Fact]
    public void QustEncoder_EncodeNew_MultipleTargetsEachWithOwnConditions()
    {
        var quest = new QuestRecord
        {
            FormId = 0x2000,
            EditorId = "T",
            Objectives =
            [
                new QuestObjective
                {
                    Index = 0,
                    Targets =
                    [
                        new QuestObjectiveTarget
                        {
                            TargetFormId = 0xA,
                            Conditions = [new DialogueCondition { Type = 0x20, FunctionIndex = 1 }]
                        },
                        new QuestObjectiveTarget
                        {
                            TargetFormId = 0xB,
                            Conditions = [new DialogueCondition { Type = 0x20, FunctionIndex = 2 }]
                        }
                    ]
                }
            ]
        };

        var encoded = QustEncoder.EncodeNew(quest);
        var sigs = encoded.Subrecords
            .Where(s => s.Signature is "QSTA" or "CTDA")
            .Select(s => s.Signature).ToList();

        Assert.Equal(["QSTA", "CTDA", "QSTA", "CTDA"], sigs);
    }

    [Fact]
    public void QustEncoder_EncodeNew_AllConditionScopesCoexist()
    {
        // Top-level + per-stage + per-target conditions all in one quest.
        var quest = new QuestRecord
        {
            FormId = 0x2000,
            EditorId = "T",
            Conditions = [new DialogueCondition { Type = 0x20, FunctionIndex = 100 }],
            Stages =
            [
                new QuestStage
                {
                    Index = 10,
                    Conditions = [new DialogueCondition { Type = 0x20, FunctionIndex = 200 }]
                }
            ],
            Objectives =
            [
                new QuestObjective
                {
                    Index = 0,
                    Targets =
                    [
                        new QuestObjectiveTarget
                        {
                            TargetFormId = 0xC,
                            Conditions = [new DialogueCondition { Type = 0x20, FunctionIndex = 300 }]
                        }
                    ]
                }
            ]
        };

        var encoded = QustEncoder.EncodeNew(quest);
        Assert.Equal(3, encoded.Subrecords.Count(s => s.Signature == "CTDA"));

        // Verify function indices appear in scope order: top-level, stage, target.
        var ctdas = encoded.Subrecords.Where(s => s.Signature == "CTDA").ToList();
        Assert.Equal(100, BinaryPrimitives.ReadUInt16LittleEndian(ctdas[0].Bytes.AsSpan(8, 2)));
        Assert.Equal(200, BinaryPrimitives.ReadUInt16LittleEndian(ctdas[1].Bytes.AsSpan(8, 2)));
        Assert.Equal(300, BinaryPrimitives.ReadUInt16LittleEndian(ctdas[2].Bytes.AsSpan(8, 2)));
    }

    // ====================================================================================
    // ArmaEncoder ETYP + REPL
    // ====================================================================================

    [Fact]
    public void ArmaEncoder_EncodeNew_EmitsEtypAsInt32()
    {
        var arma = new ArmaRecord
        {
            FormId = 0xF00,
            EditorId = "T",
            EquipmentType = EquipmentType.BodyWear
        };

        var encoded = ArmaEncoder.EncodeNew(arma);
        var etyp = Assert.Single(encoded.Subrecords, s => s.Signature == "ETYP").Bytes;

        Assert.Equal(4, etyp.Length);
        Assert.Equal((int)EquipmentType.BodyWear, BinaryPrimitives.ReadInt32LittleEndian(etyp));
    }

    [Fact]
    public void ArmaEncoder_EncodeNew_OmitsEtypWhenNone()
    {
        var arma = new ArmaRecord
        {
            FormId = 0xF00,
            EditorId = "T",
            EquipmentType = EquipmentType.None
        };

        var encoded = ArmaEncoder.EncodeNew(arma);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "ETYP");
    }

    [Fact]
    public void ArmaEncoder_EncodeNew_EmitsReplAsFormId()
    {
        var arma = new ArmaRecord
        {
            FormId = 0xF00,
            EditorId = "T",
            RepairItemListFormId = 0x12345u
        };

        var encoded = ArmaEncoder.EncodeNew(arma);
        var repl = Assert.Single(encoded.Subrecords, s => s.Signature == "REPL").Bytes;

        Assert.Equal(4, repl.Length);
        Assert.Equal(0x12345u, BinaryPrimitives.ReadUInt32LittleEndian(repl));
    }

    [Fact]
    public void ArmaEncoder_EncodeNew_OmitsReplWhenNull()
    {
        var arma = new ArmaRecord { FormId = 0xF00, EditorId = "T", RepairItemListFormId = null };
        var encoded = ArmaEncoder.EncodeNew(arma);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "REPL");
    }

    [Fact]
    public void ArmaEncoder_EncodeNew_EtypAndReplFollowDnam()
    {
        var arma = new ArmaRecord
        {
            FormId = 0xF00,
            EditorId = "T",
            DetectionSoundLevel = 2,
            EquipmentType = EquipmentType.HeadWear,
            RepairItemListFormId = 0x100
        };

        var encoded = ArmaEncoder.EncodeNew(arma);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        var dataIdx = sigs.IndexOf("DATA");
        var dnamIdx = sigs.IndexOf("DNAM");
        var etypIdx = sigs.IndexOf("ETYP");
        var replIdx = sigs.IndexOf("REPL");

        Assert.True(dataIdx < dnamIdx);
        Assert.True(dnamIdx < etypIdx);
        Assert.True(etypIdx < replIdx);
    }
}
