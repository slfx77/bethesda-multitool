using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Daggerfall;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>
///     Synthetic vectors for <see cref="DaggerfallSaveVars" />: the measured scalars, the 62
///     region blocks, and the faction table whose length the file STATES rather than implies.
/// </summary>
public class DaggerfallSaveVarsTests
{
    /// <summary>
    ///     A SAVEVARS image with <paramref name="factionCount" /> faction records and the fixture's
    ///     scalar values written in by hand.
    /// </summary>
    internal static byte[] Vars(int factionCount = 2)
    {
        var bytes = new byte[DaggerfallSaveVars.FactionTableOffset + factionCount * DaggerfallSaveFaction.RecordLength];
        var span = bytes.AsSpan();

        BinaryPrimitives.WriteInt32LittleEndian(span[(DaggerfallSaveVars.BiographyModifiersOffset + 4)..], -5);
        span[DaggerfallSaveVars.EmperorSonOffset] = 1;
        BinaryPrimitives.WriteInt16LittleEndian(span[DaggerfallSaveVars.TravelFlagsOffset..], 0x219);

        // The 71-byte copy of the tree's Move record root: type 4, id 0x12712, parent 0xC3810001.
        span[DaggerfallSaveVars.PlayerPositionRootOffset] = 0x04;
        BinaryPrimitives.WriteInt32LittleEndian(span[(DaggerfallSaveVars.PlayerPositionRootOffset + 7)..], 3_592_670);
        BinaryPrimitives.WriteInt32LittleEndian(span[(DaggerfallSaveVars.PlayerPositionRootOffset + 11)..], -484);
        BinaryPrimitives.WriteInt32LittleEndian(span[(DaggerfallSaveVars.PlayerPositionRootOffset + 15)..], 11_190_722);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(DaggerfallSaveVars.PlayerPositionRootOffset + 31)..],
            0x00012712);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(DaggerfallSaveVars.PlayerPositionRootOffset + 39)..],
            0xC3810001);

        span[DaggerfallSaveVars.GlobalQuestVarsOffset] = 0x7F;
        BinaryPrimitives.WriteInt16LittleEndian(span[0x38F..], 12);
        span[0x391] = 0;
        span[0x3A3] = 1;
        span[0x3A6] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(span[0x3AB..], 20);
        new byte[] { 0, 2, 2, 2, 0, 2 }.CopyTo(span[0x3B7..]);
        new byte[] { 0, 2, 2, 2, 0, 2 }.CopyTo(span[0x17A2..]);
        span[0x3BF] = DaggerfallSaveVars.UiFlagWeaponDrawn;
        BinaryPrimitives.WriteUInt32LittleEndian(span[DaggerfallSaveVars.GameTimeOffset..], 524_043);
        span[0x3D9] = 1;

        // Region 17's block: legal reputation, persecuted temple and price adjustment.
        var region17 = DaggerfallSaveVars.RegionsOffset + 17 * DaggerfallSaveRegion.RecordLength;
        span[region17] = 42;
        span[region17 + DaggerfallSaveRegion.ValueCount] = 0x80;
        span[region17 + 72] = 3;
        span[region17 + 73] = 5;
        BinaryPrimitives.WriteInt16LittleEndian(span[(region17 + 74)..], -7);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(region17 + 76)..], 21);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(region17 + 78)..], 813);

        // Region 61 is the LAST block, and its PriceAdjustment is the u16 at 0x1738 — written
        // here through the region arithmetic on purpose, because an earlier pass read those same
        // two bytes a second time as an "unknown scalar". 0x173A is the first byte past the table.
        var region61 = DaggerfallSaveVars.RegionsOffset + 61 * DaggerfallSaveRegion.RecordLength;
        BinaryPrimitives.WriteUInt16LittleEndian(span[(region61 + 78)..], 886);
        span[0x173A] = 17;
        span[0x173B] = 0;
        new byte[] { 0x7F, 0xFF, 0xFF, 0xD8, 0xD6, 0, 0, 0 }.CopyTo(span[0x1748..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[0x1750..], 25_600_000);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x1796..], 525_007);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x179A..], 524_036);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x17A8..], 10_000);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x17AC..], 0xC3810001);
        BinaryPrimitives.WriteUInt32LittleEndian(span[DaggerfallSaveVars.FactionCountOffset..], (uint)factionCount);

        if (factionCount > 0)
        {
            WriteFaction(span[DaggerfallSaveVars.FactionTableOffset..], 0, -1, "Clavicus Vile", 0, 50, 1);
        }

        if (factionCount > 1)
        {
            WriteFaction(
                span[(DaggerfallSaveVars.FactionTableOffset + DaggerfallSaveFaction.RecordLength)..],
                7,
                32,
                "Northmoor",
                -3,
                52,
                208);
        }

        return bytes;
    }

    private static void WriteFaction(Span<byte> record, byte type, sbyte region, string name, short reputation,
        short power, short id)
    {
        record[0] = type;
        record[1] = (byte)region;
        record[2] = 4;
        Encoding.ASCII.GetBytes(name).CopyTo(record[3..]);
        BinaryPrimitives.WriteInt16LittleEndian(record[29..], reputation);
        BinaryPrimitives.WriteInt16LittleEndian(record[31..], power);
        BinaryPrimitives.WriteInt16LittleEndian(record[33..], id);
        BinaryPrimitives.WriteInt16LittleEndian(record[35..], -1);
        BinaryPrimitives.WriteInt16LittleEndian(record[37..], 0x40);
        BinaryPrimitives.WriteUInt32LittleEndian(record[39..], 123_456u);
        BinaryPrimitives.WriteInt32LittleEndian(record[43..], 9);
        BinaryPrimitives.WriteInt16LittleEndian(record[47..], 11);
        BinaryPrimitives.WriteInt16LittleEndian(record[49..], 12);
        record[51] = 13;
        record[52] = 0xFF;
        record[53] = 2;
        record[54] = 6;
        record[55] = 3;
        BinaryPrimitives.WriteInt32LittleEndian(record[56..], 100);
        BinaryPrimitives.WriteInt32LittleEndian(record[68..], 200);
        BinaryPrimitives.WriteInt32LittleEndian(record[80..], 15_966_448);
    }

    [Fact]
    public void Parse_ReadsTheMeasuredScalars()
    {
        var vars = DaggerfallSaveVars.Parse(Vars(), "SAVEVARS.DAT");

        Assert.Equal<int>([0, -5, 0, 0, 0], vars.BiographyModifiers);
        Assert.Equal(1, vars.EmperorSonIndex);
        Assert.Equal("Cephorus", vars.EmperorSonName);
        Assert.Equal(0x219, vars.TravelFlags);
        Assert.Equal(12, vars.LastSpellCost);
        Assert.Equal(0, vars.IsDay);
        Assert.Equal(1, vars.CrimeCommitted);
        Assert.Equal(1, vars.InDungeonWater);
        Assert.Equal(20, vars.BreathRemaining);
        Assert.True(vars.IsWeaponDrawn);
        Assert.Equal(1, vars.UsingLeftHandWeapon);
        Assert.Equal(17, vars.CurrentRegionIndex);
        Assert.Equal(0, vars.CheatFlags);
        Assert.Equal(25_600_000, vars.Ship);
        Assert.Equal(525_007u, vars.Unknown1796);
        Assert.Equal(524_036u, vars.LastSkillCheckTime);
        Assert.Equal(10_000u, vars.DungeonWaterLevel);
        Assert.Equal(0x7F, vars.GlobalQuestVars.Span[0]);
        Assert.Equal(DaggerfallSaveVars.GlobalQuestVarsLength, vars.GlobalQuestVars.Length);
        Assert.Equal<byte>([0, 2, 2, 2, 0, 2], vars.ClimateWeathers.ToArray());
        Assert.Equal(vars.ClimateWeathers.ToArray(), vars.ClimateWeathers2.ToArray());
        Assert.Equal<byte>([0x7F, 0xFF, 0xFF, 0xD8, 0xD6, 0, 0, 0], vars.Unknown1748.ToArray());
    }

    [Fact]
    public void Parse_ReadsTheGameClockAsACalendarDate()
    {
        var vars = DaggerfallSaveVars.Parse(Vars(), "SAVEVARS.DAT");

        Assert.Equal(524_043u, vars.GameTimeMinutes);
        Assert.Equal("22:03, 4 Morning Star 3E405", vars.GameTime.ToString());
    }

    /// <summary>The current-location word is (exterior LocationId &lt;&lt; 16) | 1.</summary>
    [Fact]
    public void Parse_SplitsTheCurrentWorldRecordId()
    {
        var vars = DaggerfallSaveVars.Parse(Vars(), "SAVEVARS.DAT");

        Assert.Equal(0xC3810001u, vars.CurrentWorldRecordId);
        Assert.Equal(50_049, vars.CurrentLocationId);
    }

    /// <summary>The 71 bytes at 0x2F8 decode with the tree's own root reader.</summary>
    [Fact]
    public void Parse_DecodesThePlayerPositionRootCopy()
    {
        var vars = DaggerfallSaveVars.Parse(Vars(), "SAVEVARS.DAT");

        Assert.Equal(DaggerfallSaveRecordType.Move, vars.PlayerPositionRoot.Type);
        Assert.Equal(0x00012712u, vars.PlayerPositionRoot.RecordId);
        Assert.Equal(0xC3810001u, vars.PlayerPositionRoot.ParentId);
        Assert.Equal(3_592_670, vars.PlayerPositionRoot.X);
        Assert.Equal(-484, vars.PlayerPositionRoot.Y);
        Assert.Equal(11_190_722, vars.PlayerPositionRoot.Z);
        Assert.Equal(0, vars.PlayerPositionRoot.Data.Length);
    }

    [Fact]
    public void Parse_ReadsSixtyTwoRegionBlocks()
    {
        var vars = DaggerfallSaveVars.Parse(Vars(), "SAVEVARS.DAT");

        Assert.Equal(62, vars.Regions.Count);
        var region = vars.Regions[17];
        Assert.Equal(17, region.Index);
        Assert.Equal(29, region.Values.Length);
        Assert.Equal(29, region.Flags.Length);
        Assert.Equal(14, region.Flags2.Length);
        Assert.Equal(42, region.Values.Span[0]);
        Assert.Equal(0x80, region.Flags.Span[0]);
        Assert.Equal(3, region.PrecipitationOverride);
        Assert.Equal(5, region.SeverePunishmentFlags);
        Assert.Equal(-7, region.LegalReputation);
        Assert.Equal(21, region.PersecutedTempleId);
        Assert.Equal(813, region.PriceAdjustment);
        Assert.Equal(0, vars.Regions[16].PriceAdjustment);

        // The table's END, pinned as an offset literal rather than recomputed from the constants:
        // 0x3DA + 61 x 80 + 78 = 0x1738, so region 61's PriceAdjustment is the u16 there and
        // CurrentRegionIndex at 0x173A is the first byte past the table. Fails if RegionsOffset,
        // RecordLength or the +78 field position moves.
        Assert.Equal(0x1738, DaggerfallSaveVars.RegionsOffset + 61 * DaggerfallSaveRegion.RecordLength + 78);
        Assert.Equal(886, vars.Regions[61].PriceAdjustment);
    }

    [Fact]
    public void Parse_ReadsTheStatedFactionTable()
    {
        var vars = DaggerfallSaveVars.Parse(Vars(), "SAVEVARS.DAT");

        Assert.Equal(2, vars.DeclaredFactionCount);
        Assert.Equal(2, vars.Factions.Count);

        var vile = vars.Factions[0];
        Assert.Equal("Clavicus Vile", vile.Name);
        Assert.Equal(1, vile.Id);
        Assert.Equal(0, vile.TypeId);
        Assert.Equal(-1, vile.RegionIndex);
        Assert.Equal(50, vile.Power);

        var northmoor = vars.Factions[1];
        Assert.Equal("Northmoor", northmoor.Name);
        Assert.Equal(208, northmoor.Id);
        Assert.Equal(7, northmoor.TypeId);
        Assert.Equal(32, northmoor.RegionIndex);
        Assert.Equal(-3, northmoor.Reputation);
        Assert.Equal(-1, northmoor.Face2);
        Assert.Equal(123_456u, northmoor.RulerNameSeed);
        Assert.Equal(100, northmoor.Allies[0]);
        Assert.Equal(200, northmoor.Enemies[0]);
        Assert.Equal(15_966_448, northmoor.HeapPointers[0]);

        Assert.Same(northmoor, vars.FindFaction(208));
        Assert.Null(vars.FindFaction(9_999));
    }

    /// <summary>
    ///     The stated count must reach EOF exactly. Under-stating it by one leaves 92 unclaimed
    ///     bytes, which the reference — which sizes the table from the file length instead — would
    ///     have walked straight past.
    /// </summary>
    [Fact]
    public void Parse_RefusesACountThatDoesNotReachEndOfFile()
    {
        var bytes = Vars();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(DaggerfallSaveVars.FactionCountOffset), 1);

        var error = Assert.Throws<InvalidDataException>(() => DaggerfallSaveVars.Parse(bytes, "SAVEVARS.DAT"));
        Assert.Contains("states 1 factions", error.Message, StringComparison.Ordinal);
        Assert.False(DaggerfallSaveVars.IsSaveVars(bytes));
    }

    [Fact]
    public void Parse_RefusesAFileTooShortForTheFactionTable()
    {
        Assert.Throws<InvalidDataException>(() =>
            DaggerfallSaveVars.Parse(new byte[DaggerfallSaveVars.FactionTableOffset - 1], "SAVEVARS.DAT"));
        Assert.False(DaggerfallSaveVars.IsSaveVars(new byte[DaggerfallSaveVars.FactionTableOffset - 1]));
    }

    [Fact]
    public void IsSaveVars_AcceptsAFileWhoseTableTiles()
    {
        Assert.True(DaggerfallSaveVars.IsSaveVars(Vars()));
        Assert.True(DaggerfallSaveVars.IsSaveVars(Vars(0)));
    }

    /// <summary>
    ///     FACTION.TXT's "#id" / "name:" pairing keeps BOTH names of a repeated id. ⛔ The save
    ///     does not pick one: retail declares 77 twice ("The Guildmaster", "The Master of
    ///     Initiates") and SAVE0's table carries BOTH, at slots 129 and 135 — 366 records over 365
    ///     distinct ids. A first-name-wins map therefore reports a false mismatch on the second of
    ///     the pair, on a record that is perfectly good (measured 2026-09-07: all names + TrimEnd
    ///     on both sides = 0 mismatches, first-name-wins = 1, no trim = 1).
    /// </summary>
    [Fact]
    public void ParseFactionText_KeepsEveryNameOfARepeatedId()
    {
        var text = Encoding.ASCII.GetBytes(
            "#1\nname: Clavicus Vile\nrep: 0\n\n#77\nname: The Guildmaster\n\n#77\nname: The Master of Initiates\n\n#208\nname: Northmoor\n");

        var names = DaggerfallSaveVars.ParseFactionText(text);

        Assert.Equal(3, names.Count);
        Assert.Equal("Clavicus Vile", Assert.Single(names[1]));
        Assert.Equal<string>(["The Guildmaster", "The Master of Initiates"], names[77]);
        Assert.Equal("Northmoor", Assert.Single(names[208]));
    }
}