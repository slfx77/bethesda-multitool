using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BethesdaMultitool.Core.Formats.Daggerfall;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>RDB parsing on synthetic blocks: header, reference tables, linked object lists, resources and actions.</summary>
public class DaggerfallRdbBlockTests
{
    private static byte[] TwoByTwo()
    {
        return DaggerfallBlockFixture.Rdb(2, 2,
            [("72100", "DOR"), ("58051", "WAL"), ("junk", "")],
            new Dictionary<int, IReadOnlyList<DaggerfallBlockFixture.RdbObject>>
            {
                [0] =
                [
                    new DaggerfallBlockFixture.RdbObject(1, 640, -128, 512, ModelIndex: 0, YRotation: -512, ActionNextObject: 2),
                    new DaggerfallBlockFixture.RdbObject(2, 700, -200, 600, Radius: 900),
                    new DaggerfallBlockFixture.RdbObject(3, 800, 0, 700, TextureBits: (ushort)((208 << 7) | 4))
                ],
                [3] = [new DaggerfallBlockFixture.RdbObject(1, 1, 2, 3, ModelIndex: 1)]
            });
    }

    [Fact]
    public void Parse_ReadsHeaderReferencesAndCells()
    {
        var block = DaggerfallRdbBlock.Parse(TwoByTwo(), "N0000071.RDB");

        Assert.Equal(DaggerfallRdbType.Normal, block.Type);
        Assert.Equal((2, 2), (block.Width, block.Height));
        Assert.Equal(0xDEAD0001u, block.Unknown1);
        Assert.Equal(0xDEAD0002u, block.Unknown2);
        Assert.Equal(750, block.ModelReferences.Count);
        Assert.Equal(("72100", 72100u, "DOR"), (block.ModelReferences[0].ModelId, block.ModelReferences[0].ModelIdNumber, block.ModelReferences[0].Description));
        Assert.Null(block.ModelReferences[2].ModelIdNumber);
        Assert.Equal("junk", block.ModelReferences[2].ModelId);
        Assert.Equal(749u, block.ModelData[749]);
        Assert.Equal("DAGR", block.ObjectHeader.Dagr);

        Assert.Equal(4, block.ObjectRoots.Count);
        Assert.Equal(3, block.ObjectRoots[0].Objects.Count);
        Assert.Empty(block.ObjectRoots[1].Objects);
        Assert.Equal(-1, block.ObjectRoots[1].RootOffset);
        Assert.Single(block.ObjectRoots[3].Objects);
        Assert.Equal(4, block.AllObjects.Count());

        // The header's linked list: two nodes, the second terminal.
        Assert.Equal(2, block.UnknownObjects.Count);
        Assert.Equal(2, block.UnknownObjects[0].Index);
        Assert.Equal(-1, block.UnknownObjects[1].Next);
    }

    [Fact]
    public void Parse_ReadsResourcesAndLinksActions()
    {
        var block = DaggerfallRdbBlock.Parse(TwoByTwo(), "W0000006.RDB");
        var cell = block.ObjectRoots[0].Objects;

        var door = cell[0];
        Assert.Equal(DaggerfallRdbResourceType.Model, door.Type);
        Assert.Equal((640, -128, 512), (door.XPos, door.YPos, door.ZPos));
        Assert.Equal(-1, door.Previous);
        Assert.Equal(cell[1].Position, door.Next);
        Assert.NotNull(door.Model);
        Assert.Equal((100, -512, 300), (door.Model.XRotation, door.Model.YRotation, door.Model.ZRotation));
        Assert.Equal(0, door.Model.ModelIndex);
        Assert.Equal(4u, door.Model.TriggerFlagStartingLock);
        Assert.Equal(9, door.Model.SoundIndex);
        Assert.NotNull(door.Model.Action);
        Assert.Equal((2, 30, 64, 1), (door.Model.Action.Axis, door.Model.Action.Duration, door.Model.Action.Magnitude, door.Model.Action.Flags));
        Assert.Equal(cell[2].Position, door.Model.Action.NextObjectOffset);
        Assert.Equal(2, door.Model.Action.NextObjectIndex);

        var light = cell[1];
        Assert.Equal(DaggerfallRdbResourceType.Light, light.Type);
        var lightResource = Assert.IsType<DaggerfallRdbLightResource>(light.Light);
        Assert.Equal((7u, 8u, 900), (lightResource.Unknown1, lightResource.Unknown2, lightResource.Radius));
        Assert.Null(light.Model);

        var flat = cell[2];
        Assert.Equal(DaggerfallRdbResourceType.Flat, flat.Type);
        Assert.Equal(-1, flat.Next);
        var flatResource = Assert.IsType<DaggerfallRdbFlatResource>(flat.Flat);
        Assert.Equal((208, 4), (flatResource.TextureArchive, flatResource.TextureRecord));
        Assert.Equal(0x0102, flatResource.Flags);
        Assert.Equal(0x1234, flatResource.FactionOrMobileId);
        Assert.Equal(5, flatResource.Action);

        Assert.Equal(DaggerfallRdbType.Wet, block.Type);
        Assert.Null(Assert.IsType<DaggerfallRdbModelResource>(block.ObjectRoots[3].Objects[0].Model).Action);
    }

    // The enum is internal, so the theory takes its numeric value
    // (Unknown 0, Border 1, Wet 2, Quest 3, Mausoleum 4, Normal 5).
    [Theory]
    [InlineData("B0000000.RDB", 1)]
    [InlineData("w0000009.rdb", 2)]
    [InlineData("S0000021.RDB", 3)]
    [InlineData("M0000001.RDB", 4)]
    [InlineData("N0000071.RDB", 5)]
    [InlineData("X0000001.RDB", 0)]
    [InlineData("", 0)]
    public void TypeOf_UsesTheFirstLetter(string name, int expected)
    {
        Assert.Equal((DaggerfallRdbType)expected, DaggerfallRdbBlock.TypeOf(name));
    }

    [Fact]
    public void Parse_RejectsMalformedBlocks()
    {
        var bytes = TwoByTwo();

        Assert.Throws<InvalidDataException>(() => DaggerfallRdbBlock.Parse(bytes.AsMemory(0, 5000), "X.RDB"));

        // The root offset must be exactly where the fixed part ends.
        var wrongRoot = (byte[])bytes.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(wrongRoot.AsSpan(12), 9536);
        Assert.Throws<InvalidDataException>(() => DaggerfallRdbBlock.Parse(wrongRoot, "X.RDB"));

        // A node whose next pointer loops back to itself.
        var looping = (byte[])bytes.Clone();
        var root = BinaryPrimitives.ReadInt32LittleEndian(looping.AsSpan(9532));
        BinaryPrimitives.WriteInt32LittleEndian(looping.AsSpan(root), root);
        Assert.Throws<InvalidDataException>(() => DaggerfallRdbBlock.Parse(looping, "X.RDB"));

        // An unknown resource type.
        var badType = (byte[])bytes.Clone();
        badType[root + 20] = 9;
        Assert.Throws<InvalidDataException>(() => DaggerfallRdbBlock.Parse(badType, "X.RDB"));

        // A resource offset past the record.
        var badResource = (byte[])bytes.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(badResource.AsSpan(root + 21), badResource.Length - 4);
        Assert.Throws<InvalidDataException>(() => DaggerfallRdbBlock.Parse(badResource, "X.RDB"));
    }
}
