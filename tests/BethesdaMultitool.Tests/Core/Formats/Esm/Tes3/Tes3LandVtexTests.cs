using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Tes3;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Tes3;

/// <summary>
///     Morrowind LAND VTEX is stored as 16 sub-blocks of 4×4 (OpenMW's transposeTextureData), NOT
///     row-major. Reading it row-major scatters each vertex's land texture and the terrain renders as
///     per-cell stippled noise. These cover the de-swizzle in <see cref="Tes3LandParser" />.
/// </summary>
public class Tes3LandVtexTests
{
    [Fact]
    public void Parse_DeswizzlesVtex_From4x4BlockOrderToRowMajor()
    {
        // Storage filled sequentially 0..255; after de-swizzle, storage position p (readPos) lands at
        // grid (x = x1*4 + x2, y = y1*4 + y2) where p = ((y1*4 + x1)*4 + y2)*4 + x2.
        var land = BuildLandWithSequentialVtex();
        var draft = Tes3LandParser.Parse(land, land.Length);

        Assert.NotNull(draft);
        var tex = draft!.TextureIndices;
        Assert.NotNull(tex);
        const int size = 16;

        Assert.Equal(0, tex![0 * size + 0]); // readPos 0  → (0,0)
        Assert.Equal(1, tex[0 * size + 1]); // readPos 1  → (1,0)
        Assert.Equal(3, tex[0 * size + 3]); // readPos 3  → (3,0)
        Assert.Equal(4, tex[1 * size + 0]); // readPos 4  → (0,1)  (next inner row of block 0)
        Assert.Equal(16, tex[0 * size + 4]); // readPos 16 → (4,0)  (next outer block, x1=1)
        Assert.Equal(255, tex[15 * size + 15]); // readPos 255 → (15,15)
    }

    [Fact]
    public void Parse_DeswizzledVtex_IsAPermutationOfAllStorageValues()
    {
        var land = BuildLandWithSequentialVtex();
        var tex = Tes3LandParser.Parse(land, land.Length)!.TextureIndices!;

        var seen = new HashSet<int>();
        foreach (var v in tex)
        {
            seen.Add(v);
        }

        Assert.Equal(256, seen.Count); // every storage value 0..255 appears exactly once
    }

    private static byte[] BuildLandWithSequentialVtex()
    {
        var buf = new List<byte>();
        var grid = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(grid, 7);
        BinaryPrimitives.WriteInt32LittleEndian(grid.AsSpan(4), -3);
        AppendSub(buf, "INTV", grid);

        var vtex = new byte[256 * 2];
        for (var i = 0; i < 256; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(vtex.AsSpan(i * 2), (ushort)i);
        }

        AppendSub(buf, "VTEX", vtex);
        return [.. buf];
    }

    private static void AppendSub(List<byte> buf, string signature, byte[] data)
    {
        buf.AddRange(Encoding.ASCII.GetBytes(signature)); // 4-char signature
        var size = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)data.Length); // TES3: 4-byte subrecord size
        buf.AddRange(size);
        buf.AddRange(data);
    }
}