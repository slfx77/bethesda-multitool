using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     A lossless view of one NiBSplineData block (nif.xml: Num Float Control Points, the floats, Num Compact Control
///     Points, the signed shorts): every float control point as its raw bits and every compact control point as its raw
///     Int16, neither dequantized nor resolved through a handle. Read by <see cref="NifBsplineTransformReader.TryReadDataView" />.
/// </summary>
/// <remarks>
///     Exact consumption of the block is reported (<see cref="ConsumedExactly" />), not required. The view references the
///     caller's buffer and must not outlive it.
/// </remarks>
internal readonly struct NifBsplineDataView
{
    private readonly byte[]? _data;

    /// <summary>Creates a view over arrays already bounds-checked by <see cref="NifBsplineTransformReader" />.</summary>
    internal NifBsplineDataView(
        byte[] data,
        bool bigEndian,
        int floatControlPointsOffset,
        int floatControlPointCount,
        int compactControlPointsOffset,
        int compactControlPointCount,
        int blockEnd)
    {
        _data = data;
        BigEndian = bigEndian;
        FloatControlPointsOffset = floatControlPointsOffset;
        FloatControlPointCount = floatControlPointCount;
        CompactControlPointsOffset = compactControlPointsOffset;
        CompactControlPointCount = compactControlPointCount;
        BlockEnd = blockEnd;
    }

    /// <summary>True when the file is big-endian.</summary>
    public bool BigEndian { get; }

    /// <summary>The absolute offset of the first float control point.</summary>
    public int FloatControlPointsOffset { get; }

    /// <summary>The stored Num Float Control Points.</summary>
    public int FloatControlPointCount { get; }

    /// <summary>The absolute offset of the first compact control point.</summary>
    public int CompactControlPointsOffset { get; }

    /// <summary>The stored Num Compact Control Points.</summary>
    public int CompactControlPointCount { get; }

    /// <summary>The absolute offset one past the block.</summary>
    public int BlockEnd { get; }

    /// <summary>True when the compact array ends exactly where the block ends.</summary>
    public bool ConsumedExactly =>
        CompactControlPointsOffset + CompactControlPointCount * sizeof(short) == BlockEnd;

    private byte[] Data => _data ?? throw new InvalidOperationException("The B-spline data view is empty.");

    /// <summary>The raw bits of one float control point.</summary>
    public uint FloatControlPointBits(int index)
    {
        if ((uint)index >= (uint)FloatControlPointCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index,
                $"The block holds {FloatControlPointCount} float control points.");
        }

        return BinaryUtils.ReadUInt32(Data, FloatControlPointsOffset + index * sizeof(float), BigEndian);
    }

    /// <summary>One compact control point as stored (a signed short, scaled by 32767 at decode).</summary>
    public short CompactControlPoint(int index)
    {
        if ((uint)index >= (uint)CompactControlPointCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index,
                $"The block holds {CompactControlPointCount} compact control points.");
        }

        return BinaryUtils.ReadInt16(Data, CompactControlPointsOffset + index * sizeof(short), BigEndian);
    }
}
