using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using BethesdaMultitool.Core.Formats.Fallout;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Fallout;

/// <summary>
///     Synthetic vectors for Fallout 1 prototypes, shaped after the 4,306 retail <c>.PRO</c> files
///     measured by independent Python walks 2026-09-06.
///     <para>
///         Two facts here are easy to get wrong and are pinned outright: the record is BIG-endian and
///         its length is fixed by the TYPE and SUBTYPE together, and a tile carries neither extended
///         flags nor a script — its 28 bytes end where the other families are still reading.
///     </para>
/// </summary>
public sealed class FalloutProFileTests
{
    private static byte[] Proto(FalloutProType type, int subtype = -1, uint textId = 100, uint? fid = null, int? size = null)
    {
        var length = size ?? FalloutProFile.ExpectedSize(type, subtype);
        var b = new byte[length];
        Be(b, 0, ((uint)type << 24) | 7);            // prototype 7 of its family
        Be(b, 4, textId);
        Be(b, 8, fid ?? (((uint)type << 24) | 3));
        Be(b, 12, 2);
        Be(b, 16, 32_768);
        Be(b, 20, 0x2000);
        if (type != FalloutProType.Tile && length >= 28)
        {
            Be(b, FalloutProFile.ExtendedFlagsOffset, 0x8000_0000);
        }
        else if (type == FalloutProType.Tile)
        {
            Be(b, FalloutProFile.ExtendedFlagsOffset, 5);   // a tile's material sits here instead
        }

        if (type is not (FalloutProType.Tile or FalloutProType.Misc) && length >= 32)
        {
            Be(b, FalloutProFile.ScriptIdOffset, FalloutProFile.NoScript);
        }

        if (subtype >= 0)
        {
            Be(b, FalloutProFile.SubtypeOffset, (uint)subtype);
            Be(b, FalloutProFile.SubtypedMaterialOffset, 3);
        }
        else if (type == FalloutProType.Wall)
        {
            Be(b, FalloutProFile.SubtypeOffset, 7);
        }

        return b;
    }

    private static void Be(byte[] bytes, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);
    }

    [Theory]
    [InlineData(FalloutProType.Item, 0, 129)]
    [InlineData(FalloutProType.Item, 3, 122)]
    [InlineData(FalloutProType.Item, 6, 61)]
    [InlineData(FalloutProType.Scenery, 0, 49)]
    [InlineData(FalloutProType.Scenery, 5, 45)]
    [InlineData(FalloutProType.Wall, -1, 36)]
    [InlineData(FalloutProType.Tile, -1, 28)]
    [InlineData(FalloutProType.Misc, -1, 28)]
    [InlineData(FalloutProType.Critter, -1, 416)]
    // internal, not public: the parameter type is an internal enum, and xUnit v3 discovers
    // non-public test methods (measured on 3.2.2). That keeps the enum internal instead of
    // widening a production API, or passing ints and casting, to suit a test.
    internal void ExpectedSize_MatchesTheRetailSizeTable(FalloutProType type, int subtype, int expected)
    {
        // Every one of these is a measured retail size, not a derivation: the seven item sizes and
        // two scenery sizes were found by asking which field predicts the record length.
        Assert.Equal(expected, FalloutProFile.ExpectedSize(type, subtype));
        Assert.Equal(expected, Proto(type, subtype).Length);
    }

    [Fact]
    public void Parse_AcceptsEitherCritterSizeBecauseFalloutTwoShipsBoth()
    {
        // ⚠ NOT a per-game constant. Fallout 2 grew the critter block to 416 (0x1A0), but two of
        // its own critters — 00000077.pro and 00000114.pro — are still Fallout 1's 412. A reader
        // that picked the size from the game would reject two files that game ships.
        Assert.Equal([416, 412], FalloutProFile.ExpectedSizes(FalloutProType.Critter, -1).ToArray());

        Assert.Equal(416, FalloutProFile.Parse(Proto(FalloutProType.Critter, size: 416), "A.PRO").Payload.Length + 32);
        Assert.Equal(FalloutProType.Critter, FalloutProFile.Parse(Proto(FalloutProType.Critter, size: 412), "B.PRO").Type);

        // A third length is still refused — the set is exactly two.
        Assert.Throws<InvalidDataException>(
            () => FalloutProFile.Parse(Proto(FalloutProType.Critter, size: 414), "C.PRO"));
    }

    [Fact]
    public void Parse_ReadsTheCommonHeaderBigEndian()
    {
        var pro = FalloutProFile.Parse(Proto(FalloutProType.Scenery, 5, textId: 3_400), "00000007.PRO");

        Assert.Equal(FalloutProType.Scenery, pro.Type);
        Assert.Equal(7, pro.ListIndex);
        Assert.Equal(3_400u, pro.TextId);
        Assert.Equal(3_401u, pro.DescriptionTextId);   // the description is always the name plus one
        Assert.Equal(2u, pro.LightDistance);
        Assert.Equal(32_768u, pro.LightIntensity);
        Assert.Equal(0x2000u, pro.Flags);
    }

    [Fact]
    public void Parse_ReadsTheArtReferenceWithTheSameTypeByteAsTheProto()
    {
        // Measured on 4,306/4,306: the FID's high byte equals the prototype's. A little-endian read
        // would put that byte at the far end and the agreement would vanish.
        var pro = FalloutProFile.Parse(Proto(FalloutProType.Wall), "W.PRO");

        Assert.Equal((uint)FalloutProType.Wall, pro.FrameId >> 24);
        Assert.Equal(pro.ProtoId >> 24, pro.FrameId >> 24);
    }

    [Fact]
    public void Parse_GivesATileNoExtendedFlagsAndNoScript()
    {
        // ⚠ THE trap: a tile is 28 bytes, so the extended-flags dword the other families carry at
        // +24 is the tile's MATERIAL, and there is no script field at all to read.
        var tile = FalloutProFile.Parse(Proto(FalloutProType.Tile), "T.PRO");

        Assert.Null(tile.ExtendedFlags);
        Assert.Null(tile.ScriptId);
        Assert.Equal(5u, tile.Material);
        Assert.False(tile.HasScript);
    }

    [Fact]
    public void Parse_ReadsTheMaterialFromWhereEachTypePutsIt()
    {
        Assert.Equal(7u, FalloutProFile.Parse(Proto(FalloutProType.Wall), "W.PRO").Material);
        Assert.Equal(3u, FalloutProFile.Parse(Proto(FalloutProType.Item, 4), "I.PRO").Material);
        Assert.Equal(3u, FalloutProFile.Parse(Proto(FalloutProType.Scenery, 0), "S.PRO").Material);
        Assert.Null(FalloutProFile.Parse(Proto(FalloutProType.Critter), "C.PRO").Material);
    }

    [Fact]
    public void Parse_TreatsMinusOneAsNoScript()
    {
        var pro = FalloutProFile.Parse(Proto(FalloutProType.Item, 5), "I.PRO");

        Assert.Equal(FalloutProFile.NoScript, pro.ScriptId);
        Assert.False(pro.HasScript);
    }

    [Fact]
    public void Parse_RejectsARecordOfTheWrongLengthForItsSubtype()
    {
        // A weapon truncated to an ammo's length still parses field-for-field; only the exact size
        // check catches it, which is why the gate is equality and not "long enough".
        var weapon = Proto(FalloutProType.Item, 3);
        var truncated = weapon[..81];

        var error = Assert.Throws<InvalidDataException>(() => FalloutProFile.Parse(truncated, "BAD.PRO"));
        Assert.Contains("must be 122 bytes, not 81", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsAnUnknownSubtypeAndAnUnknownType()
    {
        var bogusSubtype = Proto(FalloutProType.Item, 3);
        Be(bogusSubtype, FalloutProFile.SubtypeOffset, 9);
        Assert.Throws<InvalidDataException>(() => FalloutProFile.Parse(bogusSubtype, "BAD.PRO"));

        var bogusType = Proto(FalloutProType.Tile);
        Be(bogusType, 0, 0x0900_0001);
        Assert.Throws<InvalidDataException>(() => FalloutProFile.Parse(bogusType, "BAD.PRO"));
    }

    [Fact]
    public void ProtoList_ResolvesAPrototypeIdToTheFileItsLineNames()
    {
        // ⚠ The whole point of the indirection: prototype 2 is NOT 00000002.PRO here.
        var list = FalloutProList.Parse(
            Encoding.ASCII.GetBytes("00000024.pro\r\n00000002.pro\r\n\r\n00000003.pro\r\n"), "SCENERY.LST");

        Assert.Equal(3, list.Count);
        Assert.Equal("00000024.pro", list.Resolve(0x02000001));
        Assert.Equal("00000002.pro", list.Resolve(0x02000002));
        Assert.Null(list.Resolve(0x02000004));
        Assert.Null(list.Resolve(0x02000000));      // the numbering is 1-based
    }

    [Fact]
    public void ProtoList_StripsTheTrailingCommentsScriptsLstUses()
    {
        var list = FalloutProList.Parse("obj_dude.int   ; player script\nzcaves.int\n"u8.ToArray(), "SCRIPTS.LST");

        Assert.Equal(["obj_dude.int", "zcaves.int"], list.Names);
    }

    [Fact]
    public void ProtoList_PathForNamesTheDirectoryAndItsList()
    {
        Assert.Equal("PROTO/SCENERY/SCENERY.LST", FalloutProList.PathFor(FalloutProType.Scenery));
        Assert.Equal("CRITTERS", FalloutProList.DirectoryFor(FalloutProType.Critter));
    }
}
