using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Battlespire;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Battlespire;

/// <summary>
///     BS6 level parsing on a synthetic tree: the little-endian chunk lengths, group nesting, the
///     mesh list and the placements that index it.
/// </summary>
public class Bs6FileTests
{
    private static byte[] Chunk(string tag, params byte[] payload)
    {
        var header = new byte[8];
        Encoding.ASCII.GetBytes(tag.PadRight(4)).CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)payload.Length);
        return [.. header, .. payload];
    }

    private static byte[] Int32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Vector(int x, int y, int z)
    {
        return [.. Int32(x), .. Int32(y), .. Int32(z)];
    }

    private static byte[] NameList(params string[] names)
    {
        var bytes = new byte[names.Length * Bs6File.NameLength];
        for (var i = 0; i < names.Length; i++)
        {
            Encoding.ASCII.GetBytes(names[i]).CopyTo(bytes, i * Bs6File.NameLength);
        }

        return bytes;
    }

    private static byte[] Level()
    {
        byte[] objects =
        [
            .. Chunk("LFIL", NameList("7arch", "potion", "7torch")),
            .. Chunk("OBJD",
            [
                .. Chunk("IDNB", Int32(0)), .. Chunk("IDFI", Int32(1)), .. Chunk("POSI", Vector(608, -2576, -4480)),
                .. Chunk("ANGS", Vector(0, 1024, 0)), .. Chunk("SELE", Int32(0))
            ]),
            .. Chunk("OBJD",
            [
                .. Chunk("IDNB", Int32(1)), .. Chunk("IDFI", Int32(2)), .. Chunk("POSI", Vector(1, 2, 3)),
                .. Chunk("ANGS", Vector(0, 0, 0)), .. Chunk("SELE", Int32(1))
            ]),

            // A placement that indexes past the list, as two retail placements do.
            .. Chunk("OBJD",
            [
                .. Chunk("IDNB", Int32(2)), .. Chunk("IDFI", Int32(9)), .. Chunk("POSI", Vector(0, 0, 0)),
                .. Chunk("ANGS", Vector(0, 0, 0)), .. Chunk("SELE", Int32(0))
            ])
        ];

        byte[] lights =
        [
            .. Chunk("LITD",
            [
                .. Chunk("IDNB", Int32(7)), .. Chunk("POSI", Vector(9, 8, 7)), .. Chunk("BRIT", Int32(63)),
                .. Chunk("RADI", Int32(512))
            ])
        ];

        byte[] flats =
        [
            .. Chunk("FLAD",
            [
                .. Chunk("IDNB", Int32(3)), .. Chunk("FILN", [.. "monster1"u8, 0]), .. Chunk("POSI", Vector(4, 5, 6)),
                .. Chunk("SCAL", Int32(256))
            ])
        ];

        byte[] root =
        [
            .. Chunk("TEXI", Chunk("DIRN", [.. "e:\\projects\\batspire\\art"u8, 0])),
            .. Chunk("BITS", Int32(0x0F)),
            .. Chunk("BBOX", [.. Vector(-6750, -807651, -9948), .. Vector(6736, -297, 1003)]),
            .. Chunk("RADI", Int32(403770)),
            .. Chunk("CENT", Vector(-7, -403974, -4472)),
            .. Chunk("SNAP", Chunk("IDTY", Int32(1))),
            .. Chunk("VIEW", Chunk("IDNB", Int32(0))),
            .. Chunk("VIEW", Chunk("IDNB", Int32(1))),
            .. Chunk("WATR", Int32(0)),
            .. Chunk("OBJS", objects),
            .. Chunk("LITS", lights),
            .. Chunk("FLAS", flats)
        ];

        return Chunk("GNRL", root);
    }

    [Fact]
    public void Parse_ReadsTheMeshListAndItsPlacements()
    {
        var level = Bs6File.Parse(Level(), "L8.BS6");

        Assert.Equal("L8.BS6", level.Name);
        Assert.Equal("GNRL", level.Root.Tag);
        Assert.Equal(["7arch", "potion", "7torch"], level.MeshNames);

        Assert.Equal(3, level.Objects.Count);
        Assert.Equal(new Bs6Object(0, 1, new Bs6Vector(608, -2576, -4480), new Bs6Vector(0, 1024, 0), 0),
            level.Objects[0]);
        Assert.Equal("potion", level.MeshNames[level.Objects[0].MeshIndex]);
        Assert.Equal("7torch", level.MeshNames[level.Objects[1].MeshIndex]);

        // The third placement indexes past the list; the reader reports it rather than guessing.
        Assert.Equal(9, level.Objects[2].MeshIndex);
        Assert.True(level.Objects[2].MeshIndex >= level.MeshNames.Count);
    }

    [Fact]
    public void Parse_ReadsHeaderFieldsLightsAndFlats()
    {
        var level = Bs6File.Parse(Level(), "L8.BS6");

        Assert.Equal(403_770, level.Radius);
        Assert.Equal(0, level.Water);
        Assert.Equal(0x0F, level.Bits);
        Assert.Equal(new Bs6Vector(-7, -403_974, -4_472), level.Center);
        Assert.Equal("e:\\projects\\batspire\\art", level.TextureDirectory);
        Assert.Equal(2, level.ViewCount);
        Assert.Equal(1, level.SnapCount);

        var box = Assert.NotNull(level.BoundingBox);
        Assert.Equal(new Bs6Vector(-6750, -807_651, -9948), box.Min);
        Assert.Equal(new Bs6Vector(6736, -297, 1003), box.Max);

        var light = Assert.Single(level.Lights);
        Assert.Equal(new Bs6Light(7, new Bs6Vector(9, 8, 7), 63, 512), light);

        var flat = Assert.Single(level.Flats);
        Assert.Equal(new Bs6Flat(3, "monster1", new Bs6Vector(4, 5, 6), 256), flat);
    }

    [Fact]
    public void Root_ExposesTheWholeTree()
    {
        var level = Bs6File.Parse(Level(), "L8.BS6");

        // Groups nest; leaves do not.
        var objects = Assert.Single(level.Root.Children, c => c.Tag == "OBJS");
        Assert.Equal(4, objects.Children.Count);
        Assert.Empty(objects.Children[0].Children);

        Assert.Equal("DIRN", level.Root.Find("DIRN")!.Tag);
        Assert.Null(level.Root.Find("NOPE"));
        Assert.True(level.Root.Descend().Count() > 30);
    }

    [Theory]
    [InlineData("L8.BS6", true)]
    [InlineData("dmnn.bs6", true)]
    [InlineData("BS6.BSA", false)]
    public void IsBs6FileName_UsesTheExtension(string name, bool expected)
    {
        Assert.Equal(expected, Bs6File.IsBs6FileName(name));
    }

    [Fact]
    public void Parse_RejectsWhatIsNotALevel()
    {
        Assert.Throws<InvalidDataException>(() => Bs6File.Parse("8272e223"u8.ToArray(), "ADR.TXT"));

        // A chunk length that runs past its parent, like the truncated retail entry named "C".
        var level = Level();
        BinaryPrimitives.WriteUInt32LittleEndian(level.AsSpan(4), 0x4000_0000);
        Assert.Throws<InvalidDataException>(() => Bs6File.Parse(level, "C"));

        // A file whose first chunk is not GNRL.
        Assert.Throws<InvalidDataException>(() => Bs6File.Parse(Chunk("OBJS"), "X.BS6"));
    }
}