using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Reads a FULL keyframe track from a <c>NiKeyframeData</c> or <c>NiTransformData</c> block —
///     the two are the same layout under different era names (nif.xml: NiTransformData is a pure
///     rename): quaternion rotation keys, then a translation KeyGroup&lt;Vector3&gt;, then a scale
///     KeyGroup&lt;float&gt;. One reader serves both the TES3 per-node NiKeyframeController graph and
///     the modern NiTransformInterpolator indirection. Unlike the single-key snapshot reader
///     (<see cref="NifTransformDataKeyframeReader" />), every key is retained for time-domain
///     sampling, and XYZ-Euler rotation blocks are structurally skipped so the translation/scale
///     groups that follow still parse.
/// </summary>
internal static class NifKeyframeDataTrackReader
{
    /// <summary>Block type names this reader accepts.</summary>
    internal static bool IsTrackDataBlock(string typeName)
    {
        return typeName is "NiKeyframeData" or "NiTransformData";
    }

    /// <summary>
    ///     Reads the data block behind a keyframe controller into a <see cref="NifNodeTrack" />.
    ///     Returns null when the ref is invalid, the block isn't keyframe data, or the stream
    ///     desyncs (truncated keys).
    /// </summary>
    internal static NifNodeTrack? TryReadTrack(
        byte[] data,
        NifInfo nif,
        int dataRef,
        string nodeName,
        float frequency,
        float phase)
    {
        if (dataRef < 0 || dataRef >= nif.Blocks.Count)
        {
            return null;
        }

        var block = nif.Blocks[dataRef];
        if (!IsTrackDataBlock(block.TypeName))
        {
            return null;
        }

        var be = nif.IsBigEndian;
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        if (!NifKeyGroupReader.TryReadQuatKeys(
                data, ref pos, end, be, nif.BinaryVersion, out var rotInterp, out var rotKeys,
                out var eulerKeys) ||
            !NifKeyGroupReader.TryReadVector3Keys(
                data, ref pos, end, be, out var transInterp, out var transKeys) ||
            !NifKeyGroupReader.TryReadFloatKeys(
                data, ref pos, end, be, out var scaleInterp, out var scaleKeys))
        {
            return null;
        }

        return new NifNodeTrack(
            nodeName,
#pragma warning disable S1244 // 0 is the authored "unset" frequency sentinel; exact comparison intended
            frequency == 0f ? 1f : frequency,
#pragma warning restore S1244
            phase,
            rotInterp, rotKeys,
            transInterp, transKeys,
            scaleInterp, scaleKeys,
            eulerKeys?.X,
            eulerKeys?.Y,
            eulerKeys?.Z);
    }

    /// <summary>
    ///     Reads an NiKeyframeData / NiTransformData block losslessly (<see cref="NifKeyframeDataView" />): the rotation
    ///     part with each Euler axis's own key type, the translation group and the scale group, every field as raw bits.
    ///     The structural walk is the one <see cref="TryReadTrack" /> projects (both go through
    ///     <see cref="NifKeyGroupReader" />), but the two do not accept the same blocks. The view accepts more: it keeps a
    ///     Quadratic translation key whose value or tangent is non-finite or at least 1e30 in magnitude, which makes the
    ///     renderer's projection refuse the group and <see cref="TryReadTrack" /> return null. The view also refuses a block
    ///     whose declared span runs past the end of <paramref name="data" />, which <see cref="TryReadTrack" /> does not
    ///     check. Exact consumption is reported, not required.
    /// </summary>
    internal static bool TryReadView(byte[] data, NifInfo nif, BlockInfo block, out NifKeyframeDataView view)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(nif);
        ArgumentNullException.ThrowIfNull(block);
        view = default;
        if (!IsTrackDataBlock(block.TypeName) ||
            block.DataOffset < 0 || block.Size < 0 ||
            (long)block.DataOffset + block.Size > data.LongLength)
        {
            return false;
        }

        var be = nif.IsBigEndian;
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;
        if (!NifKeyGroupReader.TryReadRotationView(data, ref pos, end, be, nif.BinaryVersion, out var rotation) ||
            !NifKeyGroupReader.TryReadGroupView(
                data, ref pos, end, be, NifKeyValueLayout.Vector3, out var translations) ||
            !NifKeyGroupReader.TryReadGroupView(data, ref pos, end, be, NifKeyValueLayout.Float, out var scales))
        {
            return false;
        }

        view = new NifKeyframeDataView(rotation, translations, scales, end);
        return true;
    }

    /// <summary>
    ///     The keyframe-data ref of a <c>NiKeyframeController</c> (nif.xml: Data, the first
    ///     type-specific field at +26). <c>BSKeyframeController</c> inherits it and appends a
    ///     Data&#160;2 ref after — same offset for the primary track.
    /// </summary>
    internal static int ReadControllerDataRef(byte[] data, BlockInfo controllerBlock, bool be)
    {
        return controllerBlock.Size >= NifTimeControllerHeader.HeaderSize + 4
            ? BinaryUtils.ReadInt32(data, controllerBlock.DataOffset + NifTimeControllerHeader.HeaderSize, be)
            : -1;
    }
}
