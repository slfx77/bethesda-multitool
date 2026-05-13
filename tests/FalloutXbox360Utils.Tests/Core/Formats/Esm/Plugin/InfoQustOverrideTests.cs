using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Dialogue;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     v22: INFO and QUST override encoders now emit the full canonical subrecord stream
///     when the DMP captured any meaningful content (responses / stages / conditions /
///     etc.). When the DMP carried nothing the override returns empty subrecords and the
///     merge engine retains the master's record verbatim.
///
///     Partial emission would be unsafe: positional per-signature merge would desynchronize
///     paired subrecords (TRDT+NAM1 per response, INDX+QSDT+CNAM per stage). These tests
///     pin the all-or-nothing contract.
/// </summary>
public class InfoQustOverrideTests
{
    // ---- INFO ------------------------------------------------------------------

    [Fact]
    public void InfoEncoder_Override_NoCapturedContent_ReturnsEmpty()
    {
        // A bare DialogueRecord (no responses, conditions, scripts, etc.) signals the merge
        // engine to retain the master's INFO verbatim — override encoder emits nothing.
        var info = new DialogueRecord { FormId = 0x000ABCDE };

        var encoded = new InfoEncoder().Encode(info);

        Assert.Empty(encoded.Subrecords);
    }

    [Fact]
    public void InfoEncoder_Override_WithResponses_EmitsCanonicalStream_NoEdid_NoPlaceholder()
    {
        // DMP captured at least one response → full canonical subrecord stream replaces master.
        // EDID is intentionally omitted on the override path (master keeps its identity).
        // Empty-responses placeholder ("(NOT FOUND IN CRASH DUMP)") is also omitted —
        // HasOverrideContent gates the codepath so we won't see Responses == 0 here.
        var info = new DialogueRecord
        {
            FormId = 0x000ABCDE,
            EditorId = "DUMMY_EDID",
            Responses = [new DialogueResponse { Text = "Prototype line.", ResponseNumber = 1 }]
        };

        var encoded = new InfoEncoder().Encode(info);

        Assert.NotEmpty(encoded.Subrecords);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "EDID");
        Assert.Contains(encoded.Subrecords, s => s.Signature == "DATA");

        var nam1 = Assert.Single(encoded.Subrecords, s => s.Signature == "NAM1");
        // Latin1 null-terminated string: trim the trailing \0 before comparing.
        var text = System.Text.Encoding.Latin1.GetString(nam1.Bytes).TrimEnd('\0');
        Assert.Equal("Prototype line.", text);

        Assert.DoesNotContain(encoded.Subrecords,
            s => s.Signature == "NAM1"
                 && System.Text.Encoding.Latin1.GetString(s.Bytes).Contains("NOT FOUND IN CRASH DUMP"));
    }

    [Fact]
    public void InfoEncoder_Override_WithConditionsOnly_FallsThroughToEmpty()
    {
        // Conditions alone do NOT trigger override emit on the conservative override path.
        // CTDA is a multi-occurrence chain — positional partial replacement would break the
        // master's AND/OR boolean logic — so the override skips it entirely. The DMP-only
        // override of an INFO with no response/prompt/etc. content falls through to empty,
        // and the merge engine retains the master's INFO verbatim.
        var info = new DialogueRecord
        {
            FormId = 0x000ABCDE,
            Conditions = [new DialogueCondition { Type = 0, FunctionIndex = 10 }]
        };

        var encoded = new InfoEncoder().Encode(info);

        Assert.Empty(encoded.Subrecords);
    }

    [Fact]
    public void InfoEncoder_Override_PromptOnly_EmitsCanonicalStream()
    {
        var info = new DialogueRecord
        {
            FormId = 0x000ABCDE,
            PromptText = "Tell me about the prototype"
        };

        var encoded = new InfoEncoder().Encode(info);

        Assert.Contains(encoded.Subrecords, s => s.Signature == "RNAM");
    }

    [Fact]
    public void InfoEncoder_New_EmptyResponses_StillEmitsPlaceholder()
    {
        // The new-record path must NOT short-circuit on empty responses — fresh INFOs need
        // at least one TRDT/NAM1 so the runtime doesn't crash iterating an empty list.
        var info = new DialogueRecord { FormId = 0x12345678, EditorId = "NewInfo" };

        var encoded = InfoEncoder.EncodeNew(info);

        Assert.Contains(encoded.Subrecords, s => s.Signature == "EDID");
        Assert.Contains(encoded.Subrecords, s => s.Signature == "TRDT");
        Assert.Contains(encoded.Subrecords,
            s => s.Signature == "NAM1"
                 && System.Text.Encoding.Latin1.GetString(s.Bytes).Contains("NOT FOUND IN CRASH DUMP"));
    }

    // ---- QUST ------------------------------------------------------------------

    [Fact]
    public void QustEncoder_Override_NoCapturedContent_ReturnsEmpty()
    {
        var quest = new QuestRecord { FormId = 0x000F1234 };

        var encoded = new QustEncoder().Encode(quest);

        Assert.Empty(encoded.Subrecords);
    }

    [Fact]
    public void QustEncoder_Override_WithStagesOnly_FallsThroughToEmpty()
    {
        // Stages alone do NOT trigger the override emit. INDX + QSDT + CNAM are positional
        // triplets the merge engine can't safely interleave with the master's stages, so the
        // override skips them entirely. Without FULL/SCRI, the override falls through to
        // empty and the master quest is retained verbatim.
        var quest = new QuestRecord
        {
            FormId = 0x000F1234,
            EditorId = "DUMMY_EDID",
            Stages =
            [
                new QuestStage { Index = 10, LogEntry = "Stage 10 log." },
                new QuestStage { Index = 20, LogEntry = "Stage 20 log." }
            ]
        };

        var encoded = new QustEncoder().Encode(quest);

        Assert.Empty(encoded.Subrecords);
    }

    [Fact]
    public void QustEncoder_Override_FullNameOnly_EmitsCanonicalStream()
    {
        // A captured FULL alone (prototype-era display name rewrite) should round-trip
        // through the override path.
        var quest = new QuestRecord { FormId = 0x000F1234, FullName = "Prototype Quest Title" };

        var encoded = new QustEncoder().Encode(quest);

        var full = Assert.Single(encoded.Subrecords, s => s.Signature == "FULL");
        var name = System.Text.Encoding.Latin1.GetString(full.Bytes).TrimEnd('\0');
        Assert.Equal("Prototype Quest Title", name);
    }

    [Fact]
    public void QustEncoder_New_EmitsEdid()
    {
        // The new-record path keeps emitting EDID (overrides do not).
        var quest = new QuestRecord { FormId = 0x12345678, EditorId = "NewQuest" };

        var encoded = QustEncoder.EncodeNew(quest);

        Assert.Contains(encoded.Subrecords, s => s.Signature == "EDID");
    }
}
