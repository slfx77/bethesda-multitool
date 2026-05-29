using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Magic;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.Parity;

/// <summary>
///     Tier 3 byte-exact parity. Each test uses a minimal record with no outgoing FormID
///     references, so the planner's emit set and remap table behave identically to legacy
///     defaults (null/empty). End-to-end parity for records WITH ref payloads needs
///     additional plumbing through the plan — covered by separate end-to-end tests once
///     Tier 3 plumbing lands.
/// </summary>
public sealed class Tier3EncoderParityTests
{
    [Fact]
    public void New_Scpt_With_No_Refs_Parity()
    {
        var scpt = new ScriptRecord
        {
            FormId = 0x01000800,
            EditorId = "TestScript",
        };

        var legacy = ScptEncoder.EncodeNew(scpt, validFormIds: null, remapTable: null);
        PlannerTier1ParityHelper.AssertNewRecordParity("SCPT", scpt.FormId, scpt, legacy);
    }

    [Fact]
    public void New_Perk_With_No_Refs_Parity()
    {
        var perk = new PerkRecord
        {
            FormId = 0x01000800,
            EditorId = "TestPerk",
        };

        var legacy = PerkEncoder.EncodeNew(perk, validFormIds: null, remapTable: null);
        PlannerTier1ParityHelper.AssertNewRecordParity("PERK", perk.FormId, perk, legacy);
    }

    [Fact]
    public void New_Cont_With_No_Refs_Parity()
    {
        var cont = new ContainerRecord
        {
            FormId = 0x01000800,
            EditorId = "TestCont",
        };

        var legacy = ContEncoder.EncodeNew(cont, validFormIds: null, remapTable: null);
        PlannerTier1ParityHelper.AssertNewRecordParity("CONT", cont.FormId, cont, legacy);
    }

    [Fact]
    public void New_Idle_With_No_Refs_Parity()
    {
        var idle = new IdleAnimationRecord
        {
            FormId = 0x01000800,
            EditorId = "TestIdle",
        };

        var legacy = IdleEncoder.EncodeNew(idle, validFormIds: null, remapTable: null);
        PlannerTier1ParityHelper.AssertNewRecordParity("IDLE", idle.FormId, idle, legacy);
    }

    [Fact]
    public void New_Term_With_No_Refs_Parity()
    {
        var term = new TerminalRecord
        {
            FormId = 0x01000800,
            EditorId = "TestTerm",
        };

        var legacy = TermEncoder.EncodeNew(term, validFormIds: null, remapTable: null);
        PlannerTier1ParityHelper.AssertNewRecordParity("TERM", term.FormId, term, legacy);
    }

    [Fact]
    public void New_Lvli_With_No_Refs_Parity()
    {
        var lvli = new LeveledListRecord
        {
            FormId = 0x01000800,
            EditorId = "TestLvli",
            ListType = "LVLI",
        };

        var legacy = LvliEncoder.EncodeNew(lvli, validFormIds: null, remapTable: null);
        PlannerTier1ParityHelper.AssertNewRecordParity("LVLI", lvli.FormId, lvli, legacy);
    }

    [Fact]
    public void New_Npc_With_No_Refs_Parity()
    {
        var npc = new NpcRecord
        {
            FormId = 0x01000800,
            EditorId = "TestNpc",
        };

        var legacy = NpcEncoder.EncodeNew(
            npc,
            masterFormIds: null,
            masterNpcByRace: null,
            validPackageFormIds: null,
            remapTable: null,
            validFormIds: null);
        PlannerTier1ParityHelper.AssertNewRecordParity("NPC_", npc.FormId, npc, legacy);
    }

    [Fact]
    public void New_Crea_With_No_Refs_Parity()
    {
        var crea = new CreatureRecord
        {
            FormId = 0x01000800,
            EditorId = "TestCrea",
        };

        var legacy = CreaEncoder.EncodeNew(crea, validFormIds: null, remapTable: null);
        PlannerTier1ParityHelper.AssertNewRecordParity("CREA", crea.FormId, crea, legacy);
    }

    [Fact]
    public void New_Qust_With_No_Refs_Parity()
    {
        var quest = new QuestRecord
        {
            FormId = 0x01000800,
            EditorId = "TestQuest",
        };

        var legacy = QustEncoder.EncodeNew(quest, validFormIds: null, remapTable: null);
        PlannerTier1ParityHelper.AssertNewRecordParity("QUST", quest.FormId, quest, legacy);
    }

    [Fact]
    public void New_Info_With_No_Refs_Parity()
    {
        var info = new DialogueRecord
        {
            FormId = 0x01000800,
        };

        var legacy = InfoEncoder.EncodeNew(info, validFormIds: null, remapTable: null);
        PlannerTier1ParityHelper.AssertNewRecordParity("INFO", info.FormId, info, legacy);
    }
}
