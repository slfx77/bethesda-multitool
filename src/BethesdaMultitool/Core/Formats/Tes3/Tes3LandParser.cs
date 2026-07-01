using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm;

namespace BethesdaMultitool.Core.Formats.Tes3;

/// <summary>
///     Decoded Morrowind LAND record: a 65×65 heightmap + a 16×16 land-texture index grid + optional
///     65×65 vertex colors, keyed by exterior cell grid coordinates. Heights are absolute world units
///     (the VHGT delta chain already resolved and ×8-scaled). The terrain pipeline is grid-size-aware,
///     so these are consumed at native 65×65 resolution (the 2D map downsamples internally).
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
        // Per the TES3 LAND format (UESP "Tes3Mod:File Format / LAND"), VTEX is NOT a simple row-major
        // 16×16 grid: it is stored as 16 sub-blocks of 4×4, streamed in (outer block row y1, outer block
        // col x1, inner row y2, inner col x2) order, so storage position p maps to grid
        // (x = x1*4 + x2, y = y1*4 + y2). Reading it row-major scatters each vertex's land texture and the
        // terrain renders as per-cell stippled noise. De-swizzle to true spatial row-major order so the
        // resolved VtexTextureFormIds / TextureWinnerGrid line up with the heightmap.
        const int size = Tes3LandDraft.VtexSize; // 16
        var indices = new ushort[size * size];
        var readPos = 0;
        for (var y1 = 0; y1 < 4; y1++)
        {
            for (var x1 = 0; x1 < 4; x1++)
            {
                for (var y2 = 0; y2 < 4; y2++)
                {
                    for (var x2 = 0; x2 < 4; x2++)
                    {
                        var x = x1 * 4 + x2;
                        var y = y1 * 4 + y2;
                        indices[y * size + x] = BinaryPrimitives.ReadUInt16LittleEndian(span[(readPos * 2)..]);
                        readPos++;
                    }
                }
            }
        }

        return indices;
    }
}
