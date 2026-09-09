using System.Text;
using BethesdaMultitool.Core.Formats.Daggerfall;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>RMB parsing on synthetic blocks: FLD header fields, sized sub-blocks, object sets and rejection.</summary>
public class DaggerfallRmbBlockTests
{
    private static byte[] TwoBuildingBlock()
    {
        var tavern = new DaggerfallBlockFixture.SubRecord(1024, 2048, 512, 0x0F, 7,
            new DaggerfallBlockFixture.BlockData(
                [new DaggerfallBlockFixture.Model(310, 6, 3, 10, 0, 20, 1024)],
                [new DaggerfallBlockFixture.Flat(5, 6, 7, DaggerfallBlockFixture_Texture(197, 5), 42, 1)],
                2,
                [new DaggerfallBlockFixture.Flat(1, 2, 3, DaggerfallBlockFixture_Texture(182, 3), 510, 0)],
                [new DaggerfallBlockFixture.Door(100, 0, 200, 512, -512, 4)]),
            new DaggerfallBlockFixture.BlockData(
                [new DaggerfallBlockFixture.Model(410, 0, 4, 0, 0, 0, 0)],
                []),
            0x96);
        var house = new DaggerfallBlockFixture.SubRecord(3072, 512, 0, 0x11, 2,
            new DaggerfallBlockFixture.BlockData([new DaggerfallBlockFixture.Model(450, 1, 4, 0, 0, 0, 0)], []),
            new DaggerfallBlockFixture.BlockData([], []));
        return DaggerfallBlockFixture.Rmb("WALLAA03", [tavern, house],
            [new DaggerfallBlockFixture.Model(4, 1, 0, 2000, 0, 2000, 256)],
            [new DaggerfallBlockFixture.Flat(9, 9, 9, DaggerfallBlockFixture_Texture(210, 1), 0, 2)],
            Enumerable.Range(0, 256).Select(i => (byte)(i == 0 ? 0xC5 : i % 64)).ToArray(),
            Enumerable.Range(0, 256).Select(i => (byte)(i == 17 ? 0x0B : 255)).ToArray(),
            Enumerable.Range(0, 4096).Select(i => (byte)(i == 100 ? 0x0F : 0)).ToArray());
    }

    private static ushort DaggerfallBlockFixture_Texture(int archive, int record)
    {
        return (ushort)((archive << 7) | record);
    }

    [Fact]
    public void Parse_ReadsHeaderSubBlocksAndLooseObjects()
    {
        var bytes = TwoBuildingBlock();

        var block = DaggerfallRmbBlock.Parse(bytes, "WALLAA03.RMB");

        Assert.Equal("WALLAA03.RMB", block.Name);
        Assert.Equal("WALLAA03", block.HeaderName);
        Assert.Equal("OTHER01", block.OtherNames[0]);
        Assert.Equal(string.Empty, block.OtherNames[1]);
        Assert.Equal(bytes.Length, block.ParsedLength);
        Assert.Equal(0x1000u + 5, block.Section2Unknown[5]);

        Assert.Equal(2, block.SubRecords.Count);
        var tavern = block.SubRecords[0];
        Assert.Equal((1024, 2048, 512), (tavern.XPos, tavern.ZPos, tavern.YRotation));
        Assert.Equal(DaggerfallBuildingType.Tavern, block.Buildings[0].BuildingType);
        Assert.Equal(7, block.Buildings[0].Quality);
        Assert.Equal(500, block.Buildings[0].FactionId);
        Assert.Equal((byte?)0x96, tavern.TrailingByte);
        Assert.Null(block.SubRecords[1].TrailingByte);
        Assert.Equal(tavern.DeclaredSize, block.BlockDataSizes[0]);

        var exterior = tavern.Exterior;
        Assert.Equal([-1, -2, -3, -4, -5, -6], exterior.HeaderUnknowns);
        var model = Assert.Single(exterior.Models);
        Assert.Equal(31006u, model.ModelId);
        Assert.Equal((310, 6, 3), (model.ObjectId1, model.ObjectId2, model.ObjectType));
        Assert.Equal((10, 0, 20), (model.XPos, model.YPos, model.ZPos));
        Assert.Equal((11, 1, 21), (model.XPos1, model.YPos1, model.ZPos1));
        Assert.Equal(1024, model.YRotation);
        Assert.Equal(0x5555, model.Unknown4);
        Assert.Equal(0x66666666u, model.Unknown5);

        var flat = Assert.Single(exterior.Flats);
        Assert.Equal((197, 5, 42, 1), (flat.TextureArchive, flat.TextureRecord, flat.FactionId, flat.Flags));
        Assert.Equal(2, exterior.Section3.Count);
        Assert.Equal(701, exterior.Section3[1].XPos);
        var person = Assert.Single(exterior.People);
        Assert.Equal((182, 3, 510), (person.TextureArchive, person.TextureRecord, person.FactionId));
        var door = Assert.Single(exterior.Doors);
        Assert.Equal((100, 200, 512, -512, 4, 0x77),
            (door.XPos, door.ZPos, door.YRotation, door.OpenRotation, door.DoorModelIndex, door.Unknown));

        Assert.Equal(41000u, Assert.Single(tavern.Interior.Models).ModelId);
        Assert.Empty(block.SubRecords[1].Interior.Models);

        Assert.Equal(401u, Assert.Single(block.Misc3dObjects).ModelId);
        Assert.Equal(210, Assert.Single(block.MiscFlats).TextureArchive);
        Assert.Equal([31006u, 41000u, 45001u, 401u], block.AllModels.Select(m => m.ModelId));
    }

    [Fact]
    public void Parse_DecodesGroundTilesSceneryAndAutoMap()
    {
        var block = DaggerfallRmbBlock.Parse(TwoBuildingBlock(), "WALLAA03.RMB");

        Assert.Equal("GRND", Encoding.ASCII.GetString(block.GroundHeader.Span[..4]));
        var corner = block.GroundTileAt(0, 0);
        Assert.Equal(0xC5, corner.Raw);
        Assert.Equal(5, corner.TextureRecord);
        Assert.True(corner.IsRotated);
        Assert.True(corner.IsFlipped);
        Assert.Equal(17 % 64, block.GroundTileAt(1, 1).TextureRecord);
        Assert.False(block.GroundTileAt(1, 1).IsRotated);

        var scenery = block.GroundScenery[17];
        Assert.True(scenery.HasScenery);
        Assert.Equal(0x0B / 4 - 1, scenery.TextureRecord);
        Assert.Equal(0x0B & 3, scenery.Unknown1);
        Assert.False(block.GroundScenery[0].HasScenery);
        Assert.Equal(-1, block.GroundScenery[0].TextureRecord);

        Assert.Equal(4096, block.AutoMap.Length);
        Assert.Equal(0x0F, block.AutoMap.Span[100]);
    }

    [Fact]
    public void Parse_RejectsMalformedBlocks()
    {
        var bytes = TwoBuildingBlock();

        Assert.Throws<InvalidDataException>(() => DaggerfallRmbBlock.Parse(bytes.AsMemory(0, 100), "X.RMB"));

        // A declared sub-block size larger than what remains.
        var oversized = (byte[])bytes.Clone();
        oversized[1603] = 0xFF;
        oversized[1604] = 0x7F;
        Assert.Throws<InvalidDataException>(() => DaggerfallRmbBlock.Parse(oversized, "X.RMB"));

        // Two trailing bytes after the interior set: retail leaves at most one.
        var declared = BitConverter.ToInt32(bytes, 1603);
        var padded = new List<byte>(bytes);
        padded.Insert(6776 + declared, 0x00);
        var paddedBytes = padded.ToArray();
        BitConverter.GetBytes(declared + 1).CopyTo(paddedBytes, 1603);
        Assert.Throws<InvalidDataException>(() => DaggerfallRmbBlock.Parse(paddedBytes, "X.RMB"));

        // A loose-model count the record cannot hold.
        var tooManyMisc = (byte[])bytes.Clone();
        tooManyMisc[1] = 200;
        Assert.Throws<InvalidDataException>(() => DaggerfallRmbBlock.Parse(tooManyMisc, "X.RMB"));
    }
}