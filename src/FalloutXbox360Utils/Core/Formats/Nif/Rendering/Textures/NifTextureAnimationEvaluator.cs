using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Parsing;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Evaluates legacy NiTexturingProperty UV animation controllers into a static snapshot transform.
/// </summary>
internal static class NifTextureAnimationEvaluator
{
    private const uint Version10_1_0_0 = 0x0A010000;
    private const uint Version20_0_0_5 = 0x14000005;
    private const uint Version20_1_0_3 = 0x14010003;
    private const uint Version20_5_0_4 = 0x14050004;
    private const float RepresentativeSamplePhase = 0.5f;
    private const float SentinelMagnitude = 1e30f;

    internal static bool TryResolveBaseUvTransform(
        byte[] data,
        NifInfo nif,
        List<int> propertyRefs,
        out NifTextureTransformSnapshot transform)
    {
        transform = NifTextureTransformSnapshot.Identity;

        foreach (var propRef in propertyRefs)
        {
            if (propRef < 0 || propRef >= nif.Blocks.Count)
            {
                continue;
            }

            var block = nif.Blocks[propRef];
            if (block.TypeName != "NiTexturingProperty")
            {
                continue;
            }

            if (!TryReadBaseTextureState(data, nif, block, out var state))
            {
                continue;
            }

            var controllerRef = NifBinaryCursor.ReadNiObjectNETControllerRef(
                data,
                block.DataOffset,
                block.DataOffset + block.Size,
                nif.IsBigEndian);

            var hasAnimatedMember = false;
            while (controllerRef >= 0 && controllerRef < nif.Blocks.Count)
            {
                var controllerBlock = nif.Blocks[controllerRef];
                if (controllerBlock.TypeName == "NiTextureTransformController" &&
                    TryReadTextureTransformController(
                        data,
                        nif,
                        controllerBlock,
                        out var controller) &&
                    controller.TargetRef == propRef &&
                    !controller.ShaderMap &&
                    controller.TextureSlot == 0 &&
                    TryEvaluateFloatInterpolator(
                        data,
                        nif,
                        controller.InterpolatorRef,
                        ResolveRepresentativeTime(controller),
                        out var sampleValue))
                {
                    ApplyAnimatedMember(ref state, controller.Operation, sampleValue);
                    hasAnimatedMember = true;
                }

                controllerRef = ReadNextControllerRef(data, controllerBlock, nif.IsBigEndian);
            }

            if (hasAnimatedMember || state.HasTransform)
            {
                transform = state.ToSnapshot();
                return transform.HasNonIdentity;
            }
        }

        return false;
    }

    internal static void ApplyInPlace(float[] uvs, in NifTextureTransformSnapshot transform)
    {
        if (!transform.HasNonIdentity)
        {
            return;
        }

        for (var i = 0; i + 1 < uvs.Length; i += 2)
        {
            var u = uvs[i];
            var v = uvs[i + 1];
            (uvs[i], uvs[i + 1]) = Apply(transform, u, v);
        }
    }

    private static (float U, float V) Apply(
        in NifTextureTransformSnapshot transform,
        float u,
        float v)
    {
        return transform.TransformMethod switch
        {
            1 => ApplyMaxTransform(transform, u, v),
            2 => ApplyMayaTransform(transform, u, v),
            _ => ApplyMayaDeprecatedTransform(transform, u, v)
        };
    }

    private static (float U, float V) ApplyMaxTransform(
        in NifTextureTransformSnapshot transform,
        float u,
        float v)
    {
        (u, v) = ApplyBack(transform, u, v);
        (u, v) = ApplyTranslate(transform, u, v);
        (u, v) = ApplyRotate(transform, u, v);
        (u, v) = ApplyScale(transform, u, v);
        return ApplyCenter(transform, u, v);
    }

    private static (float U, float V) ApplyMayaDeprecatedTransform(
        in NifTextureTransformSnapshot transform,
        float u,
        float v)
    {
        (u, v) = ApplyScale(transform, u, v);
        (u, v) = ApplyTranslate(transform, u, v);
        (u, v) = ApplyBack(transform, u, v);
        (u, v) = ApplyRotate(transform, u, v);
        return ApplyCenter(transform, u, v);
    }

    private static (float U, float V) ApplyMayaTransform(
        in NifTextureTransformSnapshot transform,
        float u,
        float v)
    {
        (u, v) = ApplyScale(transform, u, v);
        (u, v) = ApplyTranslate(transform, u, v);
        v = 1f - v;
        (u, v) = ApplyBack(transform, u, v);
        (u, v) = ApplyRotate(transform, u, v);
        return ApplyCenter(transform, u, v);
    }

    private static (float U, float V) ApplyTranslate(
        in NifTextureTransformSnapshot transform,
        float u,
        float v)
    {
        return (u + transform.TranslationU, v + transform.TranslationV);
    }

    private static (float U, float V) ApplyScale(
        in NifTextureTransformSnapshot transform,
        float u,
        float v)
    {
        return (u * transform.ScaleU, v * transform.ScaleV);
    }

    private static (float U, float V) ApplyRotate(
        in NifTextureTransformSnapshot transform,
        float u,
        float v)
    {
        if (MathF.Abs(transform.RotationRadians) < 1e-6f)
        {
            return (u, v);
        }

        var cos = MathF.Cos(transform.RotationRadians);
        var sin = MathF.Sin(transform.RotationRadians);
        return (
            u * cos - v * sin,
            u * sin + v * cos);
    }

    private static (float U, float V) ApplyBack(
        in NifTextureTransformSnapshot transform,
        float u,
        float v)
    {
        return (u - transform.CenterU, v - transform.CenterV);
    }

    private static (float U, float V) ApplyCenter(
        in NifTextureTransformSnapshot transform,
        float u,
        float v)
    {
        return (u + transform.CenterU, v + transform.CenterV);
    }

    private static float ResolveRepresentativeTime(in TextureTransformController controller)
    {
        if (IsFinite(controller.StartTime) &&
            IsFinite(controller.StopTime) &&
            controller.StopTime > controller.StartTime)
        {
            return controller.StartTime + (controller.StopTime - controller.StartTime) * RepresentativeSamplePhase;
        }

        return RepresentativeSamplePhase;
    }

    private static bool TryReadBaseTextureState(
        byte[] data,
        NifInfo nif,
        BlockInfo block,
        out MutableTextureTransformState state)
    {
        state = MutableTextureTransformState.CreateDefault();

        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;
        if (!NifBinaryCursor.SkipNiObjectNET(data, ref pos, end, nif.IsBigEndian))
        {
            return false;
        }

        if (pos + 2 + 4 + 1 > end)
        {
            return false;
        }

        pos += 2; // NiProperty flags
        pos += 4; // texture count

        var hasBaseTexture = data[pos] != 0;
        pos += 1;
        if (!hasBaseTexture)
        {
            return false;
        }

        if (pos + 4 > end)
        {
            return false;
        }

        pos += 4; // source ref

        if (nif.BinaryVersion >= Version20_1_0_3)
        {
            if (pos + 2 > end)
            {
                return false;
            }

            pos += 2; // TexturingMapFlags
            if (nif.BinaryVersion >= Version20_5_0_4)
            {
                if (pos + 2 > end)
                {
                    return false;
                }

                pos += 2; // Max anisotropy
            }
        }
        else
        {
            if (pos + 8 > end)
            {
                return false;
            }

            pos += 8; // clamp + filter
            if (nif.BinaryVersion <= Version20_0_0_5)
            {
                if (pos + 4 > end)
                {
                    return false;
                }

                pos += 4; // UV set
            }
        }

        if (nif.BinaryVersion < Version10_1_0_0 || pos + 1 > end)
        {
            return true;
        }

        state.HasTransform = data[pos] != 0;
        pos += 1;
        if (!state.HasTransform)
        {
            return true;
        }

        if (pos + 28 > end)
        {
            return false;
        }

        state.TranslationU = BinaryUtils.ReadFloat(data, pos, nif.IsBigEndian);
        state.TranslationV = BinaryUtils.ReadFloat(data, pos + 4, nif.IsBigEndian);
        state.ScaleU = BinaryUtils.ReadFloat(data, pos + 8, nif.IsBigEndian);
        state.ScaleV = BinaryUtils.ReadFloat(data, pos + 12, nif.IsBigEndian);
        state.RotationRadians = BinaryUtils.ReadFloat(data, pos + 16, nif.IsBigEndian);
        state.TransformMethod = BinaryUtils.ReadUInt32(data, pos + 20, nif.IsBigEndian);
        state.CenterU = BinaryUtils.ReadFloat(data, pos + 24, nif.IsBigEndian);
        state.CenterV = BinaryUtils.ReadFloat(data, pos + 28, nif.IsBigEndian);
        return true;
    }

    private static bool TryReadTextureTransformController(
        byte[] data,
        NifInfo nif,
        BlockInfo block,
        out TextureTransformController controller)
    {
        controller = default;
        var be = nif.IsBigEndian;
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        if (pos + 4 + 2 + 16 + 4 + 4 + 3 > end)
        {
            return false;
        }

        controller = new TextureTransformController(
            ReadNextControllerRef(data, block, be),
            BinaryUtils.ReadFloat(data, pos + 6, be),
            BinaryUtils.ReadFloat(data, pos + 10, be),
            BinaryUtils.ReadFloat(data, pos + 14, be),
            BinaryUtils.ReadFloat(data, pos + 18, be),
            BinaryUtils.ReadInt32(data, pos + 22, be),
            BinaryUtils.ReadInt32(data, pos + 26, be),
            data[pos + 30] != 0,
            data[pos + 31],
            data[pos + 32]);
        return true;
    }

    private static int ReadNextControllerRef(byte[] data, BlockInfo block, bool be)
    {
        return block.Size >= 4
            ? BinaryUtils.ReadInt32(data, block.DataOffset, be)
            : -1;
    }

    private static bool TryEvaluateFloatInterpolator(
        byte[] data,
        NifInfo nif,
        int interpolatorRef,
        float sampleTime,
        out float value)
    {
        value = 0f;
        if (interpolatorRef < 0 || interpolatorRef >= nif.Blocks.Count)
        {
            return false;
        }

        var block = nif.Blocks[interpolatorRef];
        if (block.TypeName != "NiFloatInterpolator" || block.Size < 8)
        {
            return false;
        }

        var poseValue = BinaryUtils.ReadFloat(data, block.DataOffset, nif.IsBigEndian);
        var dataRef = BinaryUtils.ReadInt32(data, block.DataOffset + 4, nif.IsBigEndian);

        if (dataRef >= 0 && dataRef < nif.Blocks.Count &&
            TryEvaluateFloatData(data, nif, dataRef, sampleTime, out value))
        {
            return true;
        }

        if (MathF.Abs(poseValue) < SentinelMagnitude)
        {
            value = poseValue;
            return true;
        }

        return false;
    }

    private static bool TryEvaluateFloatData(
        byte[] data,
        NifInfo nif,
        int dataRef,
        float sampleTime,
        out float value)
    {
        value = 0f;
        var block = nif.Blocks[dataRef];
        if (block.TypeName != "NiFloatData" || block.Size < 4)
        {
            return false;
        }

        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;
        var numKeys = (int)BinaryUtils.ReadUInt32(data, pos, nif.IsBigEndian);
        pos += 4;

        if (numKeys <= 0 || pos + 4 > end)
        {
            return false;
        }

        var interpolation = (int)BinaryUtils.ReadUInt32(data, pos, nif.IsBigEndian);
        pos += 4;
        var keyStride = interpolation switch
        {
            2 => 16, // quadratic: time + value + forward + backward
            3 => 20, // TBC: time + value + tension/bias/continuity
            _ => 8
        };

        var keys = new List<(float Time, float Value)>(numKeys);
        for (var i = 0; i < numKeys && pos + keyStride <= end; i++)
        {
            keys.Add((
                BinaryUtils.ReadFloat(data, pos, nif.IsBigEndian),
                BinaryUtils.ReadFloat(data, pos + 4, nif.IsBigEndian)));
            pos += keyStride;
        }

        if (keys.Count == 0)
        {
            return false;
        }

        if (sampleTime <= keys[0].Time)
        {
            value = keys[0].Value;
            return true;
        }

        if (sampleTime >= keys[^1].Time)
        {
            value = keys[^1].Value;
            return true;
        }

        for (var i = 1; i < keys.Count; i++)
        {
            var previous = keys[i - 1];
            var next = keys[i];
            if (sampleTime > next.Time)
            {
                continue;
            }

            var duration = next.Time - previous.Time;
            if (duration <= 1e-6f)
            {
                value = next.Value;
                return true;
            }

            var t = (sampleTime - previous.Time) / duration;
            value = previous.Value + (next.Value - previous.Value) * t;
            return true;
        }

        value = keys[^1].Value;
        return true;
    }

    private static void ApplyAnimatedMember(
        ref MutableTextureTransformState state,
        byte operation,
        float sampleValue)
    {
        state.HasTransform = true;
        switch (operation)
        {
            case 0:
                state.TranslationU = sampleValue;
                break;
            case 1:
                state.TranslationV = sampleValue;
                break;
            case 2:
                state.RotationRadians = sampleValue;
                break;
            case 3:
                state.ScaleU = sampleValue;
                break;
            case 4:
                state.ScaleV = sampleValue;
                break;
        }
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) &&
               !float.IsInfinity(value) &&
               MathF.Abs(value) < SentinelMagnitude;
    }

    internal readonly record struct NifTextureTransformSnapshot(
        float TranslationU,
        float TranslationV,
        float ScaleU,
        float ScaleV,
        float RotationRadians,
        uint TransformMethod,
        float CenterU,
        float CenterV)
    {
        internal static readonly NifTextureTransformSnapshot Identity = new(0f, 0f, 1f, 1f, 0f, 0u, 0f, 0f);

        internal bool HasNonIdentity =>
            MathF.Abs(TranslationU) > 1e-6f ||
            MathF.Abs(TranslationV) > 1e-6f ||
            MathF.Abs(ScaleU - 1f) > 1e-6f ||
            MathF.Abs(ScaleV - 1f) > 1e-6f ||
            MathF.Abs(RotationRadians) > 1e-6f ||
            MathF.Abs(CenterU) > 1e-6f ||
            MathF.Abs(CenterV) > 1e-6f;
    }

    private readonly record struct TextureTransformController(
        int NextControllerRef,
        float Frequency,
        float Phase,
        float StartTime,
        float StopTime,
        int TargetRef,
        int InterpolatorRef,
        bool ShaderMap,
        byte TextureSlot,
        byte Operation);

    private struct MutableTextureTransformState
    {
        internal bool HasTransform;
        internal float TranslationU;
        internal float TranslationV;
        internal float ScaleU;
        internal float ScaleV;
        internal float RotationRadians;
        internal uint TransformMethod;
        internal float CenterU;
        internal float CenterV;

        internal static MutableTextureTransformState CreateDefault()
        {
            return new MutableTextureTransformState
            {
                HasTransform = false,
                ScaleU = 1f,
                ScaleV = 1f
            };
        }

        internal NifTextureTransformSnapshot ToSnapshot()
        {
            return new NifTextureTransformSnapshot(
                TranslationU,
                TranslationV,
                ScaleU,
                ScaleV,
                RotationRadians,
                TransformMethod,
                CenterU,
                CenterV);
        }
    }
}
