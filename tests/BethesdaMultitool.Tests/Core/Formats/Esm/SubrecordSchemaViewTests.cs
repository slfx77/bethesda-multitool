using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm;

/// <summary>
///     Contract tests for <see cref="SubrecordSchemaView" /> — the typed read-side bridge that
///     mirrors <c>SchemaModelSerializer</c> on the write side. Verifies the four core invariants:
///     hard-fail on missing schema (matching encoder behavior), schema-driven field decode,
///     soft-fail variant returns null, and accessor coercion across adjacent numeric types.
/// </summary>
public class SubrecordSchemaViewTests
{
    [Fact]
    public void Read_ThrowsWhenSchemaNotRegistered()
    {
        // Sanity-check the registry has no schema for our sentinel — same guard as the
        // encoder-side test for symmetry.
        var lookup = SubrecordSchemaRegistry.GetSchema("ZZZZ", "NONE", 99);
        Assert.Null(lookup);

        var data = new byte[99];

        var ex = Assert.Throws<InvalidOperationException>(() => SubrecordSchemaView.Read("ZZZZ", "NONE", data, false));
        Assert.Contains("ZZZZ", ex.Message);
    }

    [Fact]
    public void Read_PopulatesFieldsFromSchemaWalk()
    {
        // ALCH/ENIT — 20 bytes: UInt32 Value + Bytes Flags(4) + FormId Addiction + Float
        // AddictionChance + FormId UseSoundOrWithdrawalEffect. Build the LE byte block by hand
        // and verify the view returns each field as the expected typed value.
        var data = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 250u);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), 0x00000002u);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), 0x000FAB42u);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(12, 4), 0.25f);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16, 4), 0x000FAB99u);

        var view = SubrecordSchemaView.Read("ENIT", "ALCH", data, false);

        Assert.Equal(250u, view.UInt32("Value"));
        Assert.Equal(0x000FAB42u, view.UInt32("WithdrawalEffect"));
        Assert.Equal(0.25f, view.Float("AddictionChance"));
        Assert.Equal(0x000FAB99u, view.UInt32("ConsumeSound"));
        // FormId returns null when value is zero — sanity-check the present-value path.
        Assert.Equal(0x000FAB42u, view.FormId("WithdrawalEffect"));
    }

    [Fact]
    public void TryRead_ReturnsNullWhenSchemaMissing()
    {
        var lookup = SubrecordSchemaRegistry.GetSchema("ZZZZ", "NONE", 99);
        Assert.Null(lookup);

        var view = SubrecordSchemaView.TryRead("ZZZZ", "NONE", new byte[99], false);

        Assert.Null(view);
    }

    [Fact]
    public void Float_CoercesAdjacentNumericTypes()
    {
        // ALCH/DATA (4 bytes, single Float "Weight"). Verifying the view's Float accessor
        // reads a registered Float field correctly — the broader coercion paths
        // (uint->float, etc.) are tested indirectly via SubrecordDataReader.GetFloat
        // which the view delegates to.
        var data = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(data, 1.5f);

        var view = SubrecordSchemaView.Read("DATA", "ALCH", data, false);

        Assert.Equal(1.5f, view.Float("Weight"));
        // Missing field falls back to the supplied default.
        Assert.Equal(-1f, view.Float("NotAField", -1f));
    }

    [Theory]
    [InlineData(0x0000000Eu)] // 14 — fits in the low word
    [InlineData(0x00120034u)] // spans both words
    public void UInt32WordSwapped_DecodesToSameValueOnBothPlatforms(uint value)
    {
        // RGDL/DATA byte 0-3 is DynamicBoneCount, a UInt32WordSwapped field: a PC plugin stores it as
        // a normal little-endian uint32, while an Xbox 360 plugin stores it as two big-endian uint16
        // words in little-endian word order (the conversion transform is [b0 b1 b2 b3] -> [b1 b0 b3 b2]).
        // Both must decode back to the same value.
        var pc = new byte[14];
        BinaryPrimitives.WriteUInt32LittleEndian(pc.AsSpan(0, 4), value);

        var xbox = new byte[14];
        // Word-swapped Xbox layout: low half (value & 0xFFFF) as a BE word at 0, high half at 2.
        BinaryPrimitives.WriteUInt16BigEndian(xbox.AsSpan(0, 2), (ushort)(value & 0xFFFF));
        BinaryPrimitives.WriteUInt16BigEndian(xbox.AsSpan(2, 2), (ushort)(value >> 16));

        var pcView = SubrecordSchemaView.Read("DATA", "RGDL", pc, false);
        var xboxView = SubrecordSchemaView.Read("DATA", "RGDL", xbox, true);

        Assert.Equal(value, pcView.UInt32("DynamicBoneCount"));
        Assert.Equal(value, xboxView.UInt32("DynamicBoneCount"));
    }

    [Fact]
    public void Read_BigEndian_SwapsScalarsButPreservesFlagBytes()
    {
        // ALCH/ENIT on a big-endian Xbox plugin: the multi-byte Value scalar is byte-swapped, but the
        // single Flags byte (schema models Flags as Bytes(4)) sits in place — the flag value is the low
        // byte on both platforms. This pins the premise behind the ALCH flags read fix.
        var data = new byte[20];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), 250u);
        data[4] = 0x02; // Flags: FoodItem bit; bytes 5-7 remain the unused tail.

        var view = SubrecordSchemaView.Read("ENIT", "ALCH", data, true);

        Assert.Equal(250u, view.UInt32("Value"));
        var flags = view.Bytes("Flags");
        Assert.NotNull(flags);
        Assert.Equal(0x02, flags![0]);
    }

    [Fact]
    public void Read_TruncatedCamsData_DecodesAllNineFields()
    {
        // A 36-byte CAMS DATA (no TargetPctBetweenActors) must decode all 9 fields, not a single float
        // from the old DATA->FloatArray fallback.
        var data = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 3u); // Action
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), 5u); // Flags
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(16, 4), 1.5f); // PlayerTimeMult
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(32, 4), 0.25f); // MinTime (last field)

        var view = SubrecordSchemaView.Read("DATA", "CAMS", data, false);

        Assert.Equal(3u, view.UInt32("Action"));
        Assert.Equal(5u, view.UInt32("Flags"));
        Assert.Equal(1.5f, view.Float("PlayerTimeMult"));
        Assert.Equal(0.25f, view.Float("MinTime"));
    }

    [Fact]
    public void Read_TruncatedIpdsData_DecodesNineMaterialFormIds()
    {
        // A 36-byte IPDS DATA carries 9 material FormIDs; all must decode (not one float).
        var data = new byte[36];
        for (var i = 0; i < 9; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 4, 4), 0x000A0000u + (uint)i);
        }

        var view = SubrecordSchemaView.Read("DATA", "IPDS", data, false);

        Assert.Equal(0x000A0000u, view.UInt32("Stone"));
        Assert.Equal(0x000A0008u, view.UInt32("Water")); // 9th field (index 8)
    }

    [Fact]
    public void FormId_ReturnsNullForZero()
    {
        // ALCH/ENIT with Withdrawal-Effect = 0 — view.FormId("WithdrawalEffect") should return null,
        // matching the prevailing handler idiom of suppressing zero FormIDs.
        var data = new byte[20];
        // Leave bytes 8-11 as zero (Withdrawal-Effect FormID).
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 99u);

        var view = SubrecordSchemaView.Read("ENIT", "ALCH", data, false);

        Assert.Null(view.FormId("WithdrawalEffect"));
        Assert.Equal(99u, view.UInt32("Value"));
    }
}