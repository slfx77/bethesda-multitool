using System.Numerics;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Extracts a time-zero pose from supported interpolator block types.
/// </summary>
internal static class NifInterpolatorPoseReader
{
    internal static NifAnimationParser.AnimPoseOverride? Parse(
        byte[] data,
        NifInfo nif,
        BlockInfo block,
        bool be,
        bool sampleLastKeyframe = false)
    {
        return block.TypeName switch
        {
            "NiTransformInterpolator" or "BSRotAccumTransfInterpolator" =>
                ParseTransformInterpolator(data, nif, block, be, sampleLastKeyframe),
            "NiBSplineCompTransformInterpolator" or "BSTreadTransfInterpolator" =>
                ParseBsplineInterpolator(data, nif, block, be, sampleLastKeyframe),
            _ => null
        };
    }

    private static NifAnimationParser.AnimPoseOverride? ParseTransformInterpolator(
        byte[] data,
        NifInfo nif,
        BlockInfo block,
        bool be,
        bool sampleLastKeyframe)
    {
        if (block.Size < 32)
        {
            return null;
        }

        var pos = block.DataOffset;
        var tx = BinaryUtils.ReadFloat(data, pos, be);
        var ty = BinaryUtils.ReadFloat(data, pos + 4, be);
        var tz = BinaryUtils.ReadFloat(data, pos + 8, be);
        var qw = BinaryUtils.ReadFloat(data, pos + 12, be);
        var qx = BinaryUtils.ReadFloat(data, pos + 16, be);
        var qy = BinaryUtils.ReadFloat(data, pos + 20, be);
        var qz = BinaryUtils.ReadFloat(data, pos + 24, be);
        var scale = BinaryUtils.ReadFloat(data, pos + 28, be);

        var hasKeyframes = block.Size >= 36 &&
                           BinaryUtils.ReadInt32(data, pos + 32, be) >= 0;
        if (UsesFloatSentinels(qw, qx, qy, qz))
        {
            if (!hasKeyframes)
            {
                return null;
            }

            var dataRef = BinaryUtils.ReadInt32(data, pos + 32, be);
            return NifTransformDataKeyframeReader.ReadKeyframe(
                data,
                nif,
                dataRef,
                be,
                sampleLastKeyframe);
        }

        var hasTranslation = MathF.Abs(tx) < 1e30f;
        var hasScale = MathF.Abs(scale) < 1e30f;
        return new NifAnimationParser.AnimPoseOverride(
            new Quaternion(qx, qy, qz, qw),
            hasTranslation,
            hasTranslation ? tx : 0f,
            hasTranslation ? ty : 0f,
            hasTranslation ? tz : 0f,
            hasScale,
            hasScale ? scale : 1f);
    }

    private static NifAnimationParser.AnimPoseOverride? ParseBsplineInterpolator(
        byte[] data,
        NifInfo nif,
        BlockInfo block,
        bool be,
        bool sampleLastKeyframe)
    {
        if (block.Size < 84)
        {
            return null;
        }

        var splineDataRef = BinaryUtils.ReadInt32(data, block.DataOffset + 8, be);
        var basisDataRef = BinaryUtils.ReadInt32(data, block.DataOffset + 12, be);
        var transHandle = (uint)BinaryUtils.ReadInt32(data, block.DataOffset + 48, be);
        var rotHandle = (uint)BinaryUtils.ReadInt32(data, block.DataOffset + 52, be);
        var scaleHandle = (uint)BinaryUtils.ReadInt32(data, block.DataOffset + 56, be);

        // When sampling the last keyframe, offset handles to the last control point set.
        // NiBSplineBasisData stores numControlPoints as a uint32 at offset 0.
        if (sampleLastKeyframe &&
            basisDataRef >= 0 && basisDataRef < nif.Blocks.Count &&
            nif.Blocks[basisDataRef].TypeName == "NiBSplineBasisData")
        {
            var basisBlock = nif.Blocks[basisDataRef];
            var numControlPoints = BinaryUtils.ReadUInt32(data, basisBlock.DataOffset, be);
            if (numControlPoints > 1)
            {
                var lastCpOffset = numControlPoints - 1;
                if (rotHandle != 0xFFFF)
                    rotHandle += lastCpOffset * 4; // WXYZ per control point
                if (transHandle != 0xFFFF)
                    transHandle += lastCpOffset * 3; // XYZ per control point
                if (scaleHandle != 0xFFFF)
                    scaleHandle += lastCpOffset; // 1 value per control point
            }
        }

        if (rotHandle != 0xFFFF &&
            splineDataRef >= 0 &&
            splineDataRef < nif.Blocks.Count)
        {
            var splinePose = TryReadBsplinePose(
                data,
                nif,
                splineDataRef,
                transHandle,
                rotHandle,
                scaleHandle,
                block.DataOffset,
                be);
            if (splinePose != null)
            {
                return splinePose;
            }
        }

        return ReadBaseTransformFallback(data, block, be);
    }

    private static NifAnimationParser.AnimPoseOverride? TryReadBsplinePose(
        byte[] data,
        NifInfo nif,
        int splineDataRef,
        uint transHandle,
        uint rotHandle,
        uint scaleHandle,
        int blockOffset,
        bool be)
    {
        var splineBlock = nif.Blocks[splineDataRef];
        if (splineBlock.TypeName != "NiBSplineData")
        {
            return null;
        }

        var splinePos = splineBlock.DataOffset;
        var numFloatControlPoints = BinaryUtils.ReadInt32(data, splinePos, be);
        var compactArrayOffset = splinePos + 4 + numFloatControlPoints * 4;
        var numCompactControlPoints = BinaryUtils.ReadInt32(data, compactArrayOffset, be);
        var compactStart = compactArrayOffset + 4;

        if (rotHandle + 3 >= numCompactControlPoints)
        {
            return null;
        }

        var transOffset = BinaryUtils.ReadFloat(data, blockOffset + 60, be);
        var transHalfRange = BinaryUtils.ReadFloat(data, blockOffset + 64, be);
        var rotOffset = BinaryUtils.ReadFloat(data, blockOffset + 68, be);
        var rotHalfRange = BinaryUtils.ReadFloat(data, blockOffset + 72, be);
        var scaleOffset = BinaryUtils.ReadFloat(data, blockOffset + 76, be);
        var scaleHalfRange = BinaryUtils.ReadFloat(data, blockOffset + 80, be);

        var qw = DecompressControlPoint(data, compactStart, rotHandle, rotOffset, rotHalfRange, be);
        var qx = DecompressControlPoint(data, compactStart, rotHandle + 1, rotOffset, rotHalfRange, be);
        var qy = DecompressControlPoint(data, compactStart, rotHandle + 2, rotOffset, rotHalfRange, be);
        var qz = DecompressControlPoint(data, compactStart, rotHandle + 3, rotOffset, rotHalfRange, be);
        NormalizeQuaternion(ref qw, ref qx, ref qy, ref qz);

        var hasTranslation = transHandle != 0xFFFF &&
                             transHandle + 2 < numCompactControlPoints &&
                             MathF.Abs(transOffset) < 1e30f;
        var tx = 0f;
        var ty = 0f;
        var tz = 0f;
        if (hasTranslation)
        {
            tx = DecompressControlPoint(
                data,
                compactStart,
                transHandle,
                transOffset,
                transHalfRange,
                be);
            ty = DecompressControlPoint(
                data,
                compactStart,
                transHandle + 1,
                transOffset,
                transHalfRange,
                be);
            tz = DecompressControlPoint(
                data,
                compactStart,
                transHandle + 2,
                transOffset,
                transHalfRange,
                be);
        }

        var hasScale = scaleHandle != 0xFFFF &&
                       scaleHandle < numCompactControlPoints &&
                       MathF.Abs(scaleOffset) < 1e30f;
        var scale = 1f;
        if (hasScale)
        {
            scale = DecompressControlPoint(
                data,
                compactStart,
                scaleHandle,
                scaleOffset,
                scaleHalfRange,
                be);
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

    private static NifAnimationParser.AnimPoseOverride? ReadBaseTransformFallback(
        byte[] data,
        BlockInfo block,
        bool be)
    {
        var pos = block.DataOffset + 16;
        var qw = BinaryUtils.ReadFloat(data, pos + 12, be);
        var qx = BinaryUtils.ReadFloat(data, pos + 16, be);
        var qy = BinaryUtils.ReadFloat(data, pos + 20, be);
        var qz = BinaryUtils.ReadFloat(data, pos + 24, be);
        if (MathF.Abs(qw) >= 1e30f && MathF.Abs(qx) >= 1e30f)
        {
            return null;
        }

        var tx = BinaryUtils.ReadFloat(data, pos, be);
        var ty = BinaryUtils.ReadFloat(data, pos + 4, be);
        var tz = BinaryUtils.ReadFloat(data, pos + 8, be);
        var scale = BinaryUtils.ReadFloat(data, pos + 28, be);
        var hasTranslation = MathF.Abs(tx) < 1e30f;
        var hasScale = MathF.Abs(scale) < 1e30f;
        return new NifAnimationParser.AnimPoseOverride(
            new Quaternion(qx, qy, qz, qw),
            hasTranslation,
            hasTranslation ? tx : 0f,
            hasTranslation ? ty : 0f,
            hasTranslation ? tz : 0f,
            hasScale,
            hasScale ? scale : 1f);
    }

    private static bool UsesFloatSentinels(float qw, float qx, float qy, float qz)
    {
        return MathF.Abs(qw) > 1e30f &&
               MathF.Abs(qx) > 1e30f &&
               MathF.Abs(qy) > 1e30f &&
               MathF.Abs(qz) > 1e30f;
    }

    private static float DecompressControlPoint(
        byte[] data,
        int compactStart,
        uint handle,
        float offset,
        float halfRange,
        bool be)
    {
        var value = BinaryUtils.ReadInt16(data, compactStart + (int)handle * 2, be);
        return offset + value / 32767.0f * halfRange;
    }

    private static void NormalizeQuaternion(ref float qw, ref float qx, ref float qy, ref float qz)
    {
        var length = MathF.Sqrt(qw * qw + qx * qx + qy * qy + qz * qz);
        if (length <= 1e-6f)
        {
            return;
        }

        qw /= length;
        qx /= length;
        qy /= length;
        qz /= length;
    }
}
