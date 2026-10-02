using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Trims the all-zero mip levels a DDX relayout fabricates beyond the DDX header's declared count (design section 5.1:
///     "about 52 single-level UI textures per build receive all-zero mip levels the header does not declare; no data is
///     lost"), so the relayout's mip count can be compared with the declaration. Only levels past the declared count, and
///     any bytes after the last stored level, are ever removed, and only when every one of those bytes is zero; otherwise
///     nothing is trimmed and the non-zero levels are counted, which fails the gate.
/// </summary>
internal static class NifDdsMipTrim
{
    private const uint MipMapCountFlag = 0x20000;
    private const uint MipMapCaps = 0x400000 | 0x8;

    /// <summary>Trims fabricated zero levels beyond <paramref name="declaredLevels" />.</summary>
    /// <param name="dds">A DDS produced by the relayout.</param>
    /// <param name="declaredLevels">The DDX header's declared mip count.</param>
    /// <returns>The (possibly new) bytes and what was trimmed.</returns>
    public static NifDdsTrimResult Trim(byte[] dds, int declaredLevels)
    {
        ArgumentNullException.ThrowIfNull(dds);
        if (!NifDdsHeader.TryRead(dds, out var header))
        {
            return new NifDdsTrimResult(dds, 0, 0, 0, 0, 0, "the relayout output is not a readable DDS header");
        }

        var stored = header.Levels;
        if (stored <= declaredLevels || declaredLevels < 1)
        {
            return new NifDdsTrimResult(dds, stored, stored, 0, 0, 0, null);
        }

        if (header.BlockBytes is not { } blockBytes || header.IsCube || header.IsVolume || header.ArraySize > 1)
        {
            return new NifDdsTrimResult(dds, stored, stored, 0, 0, stored - declaredLevels,
                "the extra levels cannot be located (not a single block-compressed surface)");
        }

        long position = header.DataOffset;
        long keepEnd = -1;
        var nonZero = 0;
        for (var level = 0; level < stored; level++)
        {
            var size = NifDdsHeader.LevelBytes(header.Width, header.Height, level, blockBytes);
            if (level == declaredLevels)
            {
                keepEnd = position;
            }

            if (level >= declaredLevels)
            {
                var end = Math.Min(position + size, dds.Length);
                if (position + size > dds.Length || AnyNonZero(dds, position, end))
                {
                    nonZero++;
                }
            }

            position += size;
        }

        if (position < dds.Length && AnyNonZero(dds, position, dds.Length))
        {
            nonZero++;
        }

        if (nonZero > 0 || keepEnd < header.DataOffset)
        {
            return new NifDdsTrimResult(dds, stored, stored, 0, 0, nonZero, null);
        }

        var trimmed = dds.AsSpan(0, (int)keepEnd).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(trimmed.AsSpan(28), (uint)declaredLevels);
        if (declaredLevels == 1)
        {
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(trimmed.AsSpan(8)) & ~MipMapCountFlag;
            BinaryPrimitives.WriteUInt32LittleEndian(trimmed.AsSpan(8), flags);
            var caps = BinaryPrimitives.ReadUInt32LittleEndian(trimmed.AsSpan(108)) & ~MipMapCaps;
            BinaryPrimitives.WriteUInt32LittleEndian(trimmed.AsSpan(108), caps);
        }

        return new NifDdsTrimResult(trimmed, stored, declaredLevels, stored - declaredLevels, dds.Length - keepEnd, 0,
            null);
    }

    private static bool AnyNonZero(byte[] bytes, long start, long end)
    {
        return end > start && bytes.AsSpan((int)start, (int)(end - start)).ContainsAnyExcept((byte)0);
    }
}
