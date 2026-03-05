using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Nif.Conversion;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Parses idle animation pose overrides from NiControllerSequence blocks in skeleton/KF NIFs.
///     Extracts per-bone local transforms from interpolator data for skinned mesh posing.
/// </summary>
internal static class NifAnimationParser
{
    /// <summary>
    ///     Per-channel animation override. Rotation is always present; translation and scale
    ///     are optional — when absent, the bind pose values are preserved.
    /// </summary>
    internal readonly record struct AnimPoseOverride(
        Quaternion Rotation,
        bool HasTranslation, float Tx, float Ty, float Tz,
        bool HasScale, float Scale);

    /// <summary>
    ///     Parse idle pose overrides from NiControllerSequence blocks in a skeleton/KF NIF.
    ///     Prefers a sequence with "idle" in the name; falls back to the first sequence found.
    ///     Returns a bone name → local transform dictionary, or null if no sequences exist.
    /// </summary>
    internal static Dictionary<string, AnimPoseOverride>? ParseIdlePoseOverrides(byte[] data, NifInfo nif)
    {
        var be = nif.IsBigEndian;

        // Find all NiControllerSequence blocks; prefer one with "idle" in the name
        BlockInfo? bestSeq = null;
        BlockInfo? firstSeq = null;
        foreach (var block in nif.Blocks)
        {
            if (block.TypeName != "NiControllerSequence")
            {
                continue;
            }

            firstSeq ??= block;

            if (block.Size < 4)
            {
                continue;
            }

            var nameIdx = BinaryUtils.ReadInt32(data, block.DataOffset, be);
            if (nameIdx >= 0 && nameIdx < nif.Strings.Count &&
                nif.Strings[nameIdx].Contains("idle", StringComparison.OrdinalIgnoreCase))
            {
                bestSeq = block;
                break;
            }
        }

        var seqBlock = bestSeq ?? firstSeq;
        if (seqBlock == null)
        {
            return null;
        }

        var pos = seqBlock.DataOffset;

        // Name (string index, 4B)
        pos += 4;

        // Num Controlled Blocks (uint32)
        var numBlocks = BinaryUtils.ReadInt32(data, pos, be);
        pos += 4;
        if (numBlocks <= 0 || numBlocks > 500)
        {
            return null;
        }

        // Array Grow By (uint32)
        pos += 4;

        // Parse each ControlledBlock (29 bytes each)
        const int controlledBlockStride = 29;
        var result = new Dictionary<string, AnimPoseOverride>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < numBlocks; i++)
        {
            var blockStart = pos + i * controlledBlockStride;

            // Interpolator Ref (int32) — block index of interpolator
            var interpRef = BinaryUtils.ReadInt32(data, blockStart, be);

            // Skip Controller Ref (4B) + Priority (1B) = offset +8 from block start
            // Node Name (string index, 4B) at offset +9
            var nodeNameIdx = BinaryUtils.ReadInt32(data, blockStart + 9, be);

            if (interpRef < 0 || interpRef >= nif.Blocks.Count)
            {
                continue;
            }

            if (nodeNameIdx < 0 || nodeNameIdx >= nif.Strings.Count)
            {
                continue;
            }

            var interpBlock = nif.Blocks[interpRef];
            var boneName = nif.Strings[nodeNameIdx];
            var pose = ParseInterpolatorPose(data, nif, interpBlock, be);
            if (pose != null)
            {
                result[boneName] = pose.Value;
            }
        }

        // Parse AccumRoot name and exclude root-motion bones from overrides.
        // The AccumRoot and its parent "NonAccum" bone carry root motion rotation
        // (world-space facing/movement) that the game engine applies to the game object transform.
        var afterBlocks = pos + numBlocks * controlledBlockStride;
        // Weight(4) + TextKeys(4) + CycleType(4) + Frequency(4) + StartTime(4) + StopTime(4) + Manager(4) = 28
        // Note: Phase field (4B) only exists for NIF version < 10.3.0.1; FNV is 20.2.0.7 so it's absent.
        var accumRootPos = afterBlocks + 28;
        if (accumRootPos + 4 <= data.Length)
        {
            var accumIdx = BinaryUtils.ReadInt32(data, accumRootPos, be);
            if (accumIdx >= 0 && accumIdx < nif.Strings.Count)
            {
                var accumName = nif.Strings[accumIdx];
                result.Remove(accumName);

                // Exclude NonAccum bone — the AccumRoot's child that receives the
                // "non-accumulated" pose. Its stored KF rotation is in the accum
                // coordinate frame and cannot be used as a direct local transform
                // replacement. Two naming conventions exist:
                //   Human: AccumRoot="Bip01" → child "Bip01 NonAccum"
                //   Creature: AccumRoot="Bip01 Pelvis" → sibling "Bip01 NonAccum"
                result.Remove(accumName + " NonAccum");
                var pelvisReplace = accumName.Replace("Pelvis", "NonAccum", StringComparison.OrdinalIgnoreCase);
                if (pelvisReplace != accumName)
                    result.Remove(pelvisReplace);
            }
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    ///     Extract per-channel pose data at time=0 from an interpolator block.
    ///     Returns an AnimPoseOverride with rotation (always) and optional translation/scale.
    /// </summary>
    private static AnimPoseOverride? ParseInterpolatorPose(byte[] data, NifInfo nif, BlockInfo block, bool be)
    {
        var typeName = block.TypeName;

        if (typeName is "NiTransformInterpolator" or "BSRotAccumTransfInterpolator")
        {
            // NiQuatTransform at +0: Translation(12B) + Quaternion WXYZ(16B) + Scale(4B) = 32B
            if (block.Size < 32) return null;
            var p = block.DataOffset;
            var tx = BinaryUtils.ReadFloat(data, p, be);
            var ty = BinaryUtils.ReadFloat(data, p + 4, be);
            var tz = BinaryUtils.ReadFloat(data, p + 8, be);
            var qw = BinaryUtils.ReadFloat(data, p + 12, be);
            var qx = BinaryUtils.ReadFloat(data, p + 16, be);
            var qy = BinaryUtils.ReadFloat(data, p + 20, be);
            var qz = BinaryUtils.ReadFloat(data, p + 24, be);
            var scale = BinaryUtils.ReadFloat(data, p + 28, be);

            var hasKeyframes = block.Size >= 36 &&
                BinaryUtils.ReadInt32(data, p + 32, be) >= 0;

            if (MathF.Abs(qw) > 1e30f && MathF.Abs(qx) > 1e30f &&
                MathF.Abs(qy) > 1e30f && MathF.Abs(qz) > 1e30f)
            {
                // Stored pose is FLT_MAX sentinels — try reading first keyframe from NiTransformData.
                // The game engine evaluates keyframes at runtime via NiTransformInterpolator::Update.
                if (hasKeyframes)
                {
                    var dataRef = BinaryUtils.ReadInt32(data, p + 32, be);
                    var kfResult = ReadFirstKeyframe(data, nif, dataRef, be);
                    if (kfResult != null)
                        return kfResult;
                }
                return null;
            }

            var hasTrans = MathF.Abs(tx) < 1e30f;
            var hasScale = MathF.Abs(scale) < 1e30f;
            return new AnimPoseOverride(
                new Quaternion(qx, qy, qz, qw),
                hasTrans, hasTrans ? tx : 0, hasTrans ? ty : 0, hasTrans ? tz : 0,
                hasScale, hasScale ? scale : 1.0f);
        }

        if (typeName is "NiBSplineCompTransformInterpolator" or "BSTreadTransfInterpolator")
        {
            if (block.Size < 84) return null;

            // Parse BSpline handles first — per NIF spec, BSpline data takes priority
            // over the base NiQuatTransform when handles are valid (not 0xFFFF).
            var splineDataRef = BinaryUtils.ReadInt32(data, block.DataOffset + 8, be);
            var transHandle = (uint)BinaryUtils.ReadInt32(data, block.DataOffset + 48, be);
            var rotHandle = (uint)BinaryUtils.ReadInt32(data, block.DataOffset + 52, be);
            var scaleHandle = (uint)BinaryUtils.ReadInt32(data, block.DataOffset + 56, be);

            // Try BSpline decompression if rotation handle is valid
            if (rotHandle != 0xFFFF && splineDataRef >= 0 && splineDataRef < nif.Blocks.Count)
            {
                var splineBlock = nif.Blocks[splineDataRef];
                if (splineBlock.TypeName == "NiBSplineData")
                {
                    var splinePos = splineBlock.DataOffset;
                    var numFloatCPs = BinaryUtils.ReadInt32(data, splinePos, be);
                    var compactArrayOffset = splinePos + 4 + numFloatCPs * 4;
                    var numCompactCPs = BinaryUtils.ReadInt32(data, compactArrayOffset, be);
                    var compactStart = compactArrayOffset + 4;

                    var transOff = BinaryUtils.ReadFloat(data, block.DataOffset + 60, be);
                    var transHalf = BinaryUtils.ReadFloat(data, block.DataOffset + 64, be);
                    var rotOff = BinaryUtils.ReadFloat(data, block.DataOffset + 68, be);
                    var rotHalf = BinaryUtils.ReadFloat(data, block.DataOffset + 72, be);
                    var scaleOff = BinaryUtils.ReadFloat(data, block.DataOffset + 76, be);
                    var scaleHalf = BinaryUtils.ReadFloat(data, block.DataOffset + 80, be);

                    if (rotHandle + 3 < numCompactCPs)
                    {
                        // Decompress first rotation control point (4 shorts = WXYZ quaternion)
                        var rw = BinaryUtils.ReadInt16(data, compactStart + (int)rotHandle * 2, be);
                        var rx = BinaryUtils.ReadInt16(data, compactStart + (int)(rotHandle + 1) * 2, be);
                        var ry = BinaryUtils.ReadInt16(data, compactStart + (int)(rotHandle + 2) * 2, be);
                        var rz = BinaryUtils.ReadInt16(data, compactStart + (int)(rotHandle + 3) * 2, be);
                        var qw = rotOff + rw / 32767.0f * rotHalf;
                        var qx = rotOff + rx / 32767.0f * rotHalf;
                        var qy = rotOff + ry / 32767.0f * rotHalf;
                        var qz = rotOff + rz / 32767.0f * rotHalf;

                        // Normalize quaternion — BSpline decompression quantization can drift from unit length
                        var qLen = MathF.Sqrt(qw * qw + qx * qx + qy * qy + qz * qz);
                        if (qLen > 1e-6f)
                        {
                            qw /= qLen; qx /= qLen; qy /= qLen; qz /= qLen;
                        }

                        // Decompress first translation control point (optional)
                        var hasTrans = transHandle != 0xFFFF && transHandle + 2 < numCompactCPs &&
                                       MathF.Abs(transOff) < 1e30f;
                        float tx = 0, ty = 0, tz = 0;
                        if (hasTrans)
                        {
                            var cx = BinaryUtils.ReadInt16(data, compactStart + (int)transHandle * 2, be);
                            var cy = BinaryUtils.ReadInt16(data, compactStart + (int)(transHandle + 1) * 2, be);
                            var cz = BinaryUtils.ReadInt16(data, compactStart + (int)(transHandle + 2) * 2, be);
                            tx = transOff + cx / 32767.0f * transHalf;
                            ty = transOff + cy / 32767.0f * transHalf;
                            tz = transOff + cz / 32767.0f * transHalf;
                        }

                        // Decompress scale (optional)
                        var hasScale = scaleHandle != 0xFFFF && scaleHandle < numCompactCPs &&
                                       MathF.Abs(scaleOff) < 1e30f;
                        var scale = 1.0f;
                        if (hasScale)
                        {
                            var cs = BinaryUtils.ReadInt16(data, compactStart + (int)scaleHandle * 2, be);
                            scale = scaleOff + cs / 32767.0f * scaleHalf;
                        }

                        return new AnimPoseOverride(
                            new Quaternion(qx, qy, qz, qw),
                            hasTrans, tx, ty, tz,
                            hasScale, scale);
                    }
                }
            }

            // Fallback: use static base NiQuatTransform at +16 (when BSpline data unavailable)
            var p = block.DataOffset + 16;
            var sqw = BinaryUtils.ReadFloat(data, p + 12, be);
            var sqx = BinaryUtils.ReadFloat(data, p + 16, be);
            var sqy = BinaryUtils.ReadFloat(data, p + 20, be);
            var sqz = BinaryUtils.ReadFloat(data, p + 24, be);

            if (MathF.Abs(sqw) < 1e30f || MathF.Abs(sqx) < 1e30f)
            {
                var stx = BinaryUtils.ReadFloat(data, p, be);
                var sty = BinaryUtils.ReadFloat(data, p + 4, be);
                var stz = BinaryUtils.ReadFloat(data, p + 8, be);
                var sScale = BinaryUtils.ReadFloat(data, p + 28, be);
                var sHasTrans = MathF.Abs(stx) < 1e30f;
                var sHasScale = MathF.Abs(sScale) < 1e30f;
                return new AnimPoseOverride(
                    new Quaternion(sqx, sqy, sqz, sqw),
                    sHasTrans, sHasTrans ? stx : 0, sHasTrans ? sty : 0, sHasTrans ? stz : 0,
                    sHasScale, sHasScale ? sScale : 1.0f);
            }

            return null;
        }

        return null;
    }

    /// <summary>
    ///     Read the first keyframe from a NiTransformData block (rotation + optional translation/scale).
    ///     NiTransformData layout (NIF 20.2.0.7):
    ///       numRotKeys(uint) + [if>0: rotType(uint) + keys[]] + numPosKeys(uint) + [...] + numScaleKeys(uint) + [...]
    ///     For LINEAR rotation keys: each key = time(4B) + WXYZ(16B) = 20 bytes.
    ///     For LINEAR position keys: each key = time(4B) + XYZ(12B) = 16 bytes.
    ///     For LINEAR scale keys: each key = time(4B) + value(4B) = 8 bytes.
    /// </summary>
    private static AnimPoseOverride? ReadFirstKeyframe(byte[] data, NifInfo nif, int dataRef, bool be)
    {
        if (dataRef < 0 || dataRef >= nif.Blocks.Count)
            return null;

        var block = nif.Blocks[dataRef];
        if (block.TypeName != "NiTransformData")
            return null;

        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        // Rotation keys
        if (pos + 4 > end) return null;
        var numRotKeys = BinaryUtils.ReadInt32(data, pos, be);
        pos += 4;

        float qw = float.NaN, qx = float.NaN, qy = float.NaN, qz = float.NaN;
        if (numRotKeys > 0)
        {
            if (pos + 4 > end) return null;
            var rotType = BinaryUtils.ReadInt32(data, pos, be);
            pos += 4;

            if (rotType == 4) // XYZ_ROTATION — Euler angles, complex; skip for now
            {
                return null;
            }

            // LINEAR(1), QUADRATIC(2), TBC(3): first key starts with time(4B) + WXYZ(16B)
            if (pos + 20 > end) return null;
            // Skip time
            qw = BinaryUtils.ReadFloat(data, pos + 4, be);
            qx = BinaryUtils.ReadFloat(data, pos + 8, be);
            qy = BinaryUtils.ReadFloat(data, pos + 12, be);
            qz = BinaryUtils.ReadFloat(data, pos + 16, be);

            // Skip past all rotation keys to reach translation section
            var rotKeyStride = rotType switch
            {
                1 => 20, // LINEAR: time(4) + WXYZ(16)
                2 => 36, // QUADRATIC: time(4) + WXYZ(16) + TBC(12) + ?(4)
                3 => 32, // TBC: time(4) + WXYZ(16) + TBC(12)
                _ => 20
            };
            pos += numRotKeys * rotKeyStride;
        }

        if (float.IsNaN(qw))
            return null;

        // Translation keys
        float tx = 0, ty = 0, tz = 0;
        var hasTrans = false;
        if (pos + 4 <= end)
        {
            var numPosKeys = BinaryUtils.ReadInt32(data, pos, be);
            pos += 4;

            if (numPosKeys > 0 && pos + 4 <= end)
            {
                var posType = BinaryUtils.ReadInt32(data, pos, be);
                pos += 4;

                // LINEAR(1): time(4) + XYZ(12) = 16B per key
                if (pos + 16 <= end)
                {
                    tx = BinaryUtils.ReadFloat(data, pos + 4, be);
                    ty = BinaryUtils.ReadFloat(data, pos + 8, be);
                    tz = BinaryUtils.ReadFloat(data, pos + 12, be);
                    hasTrans = true;
                }

                var posKeyStride = posType switch { 1 => 16, 2 => 28, 3 => 28, _ => 16 };
                pos += numPosKeys * posKeyStride;
            }
        }

        // Scale keys
        var scale = 1.0f;
        var hasScale = false;
        if (pos + 4 <= end)
        {
            var numScaleKeys = BinaryUtils.ReadInt32(data, pos, be);
            pos += 4;

            if (numScaleKeys > 0 && pos + 4 <= end)
            {
                var scaleType = BinaryUtils.ReadInt32(data, pos, be);
                pos += 4;

                // LINEAR(1): time(4) + value(4) = 8B per key
                if (pos + 8 <= end)
                {
                    scale = BinaryUtils.ReadFloat(data, pos + 4, be);
                    hasScale = true;
                }
            }
        }

        return new AnimPoseOverride(
            new Quaternion(qx, qy, qz, qw),
            hasTrans, tx, ty, tz,
            hasScale, scale);
    }
}
