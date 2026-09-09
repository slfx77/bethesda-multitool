using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Battlespire;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Battlespire;

/// <summary>
///     Vectors for <see cref="BattlespireSaveVars" />, shaped after the SAVE0 fixture measured
///     2026-09-07: the fixed 34,805-byte block layout and the count words that discriminate it.
/// </summary>
public sealed class BattlespireSaveVarsTests
{
    private static void U32(byte[] f, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(offset), value);
    }

    private static void S32(byte[] f, int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(offset), value);
    }

    private static byte[] Vars()
    {
        var f = new byte[BattlespireSaveVars.FileLength];

        // Player body copy: name + attributes at the body offsets (UESP +65 / +97 minus 65).
        Encoding.ASCII.GetBytes("Biggus Dickus").CopyTo(f, 0);
        uint[] attributes = [67, 50, 35, 40, 52, 55, 50, 75];
        for (var i = 0; i < 8; i++)
        {
            U32(f, 32 + i * 4, attributes[i]);
            U32(f, 64 + i * 4, attributes[i]);
        }

        Encoding.ASCII.GetBytes("Monk").CopyTo(f, 378);

        // Misc: level 1, clock 1,169,900, 0, 0, 4, 4.
        U32(f, BattlespireSaveVars.MiscOffset, 1);
        U32(f, BattlespireSaveVars.MiscOffset + 4, 1_169_900);
        U32(f, BattlespireSaveVars.MiscOffset + 16, 4);
        U32(f, BattlespireSaveVars.MiscOffset + 20, 4);

        // Log: two runs, an empty run between them, junk tail without a terminator.
        Encoding.ASCII.GetBytes("ou healed 18. You now have 22 out of 70.\0\0nter name of save game.\0")
            .CopyTo(f, BattlespireSaveVars.LogTextOffset);
        // ⚠ Shaped like the fixture: a LEADING run of timestamps (order NOT established — the
        // fixture's own five are unordered), a zero gap, then the undecoded quartet at index 16..19
        // whose last word (2,560,000) is past the clock.
        U32(f, BattlespireSaveVars.LogTimestampsOffset, 1_166_615);
        U32(f, BattlespireSaveVars.LogTimestampsOffset + 4, 1_146_280);
        U32(f, BattlespireSaveVars.LogTimestampsOffset + 16 * 4, 1_169_900);
        U32(f, BattlespireSaveVars.LogTimestampsOffset + 19 * 4, 0x0027_1000);

        // ConversationMap: two NPCs, count 2.
        U32(f, BattlespireSaveVars.ConversationMapOffset, 0x241);
        Encoding.ASCII.GetBytes("dm1").CopyTo(f, BattlespireSaveVars.ConversationMapOffset + 4);
        U32(f, BattlespireSaveVars.ConversationMapOffset + 8, 0x243);
        Encoding.ASCII.GetBytes("sk1").CopyTo(f, BattlespireSaveVars.ConversationMapOffset + 12);
        U32(f, BattlespireSaveVars.ConversationMapCountOffset, 2);

        // StaticEnemy: one Dremora (EnemyList 2 stored as 3), count 1.
        var se = BattlespireSaveVars.StaticEnemyOffset;
        U32(f, se, 0x241);
        S32(f, se + 4, 3);
        S32(f, se + 8, 50);
        S32(f, se + 12, 65);
        S32(f, se + 16, 800);
        S32(f, se + 20, 100);
        S32(f, se + 24, 65);
        S32(f, se + 28, 13);
        S32(f, se + 36, 10);
        S32(f, se + 40, 19);
        U32(f, BattlespireSaveVars.StaticEnemyCountOffset, 1);

        // HP/SP: a gem at 0x12D healing 80 spell points, count 1 (9-byte records).
        U32(f, BattlespireSaveVars.HpSpModifyOffset, 0x12D);
        S32(f, BattlespireSaveVars.HpSpModifyOffset + 4, 80);
        f[BattlespireSaveVars.HpSpModifyOffset + 8] = 1;
        U32(f, BattlespireSaveVars.HpSpModifyOffset + 9, 0x176);
        S32(f, BattlespireSaveVars.HpSpModifyOffset + 13, -25);
        U32(f, BattlespireSaveVars.HpSpModifyCountOffset, 2);

        // Sigil: one at Y -192, count 1.
        U32(f, BattlespireSaveVars.SigilOffset, 0x128);
        S32(f, BattlespireSaveVars.SigilOffset + 4, -192);
        U32(f, BattlespireSaveVars.SigilCountOffset, 1);

        // GlobalVariable: the count word PRECEDES the block.
        U32(f, BattlespireSaveVars.GlobalVariableCountOffset, 2);
        U32(f, BattlespireSaveVars.GlobalVariableOffset, 88_610_309); // PCMale
        U32(f, BattlespireSaveVars.GlobalVariableOffset + 4, 1);
        U32(f, BattlespireSaveVars.GlobalVariableOffset + 8, 1_202_329_098); // PCFemale
        U32(f, BattlespireSaveVars.GlobalVariableOffset + 12, 0);

        // LocalVariable: filled from the LAST slot down.
        var last = BattlespireSaveVars.LocalVariableOffset + 127 * BattlespireSaveVars.LocalVariableRecordLength;
        U32(f, last, 0x2B4);
        U32(f, last + 4, 0x80348610); // SKNoTalk
        U32(f, last + 12, 0x77625159); // ScampMad
        U32(f, last + 16, 1);

        f[BattlespireSaveVars.MonsterTypeCountOffset] = 3;
        return f;
    }

    [Fact]
    public void Parse_TilesTheFixedBlocksAndReadsEachCountWord()
    {
        var vars = BattlespireSaveVars.Parse(Vars(), "SAVEVARS.DAT");

        Assert.Equal("Biggus Dickus", vars.Player.Name);
        Assert.Equal<uint[]>([67, 50, 35, 40, 52, 55, 50, 75], [.. vars.Player.Attributes]);
        Assert.Equal("Monk", vars.Player.ClassName);
        Assert.Equal((1u, 1_169_900u), (vars.CurrentLevel, vars.CurrentTimestamp));
        Assert.Equal<uint[]>([0, 0, 4, 4], [.. vars.MiscUnknown]);

        Assert.Equal<string[]>(["ou healed 18. You now have 22 out of 70.", "nter name of save game."],
            [.. vars.LogMessages]);
        // ⚠⚠ The region is 534 words; only the LEADING non-zero run is a timestamp list. The word
        // at index 19 is 2,560,000 — past the clock — so it must NOT come back as a timestamp.
        Assert.Equal(534, vars.LogTimestampWords.Count);
        Assert.Equal((1_166_615u, 1_169_900u, 0x0027_1000u),
            (vars.LogTimestampWords[0], vars.LogTimestampWords[16], vars.LogTimestampWords[19]));
        Assert.Equal<uint[]>([1_166_615, 1_146_280], [.. vars.LogTimestamps]);
        Assert.True(vars.CurrentTimestamp >= vars.LogTimestamps.Max());

        Assert.Equal(2, vars.ConversationMap.DeclaredCount);
        Assert.True(vars.ConversationMap.CountsAgree);
        Assert.Equal<(uint, string)[]>([(0x241, "dm1"), (0x243, "sk1")],
            [.. vars.ConversationMap.Records.Select(c => (c.RecordId, c.Stem))]);

        var enemy = Assert.Single(vars.StaticEnemies.Records);
        Assert.True(vars.StaticEnemies.CountsAgree);
        Assert.Equal((0x241u, 3, 2, "Dremora"), (enemy.RecordId, enemy.EnemyId, enemy.EnemyListIndex, enemy.EnemyName));
        Assert.Equal((50, 65, 800, 100, 65, 13),
            (enemy.Speed, enemy.Strength, enemy.SpellPoints, enemy.Health, enemy.Skill, enemy.Goal));
        Assert.Equal<int[]>([10, 19, 0, 0, 0], [.. enemy.SpellIds]);

        // ⚠ 9-byte records: IsSP is the ninth BYTE, so the second record starts at +9, not +12.
        Assert.Equal(2, vars.HpSpModifiers.DeclaredCount);
        Assert.True(vars.HpSpModifiers.CountsAgree);
        Assert.Equal(new BattlespireSaveHpSpModify(0x12D, 80, true), vars.HpSpModifiers.Records[0]);
        Assert.Equal(new BattlespireSaveHpSpModify(0x176, -25, false), vars.HpSpModifiers.Records[1]);

        Assert.Equal(0u, vars.Block5Word);
        Assert.Equal(new BattlespireSaveSigil(0x128, -192, 0), Assert.Single(vars.Sigils.Records));

        Assert.Equal(2, vars.GlobalVariables.DeclaredCount);
        Assert.True(vars.GlobalVariables.CountsAgree);
        Assert.Equal<(string?, uint)[]>([("PCMale", 1), ("PCFemale", 0)],
            [.. vars.GlobalVariables.Records.Select(v => (v.Name, v.Value))]);

        var local = Assert.Single(vars.LocalVariables);
        Assert.Equal((127, 0x2B4u), (local.Slot, local.RecordId));
        Assert.Equal<(string?, uint)[]>([("SKNoTalk", 0), ("ScampMad", 1)],
            [.. local.Variables.Select(v => (v.Name, v.Value))]);

        Assert.Equal(3, vars.MonsterTypeCounts[0]);
        Assert.Equal(16, vars.MonsterTypeCounts.Count);
        Assert.Equal(96, vars.Tail.Length);
        Assert.Equal(97, vars.UnknownGap.Length);
        Assert.Equal(512, vars.Block5.Length);
        Assert.Equal(1280, vars.LogText.Length);
    }

    [Fact]
    public void Parse_RejectsAnyOtherLength()
    {
        var error = Assert.Throws<InvalidDataException>(() => BattlespireSaveVars.Parse(new byte[34_804], "SHORT.DAT"));
        Assert.Contains("34805", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsACountWordBeyondTheBlocksCapacity()
    {
        var f = Vars();
        U32(f, BattlespireSaveVars.ConversationMapCountOffset, 129);

        var error = Assert.Throws<InvalidDataException>(() => BattlespireSaveVars.Parse(f, "BAD.DAT"));
        Assert.Contains("ConversationMap", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PopulatedCount_DisagreesWhenTheCountWordLies()
    {
        // The count word alone cannot prove a block boundary (any partition of a fixed-size file
        // "tiles"); the populated-slot count is what makes a mismatch visible.
        var f = Vars();
        U32(f, BattlespireSaveVars.SigilCountOffset, 0);
        var vars = BattlespireSaveVars.Parse(f, "SAVEVARS.DAT");

        Assert.Equal((0, 1), (vars.Sigils.DeclaredCount, vars.Sigils.PopulatedCount));
        Assert.False(vars.Sigils.CountsAgree);
        Assert.Empty(vars.Sigils.Records);
    }

    [Fact]
    public void LogTimestamps_StopAtSlot16EvenWhenTheRunWouldReachTheUndecodedQuartet()
    {
        // The SAVE0 fixture writes 5 stamps and leaves slots 5..15 zero, so it CANNOT exercise a
        // run that reaches word 16 — the reader's bound holds there by data, not by construction.
        // This vector fills all 16 stamp slots so the run would otherwise walk into the quartet at
        // 16..19 and return 0x271000 = 2,560,000, which the fixture measured to be PAST the clock
        // (1,169,900). Falsifier: an unbounded scan returns 20 words ending in 2,560,000 and every
        // assertion below fails; that is exactly what the reader did before the bound was added.
        var f = Vars();
        for (var i = 0; i < 16; i++)
        {
            U32(f, BattlespireSaveVars.LogTimestampsOffset + i * 4, (uint)(1_100_000 + i));
        }

        U32(f, BattlespireSaveVars.LogTimestampsOffset + 17 * 4, 127);
        U32(f, BattlespireSaveVars.LogTimestampsOffset + 18 * 4, 127);

        var vars = BattlespireSaveVars.Parse(f, "SAVEVARS.DAT");

        Assert.Equal(16, vars.LogTimestamps.Count);
        Assert.Equal((1_100_000u, 1_100_015u), (vars.LogTimestamps[0], vars.LogTimestamps[15]));
        Assert.DoesNotContain(0x0027_1000u, vars.LogTimestamps);
        Assert.True(vars.CurrentTimestamp >= vars.LogTimestamps.Max());

        // The raw region is still handed back whole, quartet included, for callers that want it.
        Assert.Equal(534, vars.LogTimestampWords.Count);
        Assert.Equal<uint[]>([1_169_900, 127, 127, 0x0027_1000], [.. vars.LogTimestampWords.Skip(16).Take(4)]);
    }

    [Fact]
    public void IsSaveVars_RequiresTheExactLengthAndAPrintableName()
    {
        Assert.True(BattlespireSaveVars.IsSaveVars(Vars()));
        Assert.False(BattlespireSaveVars.IsSaveVars(new byte[BattlespireSaveVars.FileLength]));
        Assert.False(BattlespireSaveVars.IsSaveVars(new byte[100]));
    }
}