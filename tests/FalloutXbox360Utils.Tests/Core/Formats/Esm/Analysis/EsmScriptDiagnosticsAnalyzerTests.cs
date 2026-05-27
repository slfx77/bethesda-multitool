using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Dialogue;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Analysis;

public sealed class EsmScriptDiagnosticsAnalyzerTests
{
    [Fact]
    public void AnalyzeRecords_DumpsTargetDialogueScriptRefsAndFollowUpFields()
    {
        const uint ulysses = 0x00112233;
        const uint infoId = 0xFE000100;
        const uint topicId = 0xFE000200;
        const uint questId = 0xFE000300;
        const uint validRef = 0xFE000400;

        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "generated.esp",
            [
                Record("NPC_", ulysses,
                    StringSub("EDID", "UlyssesNPC"),
                    StringSub("FULL", "Ulysses")),
                Record("DIAL", topicId,
                    StringSub("EDID", "VFreeformUlyssesRoot"),
                    FormIdSubrecord("TNAM", ulysses)),
                Record("QUST", questId, StringSub("EDID", "VFreeformUlysses")),
                Record("GLOB", validRef, StringSub("EDID", "UlyssesFollowerState")),
                Record("INFO", infoId,
                    FormIdSubrecord("TPIC", topicId),
                    FormIdSubrecord("QSTI", questId),
                    FormIdSubrecord("PNAM", 0xFE000099),
                    FormIdSubrecord("TCLT", topicId),
                    FormIdSubrecord("TCFU", 0xFE000101),
                    CtdaGetIsId(ulysses),
                    ScriptHeader(refCount: 1, compiledSize: 4),
                    Sub("SCDA", 0xFF, 0xFF, 0x00, 0x00),
                    StringSub("SCTX", "set UlyssesFollowerState to 1"),
                    FormIdSubrecord("SCRO", validRef),
                    Sub("NEXT"),
                    Sub("TRDT", new byte[24]),
                    StringSub("NAM1", "Travel with me."))
            ],
            ["Ulysses"]);

        Assert.Contains(result.TargetMatches, row => row.FormId == ulysses && row.MatchReason == "actor-label");
        var dialogue = Assert.Single(result.Dialogue, row => row.InfoFormId == infoId);
        Assert.Equal(topicId, dialogue.TopicFormId);
        Assert.Equal(questId, dialogue.QuestFormId);
        Assert.Equal(ulysses, dialogue.SpeakerFormId);
        Assert.Contains("0xFE000101", dialogue.FollowUpInfos, StringComparison.Ordinal);
        Assert.True(dialogue.HasResultScript);

        var script = Assert.Single(result.ScriptBlocks, row => row.FormId == infoId);
        Assert.True(script.CompiledSizeMatches);
        Assert.True(script.RefCountMatches);
        Assert.True(script.WalkedToEnd);
        Assert.Equal("canonical", script.OrderStatus);
        Assert.Contains("UlyssesFollowerState", script.SourceTextPreview, StringComparison.Ordinal);

        var reference = Assert.Single(result.ScriptReferences, row => row.ParentFormId == infoId);
        Assert.Equal("SCRO", reference.ReferenceKind);
        Assert.Equal("Resolved", reference.Status);
        Assert.Equal(validRef, reference.ResolvedFormId);
        Assert.Equal("UlyssesFollowerState", reference.ResolvedEditorId);
    }

    [Fact]
    public void AnalyzeRecords_FlagsNullAndMissingScroRefs()
    {
        const uint actorId = 0x00112233;
        const uint infoId = 0xFE000100;

        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "generated.esp",
            [
                Record("NPC_", actorId,
                    StringSub("EDID", "UlyssesNPC"),
                    StringSub("FULL", "Ulysses")),
                Record("INFO", infoId,
                    CtdaGetIsId(actorId),
                    ScriptHeader(refCount: 2, compiledSize: 4),
                    Sub("SCDA", 0xFF, 0xFF, 0x00, 0x00),
                    FormIdSubrecord("SCRO", 0),
                    FormIdSubrecord("SCRO", 0xFE00DEAD))
            ],
            ["Ulysses"]);

        Assert.Contains(result.ScriptReferences, row => row.Status == "Null");
        Assert.Contains(result.ScriptReferences, row => row.Status == "Missing");
    }

    [Fact]
    public void AnalyzeRecords_ReportsActorPackagesAndPackageConditions()
    {
        const uint chomps = 0x00100020;
        const uint pack = 0xFE000220;
        const uint targetRef = 0xFE000330;

        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "generated.esp",
            [
                Record("NPC_", chomps,
                    StringSub("EDID", "ChompsLewis"),
                    StringSub("FULL", "Chomps Lewis"),
                    FormIdSubrecord("PKID", pack)),
                Record("REFR", targetRef,
                    StringSub("EDID", "ChompsMarker")),
                Record("PACK", pack,
                    StringSub("EDID", "ChompsLewisFollowPackage"),
                    CtdaGetIsId(chomps),
                    FormIdSubrecord("PLDT", targetRef),
                    ScriptHeader(refCount: 1, compiledSize: 4),
                    Sub("SCDA", 0xFF, 0xFF, 0x00, 0x00),
                    FormIdSubrecord("SCRO", targetRef))
            ],
            ["Chomps Lewis"]);

        var packageRecord = Assert.Single(result.Records, row => row.RecordType == "PACK");
        Assert.Contains("actor-package", packageRecord.Relation, StringComparison.Ordinal);
        Assert.Contains("CTDA", packageRecord.InterestingSubrecords, StringComparison.Ordinal);
        Assert.Contains("PLDT", packageRecord.InterestingSubrecords, StringComparison.Ordinal);

        var packageScript = Assert.Single(result.ScriptBlocks, row => row.RecordType == "PACK");
        Assert.True(packageScript.CompiledSizeMatches);
        var packageRef = Assert.Single(result.ScriptReferences, row => row.ParentRecordType == "PACK");
        Assert.Equal("Resolved", packageRef.Status);
        Assert.Equal("ChompsMarker", packageRef.ResolvedEditorId);
    }

    [Fact]
    public void AnalyzeRecords_TargetNormalizationFindsChompsLewisTravelPackage()
    {
        const uint chomps = 0x00100020;
        const uint travelPackage = 0xFE000220;

        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "generated.esp",
            [
                Record("NPC_", chomps,
                    StringSub("EDID", "QJChompsLewis"),
                    StringSub("FULL", "Chomps Lewis")),
                Record("PACK", travelPackage,
                    StringSub("EDID", "QJChompsLewisTravelPackage"))
            ],
            ["Chomps Lewis"]);

        Assert.Contains(result.Records,
            row => row.RecordType == "PACK" &&
                   row.FormId == travelPackage &&
                   row.Relation.Contains("target-ref-pack", StringComparison.Ordinal));
    }

    [Fact]
    public void Provenance_FlagsNonzeroSourceScroEmittedAsNull()
    {
        const uint scriptFormId = 0xFE000100;
        const uint sourceRef = 0x00123456;

        var generated = GeneratedUlyssesScriptRecords(scriptFormId, FormIdSubrecord("SCRO", 0));
        var diagnostics = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords("generated.esp", generated, ["Ulysses"]);
        var provenance = EsmScriptProvenanceAnalyzer.AnalyzeRecords(
            generated,
            diagnostics,
            new RecordCollection
            {
                Scripts =
                [
                    new ScriptRecord
                    {
                        FormId = 0x00133FD7,
                        EditorId = "UlyssesScript",
                        ReferencedObjects = [sourceRef]
                    }
                ],
                FormIdToEditorId = new Dictionary<uint, string> { [sourceRef] = "FollowerSwitchAggressive" }
            },
            null);

        var row = Assert.Single(provenance.SourceVsEmittedRefs);
        Assert.Equal("ConverterNulledNonZeroSource", row.Classification);
        Assert.Equal(sourceRef, row.SourceRawValue);
        Assert.Equal(0u, row.EmittedRawValue);
    }

    [Fact]
    public void Provenance_ClassifiesTrueSourceNullScro()
    {
        const uint scriptFormId = 0xFE000100;

        var generated = GeneratedUlyssesScriptRecords(scriptFormId, FormIdSubrecord("SCRO", 0));
        var diagnostics = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords("generated.esp", generated, ["Ulysses"]);
        var provenance = EsmScriptProvenanceAnalyzer.AnalyzeRecords(
            generated,
            diagnostics,
            new RecordCollection
            {
                Scripts =
                [
                    new ScriptRecord
                    {
                        FormId = 0x00133FD7,
                        EditorId = "UlyssesScript",
                        ReferencedObjects = [0]
                    }
                ]
            },
            null);

        var row = Assert.Single(provenance.SourceVsEmittedRefs);
        Assert.Equal("SourceNull", row.Classification);
    }

    [Fact]
    public void Provenance_FlagsSourceScrvEmittedAsScro()
    {
        const uint scriptFormId = 0xFE000100;

        var generated = GeneratedUlyssesScriptRecords(scriptFormId, FormIdSubrecord("SCRO", 2));
        var diagnostics = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords("generated.esp", generated, ["Ulysses"]);
        var provenance = EsmScriptProvenanceAnalyzer.AnalyzeRecords(
            generated,
            diagnostics,
            new RecordCollection
            {
                Scripts =
                [
                    new ScriptRecord
                    {
                        FormId = 0x00133FD7,
                        EditorId = "UlyssesScript",
                        ReferencedObjects = [0x80000002]
                    }
                ]
            },
            null);

        var row = Assert.Single(provenance.SourceVsEmittedRefs);
        Assert.Equal("SourceScrvEmittedAsScro", row.Classification);
    }

    [Fact]
    public void Provenance_FlagsSourceResultScriptEmittedAsPlaceholderOnly()
    {
        const uint actorId = 0x00112233;
        const uint infoId = 0xFE000100;

        var generated =
            new[]
            {
                Record("NPC_", actorId,
                    StringSub("EDID", "Ulysses"),
                    StringSub("FULL", "Ulysses")),
                Record("INFO", infoId,
                    CtdaGetIsId(actorId),
                    ScriptHeader(refCount: 0, compiledSize: 0),
                    Sub("NEXT"),
                    ScriptHeader(refCount: 0, compiledSize: 0),
                    Sub("TRDT", new byte[24]),
                    StringSub("NAM1", "Travel with me."))
            };

        var diagnostics = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords("generated.esp", generated, ["Ulysses"]);
        var provenance = EsmScriptProvenanceAnalyzer.AnalyzeRecords(
            generated,
            diagnostics,
            new RecordCollection
            {
                Dialogues =
                [
                    new DialogueRecord
                    {
                        FormId = 0x0010ABCD,
                        SpeakerFormId = actorId,
                        Responses = [new DialogueResponse { Text = "Travel with me." }],
                        ResultScripts =
                        [
                            new DialogueResultScript
                            {
                                CompiledData = [0xFF, 0xFF, 0x00, 0x00],
                                ReferencedObjects = [0x000B16D0]
                            }
                        ],
                        HasResultScript = true
                    }
                ]
            },
            null);

        var row = Assert.Single(provenance.ResultScripts);
        Assert.Equal("EmittedPlaceholderOnly", row.Classification);
        Assert.Equal(0x0010ABCDu, row.SourceInfoFormId);
    }

    [Fact]
    public void Provenance_TreatsCanonicalEmptyInfoEndBlockAsPreserved()
    {
        const uint actorId = 0x00112233;
        const uint infoId = 0xFE000100;
        const uint sourceInfoId = 0x0010ABCD;
        const uint scriptRef = 0x000B16D0;
        byte[] bytecode = [0xFF, 0xFF, 0x00, 0x00];

        var generated =
            new[]
            {
                Record("NPC_", actorId,
                    StringSub("EDID", "Ulysses"),
                    StringSub("FULL", "Ulysses")),
                Record("INFO", infoId,
                    CtdaGetIsId(actorId),
                    ScriptHeader(refCount: 1, compiledSize: (uint)bytecode.Length),
                    Sub("SCDA", bytecode),
                    FormIdSubrecord("SCRO", scriptRef),
                    Sub("NEXT"),
                    ScriptHeader(refCount: 0, compiledSize: 0),
                    Sub("TRDT", new byte[24]),
                    StringSub("NAM1", "Travel with me."))
            };

        var diagnostics = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords("generated.esp", generated, ["Ulysses"]);
        var provenance = EsmScriptProvenanceAnalyzer.AnalyzeRecords(
            generated,
            diagnostics,
            new RecordCollection
            {
                Dialogues =
                [
                    new DialogueRecord
                    {
                        FormId = sourceInfoId,
                        SpeakerFormId = actorId,
                        Responses = [new DialogueResponse { Text = "Travel with me." }],
                        ResultScripts =
                        [
                            new DialogueResultScript
                            {
                                CompiledData = bytecode,
                                ReferencedObjects = [scriptRef]
                            }
                        ],
                        HasResultScript = true
                    }
                ]
            },
            null);

        var row = Assert.Single(provenance.ResultScripts);
        Assert.Equal("Preserved", row.Classification);
        Assert.All(provenance.SourceVsEmittedRefs, reference => Assert.Equal("Resolved", reference.Classification));
    }

    [Fact]
    public void Provenance_EndianProbeClassifiesBigEndianBytecodeNeedingSwap()
    {
        const uint actorId = 0x00112233;
        const uint infoId = 0xFE000100;

        var generated =
            new[]
            {
                Record("NPC_", actorId,
                    StringSub("EDID", "Ulysses"),
                    StringSub("FULL", "Ulysses")),
                Record("INFO", infoId,
                    CtdaGetIsId(actorId),
                    ScriptHeader(refCount: 0, compiledSize: 15),
                    Sub("SCDA",
                        0x00, 0x15, 0x00, 0x0B,
                        0x66,
                        0x00, 0x00,
                        0x00, 0x06,
                        0x20, 0x6E, 0x00, 0x00, 0x00, 0x01),
                    Sub("NEXT"),
                    Sub("TRDT", new byte[24]),
                    StringSub("NAM1", "Travel with me."))
            };

        var diagnostics = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords("generated.esp", generated, ["Ulysses"]);
        var provenance = EsmScriptProvenanceAnalyzer.AnalyzeRecords(generated, diagnostics, null, null);

        var row = Assert.Single(provenance.BytecodeEndianProbes, r => r.Origin == "Emitted");
        Assert.Equal("WouldSwapFix", row.Classification);
        Assert.Equal("0x1500", row.LittleEndianOpcode);
        Assert.Equal("0x0015", row.BigEndianOpcode);
    }

    private static ParsedMainRecord Record(string signature, uint formId, params ParsedSubrecord[] subrecords)
    {
        return new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = signature,
                FormId = formId,
                Version = 0x000F
            },
            Subrecords = [.. subrecords]
        };
    }

    private static ParsedSubrecord Sub(string signature, params byte[] data)
    {
        return new ParsedSubrecord
        {
            Signature = signature,
            Data = data
        };
    }

    private static ParsedSubrecord StringSub(string signature, string value)
    {
        var data = new byte[value.Length + 1];
        Encoding.Latin1.GetBytes(value, data);
        return Sub(signature, data);
    }

    private static ParsedSubrecord FormIdSubrecord(string signature, uint formId)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, formId);
        return Sub(signature, data);
    }

    private static ParsedSubrecord ScriptHeader(uint refCount, uint compiledSize)
    {
        var data = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), refCount);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), compiledSize);
        data[18] = 1;
        return Sub("SCHR", data);
    }

    private static ParsedSubrecord CtdaGetIsId(uint actorFormId)
    {
        var data = new byte[28];
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), 1.0f);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), 0x48);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), actorFormId);
        return Sub("CTDA", data);
    }

    private static ParsedMainRecord[] GeneratedUlyssesScriptRecords(uint scriptFormId, params ParsedSubrecord[] refs)
    {
        var subs = new List<ParsedSubrecord>
        {
            StringSub("EDID", "UlyssesScript"),
            ScriptHeader((uint)refs.Length, 0)
        };
        subs.AddRange(refs);

        return
        [
            Record("NPC_", 0xFE000010,
                StringSub("EDID", "Ulysses"),
                StringSub("FULL", "Ulysses"),
                FormIdSubrecord("SCRI", scriptFormId)),
            Record("SCPT", scriptFormId, [.. subs])
        ];
    }
}
