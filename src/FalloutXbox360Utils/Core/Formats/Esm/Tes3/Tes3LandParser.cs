using System.Buffers.Binary;

namespace FalloutXbox360Utils.Core.Formats.Esm.Tes3;

/// <summary>
///     Decoded Morrowind LAND record: a 65×65 heightmap + a 16×16 land-texture index grid + optional
///     65×65 vertex colors, keyed by exterior cell grid coordinates. Heights are absolute world units
///     (the VHGT delta chain already resolved and ×8-scaled). The viewer's terrain pipeline is 33×33,
///     so <see cref="DownsampleHeights" /> / <see cref="DownsampleColors" /> reduce to that grid.
/// </summary>
internal sealed class Tes3LandDraft
{
    public const int Size = 65; // Morrowind LAND is 65×65 vertices
    public const int VtexSize = 16; // 16×16 land-texture index grid
    public const float HeightScale = 8f;

    public int GridX { get; set; }
    public int GridY { get; set; }
    public float[,]? Heights { get; set; } // [Size, Size], absolute world Z
    public ushort[]? TextureIndices { get; set; } // 16×16 = 256 land-texture indices (0 = default)
    public byte[]? VertexColors { get; set; } // 65×65×3 RGB

    /// <summary>Reduce the native 65×65 heights to the viewer's 33×33 grid (every other vertex).</summary>
    public float[,]? DownsampleHeights()
    {
        if (Heights is null)
        {
            return null;
        }

        var outGrid = new float[33, 33];
        for (var j = 0; j < 33; j++)
        {
            for (var i = 0; i < 33; i++)
            {
                outGrid[j, i] = Heights[j * 2, i * 2];
            }
        }

        return outGrid;
    }

    /// <summary>Reduce the native 65×65×3 vertex colors to 33×33×3 (3267 bytes, the viewer's shape).</summary>
    public byte[]? DownsampleColors()
    {
        if (VertexColors is not { Length: Size * Size * 3 })
        {
            return null;
        }

        var outColors = new byte[33 * 33 * 3];
        for (var j = 0; j < 33; j++)
        {
            for (var i = 0; i < 33; i++)
            {
                var src = (j * 2 * Size + i * 2) * 3;
                var dst = (j * 33 + i) * 3;
                outColors[dst] = VertexColors[src];
                outColors[dst + 1] = VertexColors[src + 1];
                outColors[dst + 2] = VertexColors[src + 2];
            }
        }

        return outColors;
    }
}

/// <summary>
///     Parses a Morrowind LAND record's subrecords. INTV gives the exterior cell grid; VHGT is the
///     delta-encoded heightmap (float offset + 65×65 signed-byte deltas, accumulated down the first
///     column then across each row, ×8); VTEX is a 16×16 grid of land-texture indices (1-based into
///     LTEX, 0 = default); VCLR is per-vertex RGB. Layout per the openMW / UESP TES3 docs.
/// </summary>
internal static class Tes3LandParser
{
    public static Tes3LandDraft? Parse(byte[] data, int dataSize)
    {
        var draft = new Tes3LandDraft();
        var haveGrid = false;

        foreach (var sub in Tes3SubrecordUtils.IterateSubrecords(data, dataSize))
        {
            var span = data.AsSpan(sub.DataOffset, sub.DataLength);
            switch (sub.Signature)
            {
                case "INTV" when span.Length >= 8:
                    draft.GridX = BinaryPrimitives.ReadInt32LittleEndian(span);
                    draft.GridY = BinaryPrimitives.ReadInt32LittleEndian(span[4..]);
                    haveGrid = true;
                    break;
                case "VHGT" when span.Length >= 4 + Tes3LandDraft.Size * Tes3LandDraft.Size:
                    draft.Heights = DecodeVhgt(span);
                    break;
                case "VTEX" when span.Length >= Tes3LandDraft.VtexSize * Tes3LandDraft.VtexSize * 2:
                    draft.TextureIndices = DecodeVtex(span);
                    break;
                case "VCLR" when span.Length >= Tes3LandDraft.Size * Tes3LandDraft.Size * 3:
                    draft.VertexColors = span[..(Tes3LandDraft.Size * Tes3LandDraft.Size * 3)].ToArray();
                    break;
            }
        }

        return haveGrid ? draft : null;
    }

    private static float[,] DecodeVhgt(ReadOnlySpan<byte> span)
    {
        const int n = Tes3LandDraft.Size;
        var offset = BinaryPrimitives.ReadSingleLittleEndian(span);
        var deltas = span[4..]; // n*n signed bytes follow

        var heights = new float[n, n];
        var rowOffset = offset;
        for (var y = 0; y < n; y++)
        {
            rowOffset += (sbyte)deltas[y * n]; // first-column delta accumulates down rows
            var colValue = rowOffset;
            heights[y, 0] = colValue * Tes3LandDraft.HeightScale;
            for (var x = 1; x < n; x++)
            {
                colValue += (sbyte)deltas[y * n + x];
                heights[y, x] = colValue * Tes3LandDraft.HeightScale;
            }
        }

        return heights;
    }

    private static ushort[] DecodeVtex(ReadOnlySpan<byte> span)
    {
        const int count = Tes3LandDraft.VtexSize * Tes3LandDraft.VtexSize;
        var indices = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            indices[i] = BinaryPrimitives.ReadUInt16LittleEndian(span[(i * 2)..]);
        }

        return indices;
    }
}
