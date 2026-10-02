using System.Diagnostics.CodeAnalysis;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Cut-1b slice 7: the lossless view readers of the morph blocks the model reader binds (NiGeomMorpherController,
///     the frame table of NiMorphData, NiFloatInterpolator), in the slice-1 style of
///     <see cref="NifControllerSequenceNameTrackReader" />: every field as its raw bits, read in the file's byte order,
///     bounded by the block span, validated no further. NiFloatData is read by
///     <see cref="NifKeyGroupReader.TryReadDataBlockView" /> and the float B-spline interpolators by
///     <see cref="NifBsplineTransformReader.TryReadInterpolatorView" />.
/// </summary>
/// <remarks>
///     Since cut-1b slice 10 (the owner's D5 follow-up) the renderer's <see cref="NifGeometryMorphReader" /> reads the
///     same blocks through these views and keeps only its own admission rules on top (little-endian BS 34 only, flags 72
///     or 76, LINEAR and QUADRATIC keys), so the renderer and the model reader share one decode path.
/// </remarks>
internal static class NifGeomMorpherReader
{
    /// <summary>The bytes of an NiGeomMorpherController before its items at 20.2.0.7 (header 26, flags 2, data 4, always update 1, count 4).</summary>
    public const int ControllerPrefixSize = NifTimeControllerHeader.HeaderSize + 11;

    /// <summary>The bytes of one Interpolator Weights item (ref and float).</summary>
    public const int ItemSize = 8;

    /// <summary>The bytes of an NiMorphData before its morphs (Num Morphs, Num Vertices, Relative Targets).</summary>
    public const int MorphDataPrefixSize = 9;

    /// <summary>The bytes of an NiFloatInterpolator (Value and Data).</summary>
    public const int FloatInterpolatorSize = 8;

    private const int MorpherFlagsOffset = NifTimeControllerHeader.HeaderSize;
    private const int DataRefOffset = MorpherFlagsOffset + 2;
    private const int AlwaysUpdateOffset = DataRefOffset + 4;
    private const int CountOffset = AlwaysUpdateOffset + 1;
    private const uint MaximumItems = 1 << 16;

    /// <summary>Reads a 20.2.0.7 NiGeomMorpherController losslessly.</summary>
    /// <param name="data">The file bytes.</param>
    /// <param name="nif">NifParser's header and block table.</param>
    /// <param name="block">The controller block.</param>
    /// <param name="view">The view.</param>
    /// <returns>
    ///     False when the block is not an NiGeomMorpherController, the stream is not 20.2.0.7, or the prefix and the
    ///     declared items do not fit the block.
    /// </returns>
    public static bool TryReadControllerView(
        byte[] data, NifInfo nif, BlockInfo block, [NotNullWhen(true)] out NifGeomMorpherControllerView? view)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(nif);
        ArgumentNullException.ThrowIfNull(block);
        view = null;
        if (block.TypeName != "NiGeomMorpherController" || nif.BinaryVersion != NifVersions.Gamebryo202007 ||
            !HasReadableSpan(data, block, ControllerPrefixSize) ||
            !NifTimeControllerReader.TryRead(data, block, nif.IsBigEndian, out var header))
        {
            return false;
        }

        var be = nif.IsBigEndian;
        var start = block.DataOffset;
        var morpherFlags = BinaryUtils.ReadUInt16(data, start + MorpherFlagsOffset, be);
        var dataRef = BinaryUtils.ReadInt32(data, start + DataRefOffset, be);
        var alwaysUpdate = data[start + AlwaysUpdateOffset];
        var count = BinaryUtils.ReadUInt32(data, start + CountOffset, be);
        if (count > MaximumItems || ControllerPrefixSize + (long)count * ItemSize > block.Size)
        {
            return false;
        }

        var items = new NifMorphWeightView[(int)count];
        var position = start + ControllerPrefixSize;
        for (var index = 0; index < items.Length; index++)
        {
            items[index] = new NifMorphWeightView(
                BinaryUtils.ReadInt32(data, position, be),
                BinaryUtils.ReadUInt32(data, position + 4, be));
            position += ItemSize;
        }

        view = new NifGeomMorpherControllerView(header, morpherFlags, dataRef, alwaysUpdate, items,
            position == start + block.Size);
        return true;
    }

    /// <summary>Reads the frame table of a 20.2.0.7 NiMorphData losslessly, walking each morph's vectors for their extent.</summary>
    /// <param name="data">The file bytes.</param>
    /// <param name="nif">NifParser's header and block table.</param>
    /// <param name="block">The morph data block.</param>
    /// <param name="view">The view.</param>
    /// <returns>False when the block is not an NiMorphData, the stream is not 20.2.0.7, or the morphs do not fit the block.</returns>
    public static bool TryReadMorphDataView(
        byte[] data, NifInfo nif, BlockInfo block, [NotNullWhen(true)] out NifMorphDataView? view)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(nif);
        ArgumentNullException.ThrowIfNull(block);
        view = null;
        if (block.TypeName != "NiMorphData" || nif.BinaryVersion != NifVersions.Gamebryo202007 ||
            !HasReadableSpan(data, block, MorphDataPrefixSize))
        {
            return false;
        }

        var be = nif.IsBigEndian;
        var start = block.DataOffset;
        var numMorphs = BinaryUtils.ReadUInt32(data, start, be);
        var numVertices = BinaryUtils.ReadUInt32(data, start + 4, be);
        var relative = data[start + 8];
        var morphStride = 4L + 12L * numVertices;
        if (numMorphs > MaximumItems || MorphDataPrefixSize + numMorphs * morphStride > block.Size)
        {
            return false;
        }

        var names = new int[(int)numMorphs];
        var position = (long)start + MorphDataPrefixSize;
        for (var index = 0; index < names.Length; index++)
        {
            names[index] = BinaryUtils.ReadInt32(data, (int)position, be);
            position += morphStride;
        }

        view = new NifMorphDataView(numMorphs, numVertices, relative, names, position == start + block.Size);
        return true;
    }

    /// <summary>Reads an NiFloatInterpolator losslessly.</summary>
    /// <param name="data">The file bytes.</param>
    /// <param name="nif">NifParser's header and block table.</param>
    /// <param name="block">The interpolator block.</param>
    /// <param name="view">The view.</param>
    /// <returns>False when the block is not an NiFloatInterpolator or is not exactly its 8 bytes.</returns>
    public static bool TryReadFloatInterpolatorView(
        byte[] data, NifInfo nif, BlockInfo block, out NifFloatInterpolatorView view)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(nif);
        ArgumentNullException.ThrowIfNull(block);
        view = default;
        if (block.TypeName != "NiFloatInterpolator" || block.Size != FloatInterpolatorSize ||
            !HasReadableSpan(data, block, FloatInterpolatorSize))
        {
            return false;
        }

        var be = nif.IsBigEndian;
        view = new NifFloatInterpolatorView(
            BinaryUtils.ReadUInt32(data, block.DataOffset, be),
            BinaryUtils.ReadInt32(data, block.DataOffset + 4, be));
        return true;
    }

    /// <summary>True when the block spans at least <paramref name="minimum" /> bytes inside the file.</summary>
    private static bool HasReadableSpan(byte[] data, BlockInfo block, int minimum)
    {
        return block.DataOffset >= 0 && block.Size >= minimum &&
               (long)block.DataOffset + block.Size <= data.LongLength;
    }
}
