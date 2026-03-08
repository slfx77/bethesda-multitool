using System.Numerics;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Reads the first transform key from NiTransformData blocks.
/// </summary>
internal static class NifTransformDataKeyframeReader
{
    internal static NifAnimationParser.AnimPoseOverride? ReadFirstKeyframe(
        byte[] data,
        NifInfo nif,
        int dataRef,
        bool be)
    {
        if (dataRef < 0 || dataRef >= nif.Blocks.Count)
        {
            return null;
        }

        var block = nif.Blocks[dataRef];
        if (block.TypeName != "NiTransformData")
        {
            return null;
        }

        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        if (pos + 4 > end)
        {
            return null;
        }

        var numRotKeys = BinaryUtils.ReadInt32(data, pos, be);
        pos += 4;

        float qw = float.NaN;
        float qx = float.NaN;
        float qy = float.NaN;
        float qz = float.NaN;

        if (numRotKeys > 0)
        {
            if (pos + 4 > end)
            {
                return null;
            }

            var rotType = BinaryUtils.ReadInt32(data, pos, be);
            pos += 4;
            if (rotType == 4)
            {
                return null;
            }

            if (pos + 20 > end)
            {
                return null;
            }

            qw = BinaryUtils.ReadFloat(data, pos + 4, be);
            qx = BinaryUtils.ReadFloat(data, pos + 8, be);
            qy = BinaryUtils.ReadFloat(data, pos + 12, be);
            qz = BinaryUtils.ReadFloat(data, pos + 16, be);

            var rotKeyStride = rotType switch
            {
                1 => 20,
                2 => 36,
                3 => 32,
                _ => 20
            };
            pos += numRotKeys * rotKeyStride;
        }

        if (float.IsNaN(qw))
        {
            return null;
        }

        var hasTranslation = false;
        var tx = 0f;
        var ty = 0f;
        var tz = 0f;
        if (pos + 4 <= end)
        {
            var numPosKeys = BinaryUtils.ReadInt32(data, pos, be);
            pos += 4;

            if (numPosKeys > 0 && pos + 4 <= end)
            {
                var posType = BinaryUtils.ReadInt32(data, pos, be);
                pos += 4;

                if (pos + 16 <= end)
                {
                    tx = BinaryUtils.ReadFloat(data, pos + 4, be);
                    ty = BinaryUtils.ReadFloat(data, pos + 8, be);
                    tz = BinaryUtils.ReadFloat(data, pos + 12, be);
                    hasTranslation = true;
                }

                var posKeyStride = posType switch
                {
                    1 => 16,
                    2 => 28,
                    3 => 28,
                    _ => 16
                };
                pos += numPosKeys * posKeyStride;
            }
        }

        var hasScale = false;
        var scale = 1f;
        if (pos + 4 <= end)
        {
            var numScaleKeys = BinaryUtils.ReadInt32(data, pos, be);
            pos += 4;

            if (numScaleKeys > 0 && pos + 4 <= end)
            {
                pos += 4;
                if (pos + 8 <= end)
                {
                    scale = BinaryUtils.ReadFloat(data, pos + 4, be);
                    hasScale = true;
                }
            }
        }

        return new NifAnimationParser.AnimPoseOverride(
            new Quaternion(qx, qy, qz, qw),
            hasTranslation,
            tx,
            ty,
            tz,
            hasScale,
            scale);
    }
}
