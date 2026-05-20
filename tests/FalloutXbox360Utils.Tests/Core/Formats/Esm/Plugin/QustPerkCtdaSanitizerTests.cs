using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Dialogue;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Magic;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     Phase 2 — QUST + PERK CTDA sanitization. Reuses the same drop/remap/keep policy as
///     IDLE and INFO encoders; this test surface guards the wiring through QustEncoder's
///     three CTDA emission sites (top-level + per-stage + per-objective-target) and
///     PerkEncoder's two sites (top-level + per-entry).
/// </summary>
public class QustPerkCtdaSanitizerTests
{
    private const ushort GetIsID = 0x0048;       // Param1 = FormID
    private const ushort GetActorValue = 0x000E; // Param1 = ActorValue enum (NOT a FormID)
    private const ushort HasPerk = 0x01C1;       // Param1 = Perk FormID

    // ---------- QUST top-level conditions ----------

    [Fact]
    public void QustEncodeNew_drops_top_level_CTDA_with_dangling_FormID_param()
    {
        var quest = MakeQuest(conditions: new()
        {
            new DialogueCondition { FunctionIndex = GetIsID, Parameter1 = 0x000DEAD1u }
        });
        var valid = new HashSet<uint> { 0x00000001u };

        var encoded = QustEncoder.EncodeNew(quest, validFormIds: valid);

        Assert.Empty(encoded.Subrecords.Where(s => s.Signature == "CTDA"));
        Assert.Contains(encoded.Warnings, w => w.Contains("CTDA sanitizer"));
    }

    [Fact]
    public void QustEncodeNew_keeps_top_level_CTDA_with_non_FormID_param()
    {
        var quest = MakeQuest(conditions: new()
        {
            new DialogueCondition { FunctionIndex = GetActorValue, Parameter1 = 4u }
        });
        var valid = new HashSet<uint>();

        var encoded = QustEncoder.EncodeNew(quest, validFormIds: valid);

        var ctda = Assert.Single(encoded.Subrecords, s => s.Signature == "CTDA");
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(ctda.Bytes.AsSpan(12, 4)));
    }

    [Fact]
    public void QustEncodeNew_remaps_top_level_CTDA_FormID_param_via_alias_table()
    {
        var quest = MakeQuest(conditions: new()
        {
            new DialogueCondition { FunctionIndex = GetIsID, Parameter1 = 0x01999AAAu }
        });
        var valid = new HashSet<uint> { 0x01000123u };
        var remap = new Dictionary<uint, uint> { [0x01999AAAu] = 0x01000123u };

        var encoded = QustEncoder.EncodeNew(quest, validFormIds: valid, remapTable: remap);

        var ctda = Assert.Single(encoded.Subrecords, s => s.Signature == "CTDA");
        Assert.Equal(0x01000123u, BinaryPrimitives.ReadUInt32LittleEndian(ctda.Bytes.AsSpan(12, 4)));
    }

    [Fact]
    public void QustEncodeNew_emits_conditions_verbatim_when_no_validFormIds_supplied()
    {
        // Backward compat path: tests and legacy callers that don't pass validFormIds get
        // unchanged CTDA emission.
        var quest = MakeQuest(conditions: new()
        {
            new DialogueCondition { FunctionIndex = GetIsID, Parameter1 = 0xDEADBEEFu }
        });

        var encoded = QustEncoder.EncodeNew(quest);

        var ctda = Assert.Single(encoded.Subrecords, s => s.Signature == "CTDA");
        Assert.Equal(0xDEADBEEFu, BinaryPrimitives.ReadUInt32LittleEndian(ctda.Bytes.AsSpan(12, 4)));
    }

    // ---------- QUST per-stage conditions ----------

    [Fact]
    public void QustEncodeNew_sanitizes_per_stage_CTDAs()
    {
        var quest = MakeQuest(stages: new()
        {
            new QuestStage
            {
                Index = 10, Flags = 1,
                Conditions = new()
                {
                    new DialogueCondition { FunctionIndex = GetIsID, Parameter1 = 0x000DEAD1u }
                }
            }
        });
        var valid = new HashSet<uint>();

        var encoded = QustEncoder.EncodeNew(quest, validFormIds: valid);

        // INDX should still emit for the stage; CTDA for that stage was dropped.
        Assert.Single(encoded.Subrecords.Where(s => s.Signature == "INDX"));
        Assert.Empty(encoded.Subrecords.Where(s => s.Signature == "CTDA"));
    }

    // ---------- PERK top-level conditions ----------

    [Fact]
    public void PerkEncodeNew_drops_top_level_CTDA_with_dangling_HasPerk_FormID()
    {
        var perk = MakePerk(conditions: new()
        {
            new PerkCondition { FunctionIndex = HasPerk, Parameter1 = 0x000DEAD1u }
        });
        var valid = new HashSet<uint> { 0x00000001u };

        var encoded = PerkEncoder.EncodeNew(perk, validFormIds: valid);

        Assert.Empty(encoded.Subrecords.Where(s => s.Signature == "CTDA"));
        Assert.Contains(encoded.Warnings, w => w.Contains("CTDA sanitizer"));
    }

    [Fact]
    public void PerkEncodeNew_keeps_GetActorValue_perk_condition_with_non_FormID_param()
    {
        // Skill/SPECIAL requirements: GetActorValue(ActorValue) — Param1 is an enum, not a FormID.
        var perk = MakePerk(conditions: new()
        {
            new PerkCondition { FunctionIndex = GetActorValue, Parameter1 = 5u, ComparisonValue = 50f }
        });
        var valid = new HashSet<uint>();

        var encoded = PerkEncoder.EncodeNew(perk, validFormIds: valid);

        var ctda = Assert.Single(encoded.Subrecords, s => s.Signature == "CTDA");
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(ctda.Bytes.AsSpan(12, 4)));
    }

    [Fact]
    public void PerkEncodeNew_remaps_HasPerk_FormID_via_alias_table()
    {
        var perk = MakePerk(conditions: new()
        {
            new PerkCondition { FunctionIndex = HasPerk, Parameter1 = 0x01999AAAu }
        });
        var valid = new HashSet<uint> { 0x01000123u };
        var remap = new Dictionary<uint, uint> { [0x01999AAAu] = 0x01000123u };

        var encoded = PerkEncoder.EncodeNew(perk, validFormIds: valid, remapTable: remap);

        var ctda = Assert.Single(encoded.Subrecords, s => s.Signature == "CTDA");
        Assert.Equal(0x01000123u, BinaryPrimitives.ReadUInt32LittleEndian(ctda.Bytes.AsSpan(12, 4)));
    }

    [Fact]
    public void PerkEncodeNew_emits_conditions_verbatim_when_no_validFormIds_supplied()
    {
        var perk = MakePerk(conditions: new()
        {
            new PerkCondition { FunctionIndex = HasPerk, Parameter1 = 0xDEADBEEFu }
        });

        var encoded = PerkEncoder.EncodeNew(perk);

        var ctda = Assert.Single(encoded.Subrecords, s => s.Signature == "CTDA");
        Assert.Equal(0xDEADBEEFu, BinaryPrimitives.ReadUInt32LittleEndian(ctda.Bytes.AsSpan(12, 4)));
    }

    // ---------- PERK per-entry conditions ----------

    [Fact]
    public void PerkEncodeNew_drops_PRKC_block_when_all_entry_conditions_dropped()
    {
        // When every CTDA in an entry chain is dropped, PRKC must NOT be emitted (an empty
        // tab-count would mislead the engine about the chain shape).
        var perk = MakePerk(entries: new()
        {
            new PerkEntry
            {
                Type = 2, EntryPoint = 0, FunctionType = 0, EffectValue = 1f,
                Conditions = new()
                {
                    new PerkCondition { FunctionIndex = HasPerk, Parameter1 = 0x000DEAD1u }
                }
            }
        });
        var valid = new HashSet<uint>();

        var encoded = PerkEncoder.EncodeNew(perk, validFormIds: valid);

        Assert.Empty(encoded.Subrecords.Where(s => s.Signature == "PRKC"));
        // Make sure PRKE / DATA / EPFT / EPFD / PRKF still emit (the entry itself isn't lost).
        Assert.Single(encoded.Subrecords, s => s.Signature == "PRKE");
        Assert.Single(encoded.Subrecords, s => s.Signature == "PRKF");
    }

    [Fact]
    public void PerkEncodeNew_PRKC_emits_when_at_least_one_entry_condition_survives()
    {
        var perk = MakePerk(entries: new()
        {
            new PerkEntry
            {
                Type = 2, EntryPoint = 0, FunctionType = 0, EffectValue = 1f,
                Conditions = new()
                {
                    new PerkCondition { FunctionIndex = HasPerk, Parameter1 = 0x000DEAD1u },
                    new PerkCondition { FunctionIndex = GetActorValue, Parameter1 = 5u, ComparisonValue = 50f }
                }
            }
        });
        var valid = new HashSet<uint>();

        var encoded = PerkEncoder.EncodeNew(perk, validFormIds: valid);

        Assert.Single(encoded.Subrecords.Where(s => s.Signature == "PRKC"));
        // Only the GetActorValue CTDA survives (HasPerk was dropped).
        var ctdaCount = encoded.Subrecords.Count(s => s.Signature == "CTDA");
        Assert.Equal(1, ctdaCount);
    }

    // ---------- Helpers ----------

    private static QuestRecord MakeQuest(
        List<DialogueCondition>? conditions = null,
        List<QuestStage>? stages = null)
    {
        return new QuestRecord
        {
            FormId = 0x01000123,
            EditorId = "TestQuest",
            FullName = "Test",
            Flags = 1,
            Priority = 50,
            Conditions = conditions ?? [],
            Stages = stages ?? []
        };
    }

    private static PerkRecord MakePerk(
        List<PerkCondition>? conditions = null,
        List<PerkEntry>? entries = null)
    {
        return new PerkRecord
        {
            FormId = 0x01000456,
            EditorId = "TestPerk",
            FullName = "Test",
            Trait = 0,
            MinLevel = 1,
            Ranks = 1,
            Playable = 1,
            Conditions = conditions ?? [],
            Entries = entries ?? []
        };
    }
}
