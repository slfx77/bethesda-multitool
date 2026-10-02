using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Strict reader for the two 20.2.0.7 transform B-spline interpolators. The serialized field
///     order follows NifTools' nif.xml; compact values use its signed-short / 32767, multiplier,
///     then bias contract.
/// </summary>
internal static class NifBsplineTransformReader
{
    internal const uint AbsentChannelHandle = 0xFFFF;
    internal const int MaximumControlPointCount = 1_048_576;
    internal const long MaximumDecodedScalarCount = 16L * 1024L * 1024L;

    private const int TransformInterpolatorSize = 60;
    private const int CompressedTransformInterpolatorSize = 84;

    // NiBSplineInterpolator: Start Time, Stop Time, Spline Data ref, Basis Data ref.
    private const int BsplineInterpolatorBaseSize = 16;

    // NiQuatTransform carries TRS Valid flags until 10.1.0.109; the views read the later layout only.
    private const uint LastVersionWithTrsValidFlags = 0x0A01006D;

    internal static bool TryRead(
        byte[] data,
        NifInfo nif,
        BlockInfo interpolator,
        string nodeName,
        ref long decodedScalarCount,
        out NifNameTargetedBsplineTransformTrack track)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(nif);
        ArgumentNullException.ThrowIfNull(interpolator);

        track = null!;
        var compressed = interpolator.TypeName == "NiBSplineCompTransformInterpolator";
        if (!compressed && interpolator.TypeName != "NiBSplineTransformInterpolator")
        {
            return false;
        }

        var expectedSize = compressed
            ? CompressedTransformInterpolatorSize
            : TransformInterpolatorSize;
        if (string.IsNullOrWhiteSpace(nodeName) ||
            !HasExactReadableSpan(data, interpolator, expectedSize))
        {
            return false;
        }

        var be = nif.IsBigEndian;
        var offset = interpolator.DataOffset;
        var startTime = BinaryUtils.ReadFloat(data, offset, be);
        var stopTime = BinaryUtils.ReadFloat(data, offset + 4, be);
        if (!float.IsFinite(startTime) || !float.IsFinite(stopTime) || stopTime <= startTime)
        {
            return false;
        }

        if (!TryReadOptionalDefaults(
                data,
                offset,
                be,
                out var defaultTranslation,
                out var defaultRotation,
                out var defaultScale))
        {
            return false;
        }

        var splineDataRef = BinaryUtils.ReadInt32(data, offset + 8, be);
        var basisDataRef = BinaryUtils.ReadInt32(data, offset + 12, be);
        var translationHandle = BinaryUtils.ReadUInt32(data, offset + 48, be);
        var rotationHandle = BinaryUtils.ReadUInt32(data, offset + 52, be);
        var scaleHandle = BinaryUtils.ReadUInt32(data, offset + 56, be);
        var hasTranslationCurve = translationHandle != AbsentChannelHandle;
        var hasRotationCurve = rotationHandle != AbsentChannelHandle;
        var hasScaleCurve = scaleHandle != AbsentChannelHandle;
        var hasCurve = hasTranslationCurve || hasRotationCurve || hasScaleCurve;

        Vector3[]? translationControlPoints = null;
        Quaternion[]? rotationControlPoints = null;
        float[]? scaleControlPoints = null;
        if (hasCurve)
        {
            if (!TryReadBasisCount(data, nif, basisDataRef, out var controlPointCount) ||
                !TryReadDataStore(data, nif, splineDataRef, out var store))
            {
                return false;
            }

            var requiredScalars =
                (hasTranslationCurve ? 3L * controlPointCount : 0L) +
                (hasRotationCurve ? 4L * controlPointCount : 0L) +
                (hasScaleCurve ? controlPointCount : 0L);
            if (requiredScalars > MaximumDecodedScalarCount - decodedScalarCount)
            {
                return false;
            }

            if (compressed)
            {
                if (!TryReadCompressionScalars(
                        data,
                        offset,
                        be,
                        hasTranslationCurve,
                        hasRotationCurve,
                        hasScaleCurve,
                        out var compression) ||
                    !TryReadCompressedChannels(
                        data,
                        store,
                        controlPointCount,
                        translationHandle,
                        rotationHandle,
                        scaleHandle,
                        compression,
                        out translationControlPoints,
                        out rotationControlPoints,
                        out scaleControlPoints))
                {
                    return false;
                }
            }
            else if (!TryReadFloatChannels(
                         data,
                         store,
                         controlPointCount,
                         translationHandle,
                         rotationHandle,
                         scaleHandle,
                         be,
                         out translationControlPoints,
                         out rotationControlPoints,
                         out scaleControlPoints))
            {
                return false;
            }

            decodedScalarCount += requiredScalars;
        }
        else if (!RefsAreOptionalAndWellTyped(nif, splineDataRef, basisDataRef))
        {
            return false;
        }

        var transform = new NifBsplineTransformData(
            startTime,
            stopTime,
            defaultTranslation,
            defaultRotation,
            defaultScale,
            translationControlPoints,
            rotationControlPoints,
            scaleControlPoints);
        if (!transform.HasAnyValue)
        {
            return false;
        }

        track = new NifNameTargetedBsplineTransformTrack(nodeName, transform);
        return true;
    }

    /// <summary>
    ///     Reads any of the six 20.2.0.7 NiBSpline*Interpolator blocks losslessly (<see cref="NifBsplineInterpolatorView" />):
    ///     Start and Stop Time, the Spline Data and Basis Data refs, the static value, each channel's Handle and, on the Comp
    ///     forms, each channel's Offset and Half Range, all as raw bits in file order. The block must have its type's exact
    ///     size (NiBSplineInterpolator's 16 bytes, the static value, 4 per handle, 8 per Comp channel). Nothing else is
    ///     validated: sentinel statics, absent handles, a negative Half Range and unresolved refs are all kept as stored.
    ///     <see cref="TryRead" />, the renderer's reader, is unchanged and still decodes the transform forms on its own.
    /// </summary>
    internal static bool TryReadInterpolatorView(
        byte[] data,
        NifInfo nif,
        BlockInfo block,
        [NotNullWhen(true)] out NifBsplineInterpolatorView? view)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(nif);
        ArgumentNullException.ThrowIfNull(block);
        view = null;
        if (nif.BinaryVersion <= LastVersionWithTrsValidFlags ||
            !TryGetInterpolatorShape(block.TypeName, out var kind, out var compact))
        {
            return false;
        }

        var staticWordCount = kind switch
        {
            NifBsplineInterpolatorKind.Float => 1,
            NifBsplineInterpolatorKind.Point3 => 3,
            _ => 8
        };
        var channelCount = kind == NifBsplineInterpolatorKind.Transform ? 3 : 1;
        var size = BsplineInterpolatorBaseSize + 4 * staticWordCount + 4 * channelCount +
                   (compact ? 8 * channelCount : 0);
        if (!HasExactReadableSpan(data, block, size))
        {
            return false;
        }

        var be = nif.IsBigEndian;
        var pos = block.DataOffset;
        var startTimeBits = BinaryUtils.ReadUInt32(data, pos, be);
        var stopTimeBits = BinaryUtils.ReadUInt32(data, pos + 4, be);
        var splineDataRef = BinaryUtils.ReadInt32(data, pos + 8, be);
        var basisDataRef = BinaryUtils.ReadInt32(data, pos + 12, be);
        pos += BsplineInterpolatorBaseSize;

        var staticValueBits = new uint[staticWordCount];
        for (var index = 0; index < staticValueBits.Length; index++)
        {
            staticValueBits[index] = BinaryUtils.ReadUInt32(data, pos, be);
            pos += 4;
        }

        var handles = new uint[channelCount];
        for (var index = 0; index < handles.Length; index++)
        {
            handles[index] = BinaryUtils.ReadUInt32(data, pos, be);
            pos += 4;
        }

        uint[] offsetBits = [];
        uint[] halfRangeBits = [];
        if (compact)
        {
            // Offset then Half Range, per channel in channel order (translation, rotation, scale).
            offsetBits = new uint[channelCount];
            halfRangeBits = new uint[channelCount];
            for (var index = 0; index < channelCount; index++)
            {
                offsetBits[index] = BinaryUtils.ReadUInt32(data, pos, be);
                halfRangeBits[index] = BinaryUtils.ReadUInt32(data, pos + 4, be);
                pos += 8;
            }
        }

        view = new NifBsplineInterpolatorView(
            block.TypeName,
            kind,
            compact,
            startTimeBits,
            stopTimeBits,
            splineDataRef,
            basisDataRef,
            staticValueBits,
            handles,
            offsetBits,
            halfRangeBits);
        return true;
    }

    /// <summary>
    ///     Reads an NiBSplineData block losslessly (<see cref="NifBsplineDataView" />): both control-point arrays whole,
    ///     floats as raw bits and compact points as raw Int16. Both arrays must fit inside the block; exact consumption is
    ///     reported by the view, not required here.
    /// </summary>
    internal static bool TryReadDataView(byte[] data, NifInfo nif, BlockInfo block, out NifBsplineDataView view)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(nif);
        ArgumentNullException.ThrowIfNull(block);
        view = default;
        if (block.TypeName != "NiBSplineData" || !HasReadableSpan(data, block, sizeof(uint) * 2))
        {
            return false;
        }

        var be = nif.IsBigEndian;
        var blockEnd = (long)block.DataOffset + block.Size;
        var floatCount = BinaryUtils.ReadUInt32(data, block.DataOffset, be);
        var compactCountOffset = (long)block.DataOffset + sizeof(uint) + (long)floatCount * sizeof(float);
        if (compactCountOffset + sizeof(uint) > blockEnd)
        {
            return false;
        }

        var compactCount = BinaryUtils.ReadUInt32(data, (int)compactCountOffset, be);
        var compactStart = compactCountOffset + sizeof(uint);
        if (compactStart + (long)compactCount * sizeof(short) > blockEnd)
        {
            return false;
        }

        view = new NifBsplineDataView(
            data,
            be,
            block.DataOffset + sizeof(uint),
            (int)floatCount,
            (int)compactStart,
            (int)compactCount,
            (int)blockEnd);
        return true;
    }

    /// <summary>
    ///     Reads an NiBSplineBasisData block's Num Control Points as stored, without the renderer's range check
    ///     (<see cref="NifOpenUniformCubicBspline.MinimumControlPointCount" /> to <see cref="MaximumControlPointCount" />).
    /// </summary>
    internal static bool TryReadBasisView(byte[] data, NifInfo nif, BlockInfo block, out uint numControlPoints)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(nif);
        ArgumentNullException.ThrowIfNull(block);
        numControlPoints = 0;
        if (block.TypeName != "NiBSplineBasisData" || !HasReadableSpan(data, block, sizeof(uint)))
        {
            return false;
        }

        numControlPoints = BinaryUtils.ReadUInt32(data, block.DataOffset, nif.IsBigEndian);
        return true;
    }

    private static bool TryGetInterpolatorShape(
        string typeName,
        out NifBsplineInterpolatorKind kind,
        out bool compact)
    {
        switch (typeName)
        {
            case "NiBSplineFloatInterpolator":
                (kind, compact) = (NifBsplineInterpolatorKind.Float, false);
                return true;
            case "NiBSplineCompFloatInterpolator":
                (kind, compact) = (NifBsplineInterpolatorKind.Float, true);
                return true;
            case "NiBSplinePoint3Interpolator":
                (kind, compact) = (NifBsplineInterpolatorKind.Point3, false);
                return true;
            case "NiBSplineCompPoint3Interpolator":
                (kind, compact) = (NifBsplineInterpolatorKind.Point3, true);
                return true;
            case "NiBSplineTransformInterpolator":
                (kind, compact) = (NifBsplineInterpolatorKind.Transform, false);
                return true;
            case "NiBSplineCompTransformInterpolator":
                (kind, compact) = (NifBsplineInterpolatorKind.Transform, true);
                return true;
            default:
                (kind, compact) = (default, false);
                return false;
        }
    }

    private static bool TryReadOptionalDefaults(
        byte[] data,
        int offset,
        bool be,
        out Vector3? translation,
        out Quaternion? rotation,
        out float? scale)
    {
        translation = null;
        rotation = null;
        scale = null;

        var rawTranslation = new Vector3(
            BinaryUtils.ReadFloat(data, offset + 16, be),
            BinaryUtils.ReadFloat(data, offset + 20, be),
            BinaryUtils.ReadFloat(data, offset + 24, be));
        if (!TryClassifyOptional(
                [rawTranslation.X, rawTranslation.Y, rawTranslation.Z],
                out var hasTranslation))
        {
            return false;
        }

        if (hasTranslation)
        {
            translation = rawTranslation;
        }

        var rawRotation = new Quaternion(
            BinaryUtils.ReadFloat(data, offset + 32, be),
            BinaryUtils.ReadFloat(data, offset + 36, be),
            BinaryUtils.ReadFloat(data, offset + 40, be),
            BinaryUtils.ReadFloat(data, offset + 28, be));
        if (!TryClassifyOptional(
                [rawRotation.X, rawRotation.Y, rawRotation.Z, rawRotation.W],
                out var hasRotation))
        {
            return false;
        }

        if (hasRotation)
        {
            var lengthSquared = rawRotation.LengthSquared();
            if (!float.IsFinite(lengthSquared) || lengthSquared <= 1e-12f)
            {
                return false;
            }

            rotation = rawRotation;
        }

        var rawScale = BinaryUtils.ReadFloat(data, offset + 44, be);
        if (IsFiniteAuthored(rawScale))
        {
            scale = rawScale;
        }
        else if (!float.IsFinite(rawScale) || MathF.Abs(rawScale) < 1e30f)
        {
            return false;
        }

        return true;
    }

    private static bool TryClassifyOptional(float[] values, out bool hasValue)
    {
        hasValue = values.All(IsFiniteAuthored);
        if (hasValue)
        {
            return true;
        }

        return values.All(static value =>
            float.IsFinite(value) && MathF.Abs(value) >= 1e30f);
    }

    private static bool TryReadBasisCount(
        byte[] data,
        NifInfo nif,
        int basisDataRef,
        out int count)
    {
        count = 0;
        if (basisDataRef < 0 || basisDataRef >= nif.Blocks.Count)
        {
            return false;
        }

        var block = nif.Blocks[basisDataRef];
        if (block.TypeName != "NiBSplineBasisData" ||
            !HasExactReadableSpan(data, block, sizeof(uint)))
        {
            return false;
        }

        var raw = BinaryUtils.ReadUInt32(data, block.DataOffset, nif.IsBigEndian);
        if (raw is < NifOpenUniformCubicBspline.MinimumControlPointCount or
            > MaximumControlPointCount)
        {
            return false;
        }

        count = (int)raw;
        return true;
    }

    private static bool TryReadDataStore(
        byte[] data,
        NifInfo nif,
        int splineDataRef,
        out DataStore store)
    {
        store = default;
        if (splineDataRef < 0 || splineDataRef >= nif.Blocks.Count)
        {
            return false;
        }

        var block = nif.Blocks[splineDataRef];
        if (block.TypeName != "NiBSplineData" || !HasReadableSpan(data, block, sizeof(uint) * 2))
        {
            return false;
        }

        var be = nif.IsBigEndian;
        var floatCount = BinaryUtils.ReadUInt32(data, block.DataOffset, be);
        var compactCountOffset = (long)block.DataOffset + sizeof(uint) + (long)floatCount * sizeof(float);
        if (compactCountOffset < block.DataOffset ||
            compactCountOffset + sizeof(uint) > (long)block.DataOffset + block.Size)
        {
            return false;
        }

        var compactCount = BinaryUtils.ReadUInt32(data, (int)compactCountOffset, be);
        var compactStart = compactCountOffset + sizeof(uint);
        var expectedEnd = compactStart + (long)compactCount * sizeof(short);
        if (floatCount > int.MaxValue || compactCount > int.MaxValue ||
            expectedEnd != (long)block.DataOffset + block.Size ||
            expectedEnd > data.LongLength)
        {
            return false;
        }

        store = new DataStore(
            block.DataOffset + sizeof(uint),
            (int)floatCount,
            (int)compactStart,
            (int)compactCount,
            be);
        return true;
    }

    private static bool TryReadCompressionScalars(
        byte[] data,
        int offset,
        bool be,
        bool hasTranslation,
        bool hasRotation,
        bool hasScale,
        out CompressionScalars scalars)
    {
        scalars = new CompressionScalars(
            BinaryUtils.ReadFloat(data, offset + 60, be),
            BinaryUtils.ReadFloat(data, offset + 64, be),
            BinaryUtils.ReadFloat(data, offset + 68, be),
            BinaryUtils.ReadFloat(data, offset + 72, be),
            BinaryUtils.ReadFloat(data, offset + 76, be),
            BinaryUtils.ReadFloat(data, offset + 80, be));
        return ValidCompression(scalars.TranslationBias, scalars.TranslationMultiplier, hasTranslation) &&
               ValidCompression(scalars.RotationBias, scalars.RotationMultiplier, hasRotation) &&
               ValidCompression(scalars.ScaleBias, scalars.ScaleMultiplier, hasScale);
    }

    private static bool ValidCompression(float bias, float multiplier, bool active)
    {
        return !active || (IsFiniteAuthored(bias) && IsFiniteAuthored(multiplier) && multiplier >= 0f);
    }

    private static bool TryReadCompressedChannels(
        byte[] data,
        in DataStore store,
        int count,
        uint translationHandle,
        uint rotationHandle,
        uint scaleHandle,
        in CompressionScalars compression,
        out Vector3[]? translations,
        out Quaternion[]? rotations,
        out float[]? scales)
    {
        translations = null;
        rotations = null;
        scales = null;
        if (translationHandle != AbsentChannelHandle)
        {
            if (!RangeFits(translationHandle, count, 3, store.CompactCount))
            {
                return false;
            }

            translations = new Vector3[count];
            for (var index = 0; index < count; index++)
            {
                var scalar = translationHandle + (uint)(index * 3);
                var value = new Vector3(
                    ReadCompressed(data, store, scalar, compression.TranslationBias,
                        compression.TranslationMultiplier),
                    ReadCompressed(data, store, scalar + 1, compression.TranslationBias,
                        compression.TranslationMultiplier),
                    ReadCompressed(data, store, scalar + 2, compression.TranslationBias,
                        compression.TranslationMultiplier));
                if (!IsFiniteAuthored(value))
                {
                    return false;
                }

                translations[index] = value;
            }
        }

        if (rotationHandle != AbsentChannelHandle)
        {
            if (!RangeFits(rotationHandle, count, 4, store.CompactCount))
            {
                return false;
            }

            rotations = new Quaternion[count];
            for (var index = 0; index < count; index++)
            {
                var scalar = rotationHandle + (uint)(index * 4);
                var w = ReadCompressed(data, store, scalar, compression.RotationBias,
                    compression.RotationMultiplier);
                var value = new Quaternion(
                    ReadCompressed(data, store, scalar + 1, compression.RotationBias,
                        compression.RotationMultiplier),
                    ReadCompressed(data, store, scalar + 2, compression.RotationBias,
                        compression.RotationMultiplier),
                    ReadCompressed(data, store, scalar + 3, compression.RotationBias,
                        compression.RotationMultiplier),
                    w);
                var lengthSquared = value.LengthSquared();
                if (!IsFiniteAuthored(value) || !float.IsFinite(lengthSquared) || lengthSquared <= 1e-12f)
                {
                    return false;
                }

                rotations[index] = value;
            }
        }

        if (scaleHandle != AbsentChannelHandle)
        {
            if (!RangeFits(scaleHandle, count, 1, store.CompactCount))
            {
                return false;
            }

            scales = new float[count];
            for (var index = 0; index < count; index++)
            {
                var value = ReadCompressed(
                    data,
                    store,
                    scaleHandle + (uint)index,
                    compression.ScaleBias,
                    compression.ScaleMultiplier);
                if (!IsFiniteAuthored(value))
                {
                    return false;
                }

                scales[index] = value;
            }
        }

        return true;
    }

    private static bool TryReadFloatChannels(
        byte[] data,
        in DataStore store,
        int count,
        uint translationHandle,
        uint rotationHandle,
        uint scaleHandle,
        bool be,
        out Vector3[]? translations,
        out Quaternion[]? rotations,
        out float[]? scales)
    {
        translations = null;
        rotations = null;
        scales = null;
        if (translationHandle != AbsentChannelHandle)
        {
            if (!RangeFits(translationHandle, count, 3, store.FloatCount))
            {
                return false;
            }

            translations = new Vector3[count];
            for (var index = 0; index < count; index++)
            {
                var scalar = translationHandle + (uint)(index * 3);
                var value = new Vector3(
                    ReadFloat(data, store, scalar, be),
                    ReadFloat(data, store, scalar + 1, be),
                    ReadFloat(data, store, scalar + 2, be));
                if (!IsFiniteAuthored(value))
                {
                    return false;
                }

                translations[index] = value;
            }
        }

        if (rotationHandle != AbsentChannelHandle)
        {
            if (!RangeFits(rotationHandle, count, 4, store.FloatCount))
            {
                return false;
            }

            rotations = new Quaternion[count];
            for (var index = 0; index < count; index++)
            {
                var scalar = rotationHandle + (uint)(index * 4);
                var w = ReadFloat(data, store, scalar, be);
                var value = new Quaternion(
                    ReadFloat(data, store, scalar + 1, be),
                    ReadFloat(data, store, scalar + 2, be),
                    ReadFloat(data, store, scalar + 3, be),
                    w);
                var lengthSquared = value.LengthSquared();
                if (!IsFiniteAuthored(value) || !float.IsFinite(lengthSquared) || lengthSquared <= 1e-12f)
                {
                    return false;
                }

                rotations[index] = value;
            }
        }

        if (scaleHandle != AbsentChannelHandle)
        {
            if (!RangeFits(scaleHandle, count, 1, store.FloatCount))
            {
                return false;
            }

            scales = new float[count];
            for (var index = 0; index < count; index++)
            {
                var value = ReadFloat(data, store, scaleHandle + (uint)index, be);
                if (!IsFiniteAuthored(value))
                {
                    return false;
                }

                scales[index] = value;
            }
        }

        return true;
    }

    private static float ReadCompressed(
        byte[] data,
        in DataStore store,
        uint scalar,
        float bias,
        float multiplier)
    {
        var value = BinaryUtils.ReadInt16(
            data,
            store.CompactStart + checked((int)scalar * sizeof(short)),
            store.BigEndian);
        return bias + value / 32767f * multiplier;
    }

    private static float ReadFloat(byte[] data, in DataStore store, uint scalar, bool be)
    {
        return BinaryUtils.ReadFloat(
            data,
            store.FloatStart + checked((int)scalar * sizeof(float)),
            be);
    }

    private static bool RangeFits(uint handle, int count, int dimension, int availableScalars)
    {
        var end = handle + (ulong)(uint)count * (uint)dimension;
        return handle != AbsentChannelHandle && end <= (uint)availableScalars;
    }

    private static bool RefsAreOptionalAndWellTyped(NifInfo nif, int dataRef, int basisRef)
    {
        return OptionalRefIsTyped(nif, dataRef, "NiBSplineData") &&
               OptionalRefIsTyped(nif, basisRef, "NiBSplineBasisData");
    }

    private static bool OptionalRefIsTyped(NifInfo nif, int blockRef, string typeName)
    {
        return blockRef == -1 ||
               (blockRef >= 0 && blockRef < nif.Blocks.Count &&
                nif.Blocks[blockRef].TypeName == typeName);
    }

    private static bool HasExactReadableSpan(byte[] data, BlockInfo block, int exactSize)
    {
        return block.Size == exactSize && HasReadableSpan(data, block, exactSize);
    }

    private static bool HasReadableSpan(byte[] data, BlockInfo block, int minimumSize)
    {
        return block.DataOffset >= 0 && block.Size >= minimumSize &&
               (long)block.DataOffset + block.Size <= data.LongLength;
    }

    private static bool IsFiniteAuthored(float value)
    {
        return float.IsFinite(value) && MathF.Abs(value) < 1e30f;
    }

    private static bool IsFiniteAuthored(Vector3 value)
    {
        return IsFiniteAuthored(value.X) && IsFiniteAuthored(value.Y) && IsFiniteAuthored(value.Z);
    }

    private static bool IsFiniteAuthored(Quaternion value)
    {
        return IsFiniteAuthored(value.X) && IsFiniteAuthored(value.Y) &&
               IsFiniteAuthored(value.Z) && IsFiniteAuthored(value.W);
    }

    private readonly record struct DataStore(
        int FloatStart,
        int FloatCount,
        int CompactStart,
        int CompactCount,
        bool BigEndian);

    private readonly record struct CompressionScalars(
        float TranslationBias,
        float TranslationMultiplier,
        float RotationBias,
        float RotationMultiplier,
        float ScaleBias,
        float ScaleMultiplier);
}
