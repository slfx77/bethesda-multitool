using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Animation;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Parses idle animation pose overrides from NiControllerSequence blocks in skeleton/KF NIFs.
/// </summary>
internal static class NifAnimationParser
{
    /// <summary>
    ///     Per-channel animation override. Rotation is always present; translation and scale
    ///     are optional.
    /// </summary>
    internal readonly record struct AnimPoseOverride(
        Quaternion Rotation,
        bool HasTranslation,
        float Tx,
        float Ty,
        float Tz,
        bool HasScale,
        float Scale);

    /// <summary>
    ///     Parse idle pose overrides from NiControllerSequence blocks in a skeleton/KF NIF.
    /// </summary>
    internal static Dictionary<string, AnimPoseOverride>? ParseIdlePoseOverrides(
        byte[] data,
        NifInfo nif)
    {
        var be = nif.IsBigEndian;
        var sequenceBlock = NifControllerSequenceSelector.SelectIdleSequence(data, nif, be);
        if (sequenceBlock == null)
        {
            return null;
        }

        var pos = sequenceBlock.DataOffset;
        pos += 4;

        var numBlocks = BinaryUtils.ReadInt32(data, pos, be);
        pos += 4;
        if (numBlocks <= 0 || numBlocks > 500)
        {
            return null;
        }

        pos += 4;

        const int controlledBlockStride = 29;
        var result = new Dictionary<string, AnimPoseOverride>(
            StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < numBlocks; i++)
        {
            var blockStart = pos + i * controlledBlockStride;
            var interpolatorRef = BinaryUtils.ReadInt32(data, blockStart, be);
            var nodeNameIndex = BinaryUtils.ReadInt32(data, blockStart + 9, be);

            if (interpolatorRef < 0 ||
                interpolatorRef >= nif.Blocks.Count ||
                nodeNameIndex < 0 ||
                nodeNameIndex >= nif.Strings.Count)
            {
                continue;
            }

            var pose = NifInterpolatorPoseReader.Parse(
                data,
                nif,
                nif.Blocks[interpolatorRef],
                be);
            if (pose == null)
            {
                continue;
            }

            result[nif.Strings[nodeNameIndex]] = pose.Value;
        }

        RemoveAccumRootOverrides(data, nif, be, pos + numBlocks * controlledBlockStride, result);
        return result.Count > 0 ? result : null;
    }

    private static void RemoveAccumRootOverrides(
        byte[] data,
        NifInfo nif,
        bool be,
        int afterBlocks,
        Dictionary<string, AnimPoseOverride> result)
    {
        var accumRootPos = afterBlocks + 28;
        if (accumRootPos + 4 > data.Length)
        {
            return;
        }

        var accumRootIndex = BinaryUtils.ReadInt32(data, accumRootPos, be);
        if (accumRootIndex < 0 || accumRootIndex >= nif.Strings.Count)
        {
            return;
        }

        var accumName = nif.Strings[accumRootIndex];
        result.Remove(accumName);
        result.Remove(accumName + " NonAccum");

        var pelvisVariant = accumName.Replace(
            "Pelvis",
            "NonAccum",
            StringComparison.OrdinalIgnoreCase);
        if (pelvisVariant != accumName)
        {
            result.Remove(pelvisVariant);
        }
    }
}
