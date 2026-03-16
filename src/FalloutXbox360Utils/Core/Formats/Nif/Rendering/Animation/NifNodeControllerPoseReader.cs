using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Parsing;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Samples transform poses from NiTransformController chains embedded directly on scene nodes.
/// </summary>
internal static class NifNodeControllerPoseReader
{
    internal static Dictionary<string, NifAnimationParser.AnimPoseOverride>? Parse(
        byte[] data,
        NifInfo nif,
        bool sampleLastKeyframe = false)
    {
        var result = new Dictionary<string, NifAnimationParser.AnimPoseOverride>(
            StringComparer.OrdinalIgnoreCase);
        var be = nif.IsBigEndian;

        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            var nodeBlock = nif.Blocks[i];
            if (!NifSceneGraphWalker.NodeTypes.Contains(nodeBlock.TypeName))
            {
                continue;
            }

            var nodeName = NifBlockParsers.ReadBlockName(data, nodeBlock, nif);
            if (string.IsNullOrWhiteSpace(nodeName))
            {
                continue;
            }

            var controllerRef = NifBinaryCursor.ReadNiObjectNETControllerRef(
                data,
                nodeBlock.DataOffset,
                nodeBlock.DataOffset + nodeBlock.Size,
                be);

            while (controllerRef >= 0 && controllerRef < nif.Blocks.Count)
            {
                var controllerBlock = nif.Blocks[controllerRef];
                if (controllerBlock.TypeName == "NiTransformController" &&
                    TryReadInterpolatorRef(data, controllerBlock, be, out var interpolatorRef))
                {
                    var pose = NifInterpolatorPoseReader.Parse(
                        data,
                        nif,
                        nif.Blocks[interpolatorRef],
                        be,
                        sampleLastKeyframe);
                    if (pose != null)
                    {
                        result[nodeName] = pose.Value;
                        break;
                    }
                }

                controllerRef = ReadNextControllerRef(data, controllerBlock, be);
            }
        }

        return result.Count > 0 ? result : null;
    }

    private static bool TryReadInterpolatorRef(
        byte[] data,
        BlockInfo controllerBlock,
        bool be,
        out int interpolatorRef)
    {
        interpolatorRef = -1;
        if (controllerBlock.Size < 30)
        {
            return false;
        }

        interpolatorRef = BinaryUtils.ReadInt32(data, controllerBlock.DataOffset + 26, be);
        return interpolatorRef >= 0;
    }

    private static int ReadNextControllerRef(byte[] data, BlockInfo controllerBlock, bool be)
    {
        return controllerBlock.Size >= 4
            ? BinaryUtils.ReadInt32(data, controllerBlock.DataOffset, be)
            : -1;
    }
}
