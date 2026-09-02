using System.Numerics;
using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Reads Bethesda standalone-KF controller-sequence layouts: the byte-verified header-string
///     targets in 20.2.0.7 and the bundled-schema-defined palette-offset targets in little-endian
///     Oblivion 20.0.0.4/.5 BS11. Unlike the embedded-NIF collector, this reader does not choose an
///     idle clip, consult BSX flags, or invent destination object refs: a KF controlled block
///     identifies its future rig target by name.
/// </summary>
internal static class NifControllerSequenceNameTrackReader
{
    private const int ModernControlledBlockTableOffset = 12;
    private const int ModernControlledBlockStride = 29;
    private const int ModernNodeNameFieldOffset = 9;
    private const int SequenceTailSize = 32;
    private const int InterpolatorDataRefOffset = 32;
    private const int TransformInterpolatorSize = 36;
    private const int MaxControlledBlocks = 4096;
    private const int MaxInlineStringBytes = 512;

    // Oblivion 20.0.0.4/.5, BS11. The priority byte deliberately leaves every following
    // uint unaligned: Interpolator @0, Controller @4, Priority @8, Palette ref @9, then five
    // StringOffsets @13/@17/@21/@25/@29. The per-block palette ref makes this 33 bytes, not
    // the 29-byte 20.2.0.7 form which stores five header-string indices directly.
    private const int OblivionControlledBlockStride = 33;
    private const int OblivionPaletteRefOffset = 9;
    private const int OblivionNodeNameOffset = 13;
    private const uint OblivionBsVersion = 11;

    /// <summary>
    ///     Reads every structurally valid sequence. Invalid sequences fail closed and unsupported
    ///     time-domain transform interpolators are counted on the retained clip instead of being
    ///     misrepresented as playable tracks.
    /// </summary>
    internal static NifNameTargetedAnimationClip[] ReadAll(byte[] data, NifInfo nif)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(nif);

        var modern = nif.BinaryVersion == NifVersions.Gamebryo202007 && nif.BsVersion != 0;
        var oblivion = (nif.BinaryVersion is NifVersions.Gamebryo20004 or NifVersions.Gamebryo20005) &&
                       nif.BsVersion == OblivionBsVersion &&
                       nif.UserVersion is 10 or 11 &&
                       nif.HasInlineStrings &&
                       !nif.IsBigEndian;
        if (!modern && !oblivion)
        {
            return [];
        }

        var clips = new List<NifNameTargetedAnimationClip>();
        foreach (var block in nif.Blocks)
        {
            if (block.TypeName != "NiControllerSequence")
            {
                continue;
            }

            NifNameTargetedAnimationClip clip;
            var valid = modern
                ? TryReadModernSequence(data, nif, block, out clip)
                : TryReadOblivionSequence(data, nif, block, out clip);
            if (valid)
            {
                clips.Add(clip);
            }
        }

        return clips.ToArray();
    }

    private static bool TryReadModernSequence(
        byte[] data,
        NifInfo nif,
        BlockInfo sequence,
        out NifNameTargetedAnimationClip clip)
    {
        clip = null!;
        if (!HasReadableSpan(
                data,
                sequence,
                ModernControlledBlockTableOffset + SequenceTailSize))
        {
            return false;
        }

        var be = nif.IsBigEndian;
        var sequenceStart = sequence.DataOffset;
        var sequenceEnd = sequenceStart + sequence.Size;
        var nameIndex = BinaryUtils.ReadInt32(data, sequenceStart, be);
        var controlledBlockCount = BinaryUtils.ReadInt32(data, sequenceStart + 4, be);
        if (!TryResolveRequiredString(nif, nameIndex, out var name) ||
            controlledBlockCount is < 0 or > MaxControlledBlocks)
        {
            return false;
        }

        var tailLong = (long)sequenceStart + ModernControlledBlockTableOffset +
                       (long)controlledBlockCount * ModernControlledBlockStride;
        var coreEndLong = tailLong + SequenceTailSize;
        if (tailLong < sequenceStart || coreEndLong > sequenceEnd || coreEndLong > data.LongLength)
        {
            return false;
        }

        var tail = (int)tailLong;
        var coreEnd = (int)coreEndLong;
        if (!HasExpectedAnimNotesTail(data, nif, coreEnd, sequenceEnd))
        {
            return false;
        }

        var textKeysRef = BinaryUtils.ReadInt32(data, tail + 4, be);
        var rawCycle = BinaryUtils.ReadInt32(data, tail + 8, be);
        var frequency = BinaryUtils.ReadFloat(data, tail + 12, be);
        var startTime = BinaryUtils.ReadFloat(data, tail + 16, be);
        var stopTime = BinaryUtils.ReadFloat(data, tail + 20, be);
        var accumRootIndex = BinaryUtils.ReadInt32(data, tail + 28, be);
        var duration = stopTime - startTime;
        if (rawCycle is < (int)NifCycleType.Loop or > (int)NifCycleType.Clamp ||
            !float.IsFinite(frequency) || frequency < 0f ||
            !float.IsFinite(startTime) || !float.IsFinite(stopTime) ||
            !float.IsFinite(duration) || duration <= 0f)
        {
            return false;
        }

        string? accumRoot = null;
        if (accumRootIndex >= 0)
        {
            if (!TryResolveRequiredString(nif, accumRootIndex, out accumRoot))
            {
                return false;
            }
        }
        else if (accumRootIndex != -1)
        {
            return false;
        }

        if (!TryReadTextKeys(data, nif, textKeysRef, out var textKeys))
        {
            return false;
        }

        var tracks = new List<NifNodeTrack>(controlledBlockCount);
        var unsupportedTransformTrackCount = 0;
        for (var index = 0; index < controlledBlockCount; index++)
        {
            var controlledBlockStart = sequenceStart + ModernControlledBlockTableOffset +
                                       index * ModernControlledBlockStride;
            var interpolatorRef = BinaryUtils.ReadInt32(data, controlledBlockStart, be);
            var nodeNameIndex = BinaryUtils.ReadInt32(
                data,
                controlledBlockStart + ModernNodeNameFieldOffset,
                be);
            if (!TryResolveRequiredString(nif, nodeNameIndex, out var nodeName) ||
                interpolatorRef < 0 || interpolatorRef >= nif.Blocks.Count)
            {
                continue;
            }

            var interpolator = nif.Blocks[interpolatorRef];
            if (interpolator.TypeName != "NiTransformInterpolator")
            {
                if (IsUnsupportedTransformInterpolator(interpolator.TypeName))
                {
                    unsupportedTransformTrackCount++;
                }

                continue;
            }

            var track = TryReadTransformTrack(
                data,
                nif,
                interpolator,
                nodeName,
                startTime);
            if (track is not null)
            {
                tracks.Add(track);
            }
        }

        clip = new NifNameTargetedAnimationClip(
            name,
            frequency,
            startTime,
            stopTime,
            (NifCycleType)rawCycle,
            accumRoot,
            tracks.ToArray(),
            textKeys,
            unsupportedTransformTrackCount);
        return true;
    }

    /// <summary>
    ///     Reads the palette-backed standalone-KF layout used by Oblivion. The legacy NIF parser
    ///     recovers these block ranges by schema-walking native PC little-endian bytes; until that
    ///     measurement path becomes source-endian-aware, big-endian TES4 KFs are rejected at the
    ///     public gate rather than interpreted using untrustworthy block boundaries.
    /// </summary>
    private static bool TryReadOblivionSequence(
        byte[] data,
        NifInfo nif,
        BlockInfo sequence,
        out NifNameTargetedAnimationClip clip)
    {
        clip = null!;
        if (!HasReadableSpan(data, sequence, 48))
        {
            return false;
        }

        var sequenceStart = sequence.DataOffset;
        var sequenceEnd = sequenceStart + sequence.Size;
        var pos = sequenceStart;
        if (!TryReadSizedString(data, ref pos, sequenceEnd, false, out var name) ||
            string.IsNullOrWhiteSpace(name) ||
            (long)pos + 8L > sequenceEnd)
        {
            return false;
        }

        var controlledBlockCount = BinaryUtils.ReadInt32(data, pos, false);
        pos += 8; // Num Controlled Blocks + Array Grow By.
        if (controlledBlockCount is < 0 or > MaxControlledBlocks)
        {
            return false;
        }

        var controlledStart = pos;
        var tailLong = (long)controlledStart +
                       (long)controlledBlockCount * OblivionControlledBlockStride;
        // Weight through Manager (28), the AccumRoot SizedString length (4), and palette ref (4).
        if (tailLong < controlledStart || tailLong + 36L > sequenceEnd)
        {
            return false;
        }

        var tail = (int)tailLong;
        var textKeysRef = BinaryUtils.ReadInt32(data, tail + 4, false);
        var rawCycle = BinaryUtils.ReadInt32(data, tail + 8, false);
        var frequency = BinaryUtils.ReadFloat(data, tail + 12, false);
        var startTime = BinaryUtils.ReadFloat(data, tail + 16, false);
        var stopTime = BinaryUtils.ReadFloat(data, tail + 20, false);
        var duration = stopTime - startTime;
        if (rawCycle is < (int)NifCycleType.Loop or > (int)NifCycleType.Clamp ||
            !float.IsFinite(frequency) || frequency < 0f ||
            !float.IsFinite(startTime) || !float.IsFinite(stopTime) ||
            !float.IsFinite(duration) || duration <= 0f)
        {
            return false;
        }

        pos = tail + 28;
        if (!TryReadSizedString(data, ref pos, sequenceEnd, false, out var accumRootValue) ||
            (long)pos + 4L > sequenceEnd)
        {
            return false;
        }

        var sequencePaletteRef = BinaryUtils.ReadInt32(data, pos, false);
        pos += 4;
        // No fields follow String Palette in a 20.0.0.x/BS11 NiControllerSequence. Requiring the
        // exact boundary is what prevents a 29-byte modern table from accidentally passing this path.
        if (pos != sequenceEnd || !IsOptionalStringPaletteRef(nif, sequencePaletteRef))
        {
            return false;
        }

        if (!TryReadTextKeys(data, nif, textKeysRef, out var textKeys))
        {
            return false;
        }

        var tracks = new List<NifNodeTrack>(controlledBlockCount);
        var unsupportedTransformTrackCount = 0;
        for (var index = 0; index < controlledBlockCount; index++)
        {
            var controlledBlockStart = controlledStart +
                                       index * OblivionControlledBlockStride;
            var interpolatorRef = BinaryUtils.ReadInt32(data, controlledBlockStart, false);
            var paletteRef = BinaryUtils.ReadInt32(
                data,
                controlledBlockStart + OblivionPaletteRefOffset,
                false);
            var nodeNameOffset = BinaryUtils.ReadUInt32(
                data,
                controlledBlockStart + OblivionNodeNameOffset,
                false);
            if (!TryResolvePaletteString(
                    data,
                    nif,
                    paletteRef,
                    nodeNameOffset,
                    out var nodeName) ||
                interpolatorRef < 0 || interpolatorRef >= nif.Blocks.Count)
            {
                continue;
            }

            var interpolator = nif.Blocks[interpolatorRef];
            if (interpolator.TypeName != "NiTransformInterpolator")
            {
                if (IsUnsupportedTransformInterpolator(interpolator.TypeName))
                {
                    unsupportedTransformTrackCount++;
                }

                continue;
            }

            var track = TryReadTransformTrack(
                data,
                nif,
                interpolator,
                nodeName,
                startTime);
            if (track is not null)
            {
                tracks.Add(track);
            }
        }

        clip = new NifNameTargetedAnimationClip(
            name,
            frequency,
            startTime,
            stopTime,
            (NifCycleType)rawCycle,
            string.IsNullOrWhiteSpace(accumRootValue) ? null : accumRootValue,
            tracks.ToArray(),
            textKeys,
            unsupportedTransformTrackCount);
        return true;
    }

    private static NifNodeTrack? TryReadTransformTrack(
        byte[] data,
        NifInfo nif,
        BlockInfo interpolator,
        string nodeName,
        float startTime)
    {
        if (!HasReadableSpan(data, interpolator, TransformInterpolatorSize))
        {
            return null;
        }

        var be = nif.IsBigEndian;
        var pos = interpolator.DataOffset;
        var baseTranslation = new Vector3(
            BinaryUtils.ReadFloat(data, pos, be),
            BinaryUtils.ReadFloat(data, pos + 4, be),
            BinaryUtils.ReadFloat(data, pos + 8, be));
        var baseRotation = new Quaternion(
            BinaryUtils.ReadFloat(data, pos + 16, be),
            BinaryUtils.ReadFloat(data, pos + 20, be),
            BinaryUtils.ReadFloat(data, pos + 24, be),
            BinaryUtils.ReadFloat(data, pos + 12, be));
        var baseScale = BinaryUtils.ReadFloat(data, pos + 28, be);
        var dataRef = BinaryUtils.ReadInt32(data, pos + InterpolatorDataRefOffset, be);

        NifNodeTrack track;
        if (dataRef == -1)
        {
            track = EmptyTrack(nodeName);
        }
        else
        {
            if (dataRef < 0 || dataRef >= nif.Blocks.Count ||
                nif.Blocks[dataRef].TypeName != "NiTransformData" ||
                !HasReadableSpan(data, nif.Blocks[dataRef], 12))
            {
                return null;
            }

            var parsedTrack = NifKeyframeDataTrackReader.TryReadTrack(
                data,
                nif,
                dataRef,
                nodeName,
                1f,
                0f);
            if (parsedTrack is null)
            {
                // A referenced but malformed data block must not silently become a base-only track.
                return null;
            }

            track = parsedTrack;
        }

        var hasBaseTranslation = IsFiniteAuthored(baseTranslation.X) &&
                                 IsFiniteAuthored(baseTranslation.Y) &&
                                 IsFiniteAuthored(baseTranslation.Z);
        var baseRotationLengthSquared = baseRotation.LengthSquared();
        var hasBaseRotation = IsFiniteAuthored(baseRotation.X) &&
                              IsFiniteAuthored(baseRotation.Y) &&
                              IsFiniteAuthored(baseRotation.Z) &&
                              IsFiniteAuthored(baseRotation.W) &&
                              float.IsFinite(baseRotationLengthSquared) &&
                              baseRotationLengthSquared > 1e-12f;
        var hasBaseScale = IsFiniteAuthored(baseScale);

        if (track.RotationKeys.Length == 0 && !track.HasEulerRotation && hasBaseRotation)
        {
            track = track with
            {
                RotationInterpolation = NifKeyInterpolation.Constant,
                RotationKeys = [new NifQuatKey(startTime, baseRotation)]
            };
        }

        if (track.TranslationKeys.Length == 0 && hasBaseTranslation)
        {
            track = track with
            {
                TranslationInterpolation = NifKeyInterpolation.Constant,
                TranslationKeys = [new NifVec3Key(startTime, baseTranslation)]
            };
        }

        if (track.ScaleKeys.Length == 0 && hasBaseScale)
        {
            track = track with
            {
                ScaleInterpolation = NifKeyInterpolation.Constant,
                ScaleKeys = [new NifFloatKey(startTime, baseScale)]
            };
        }

        return track.HasAnyKeys && IsValidTrack(track) ? track : null;
    }

    private static NifNodeTrack EmptyTrack(string nodeName)
    {
        return new NifNodeTrack(
            nodeName,
            1f,
            0f,
            NifKeyInterpolation.Linear,
            [],
            NifKeyInterpolation.Linear,
            [],
            NifKeyInterpolation.Linear,
            []);
    }

    private static bool TryReadTextKeys(
        byte[] data,
        NifInfo nif,
        int textKeysRef,
        out NifAnimTextKey[] textKeys)
    {
        textKeys = [];
        if (textKeysRef == -1)
        {
            return true;
        }

        if (textKeysRef < 0 || textKeysRef >= nif.Blocks.Count)
        {
            return false;
        }

        var block = nif.Blocks[textKeysRef];
        if (block.TypeName != "NiTextKeyExtraData" || !HasReadableSpan(data, block, 8))
        {
            return false;
        }

        if (!NifTextKeyReader.TryReadExact(data, nif, block, out var parsed) ||
            parsed.Any(static key =>
                !float.IsFinite(key.Time) || string.IsNullOrWhiteSpace(key.Label)))
        {
            return false;
        }

        textKeys = parsed
            .Select(static key => new NifAnimTextKey(key.Time, key.Label))
            .ToArray();
        return true;
    }

    private static bool HasExpectedAnimNotesTail(
        byte[] data,
        NifInfo nif,
        int coreEnd,
        int sequenceEnd)
    {
        if (nif.BsVersion is >= 24 and <= 28)
        {
            return (long)coreEnd + 4L == sequenceEnd;
        }

        if (nif.BsVersion <= 28)
        {
            return coreEnd == sequenceEnd;
        }

        if ((long)coreEnd + 2 > sequenceEnd)
        {
            return false;
        }

        var count = BinaryUtils.ReadUInt16(data, coreEnd, nif.IsBigEndian);
        return (long)coreEnd + 2L + (long)count * 4L == sequenceEnd;
    }

    private static bool TryReadSizedString(
        byte[] data,
        ref int pos,
        int end,
        bool be,
        out string value)
    {
        value = string.Empty;
        if (pos < 0 || (long)pos + 4L > end || end > data.Length)
        {
            return false;
        }

        var length = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (length > MaxInlineStringBytes || (long)pos + length > end)
        {
            return false;
        }

        value = Encoding.ASCII.GetString(data, pos, (int)length);
        pos += (int)length;
        return true;
    }

    private static bool IsOptionalStringPaletteRef(NifInfo nif, int paletteRef)
    {
        return paletteRef == -1 ||
               paletteRef >= 0 && paletteRef < nif.Blocks.Count &&
               nif.Blocks[paletteRef].TypeName == "NiStringPalette";
    }

    private static bool TryResolvePaletteString(
        byte[] data,
        NifInfo nif,
        int paletteRef,
        uint stringOffset,
        out string value)
    {
        value = string.Empty;
        // Both encodings are used as the empty StringOffset sentinel by NIF tooling. A node target
        // is required, so neither is a resolvable value here.
        if (stringOffset is uint.MaxValue or 0x0000FFFF ||
            paletteRef < 0 || paletteRef >= nif.Blocks.Count)
        {
            return false;
        }

        var palette = nif.Blocks[paletteRef];
        if (palette.TypeName != "NiStringPalette" || !HasReadableSpan(data, palette, 8))
        {
            return false;
        }

        var paletteStart = palette.DataOffset;
        var paletteLength = BinaryUtils.ReadUInt32(data, paletteStart, false);
        if (paletteLength > int.MaxValue)
        {
            return false;
        }

        var payloadStart = paletteStart + 4;
        var payloadEndLong = (long)payloadStart + paletteLength;
        var repeatedLengthPosLong = payloadEndLong;
        // NiStringPalette has no padding or trailing fields: SizedString bytes followed by the
        // repeated uint length must consume the block exactly.
        if (payloadEndLong > int.MaxValue ||
            repeatedLengthPosLong + 4L != (long)paletteStart + palette.Size)
        {
            return false;
        }

        var payloadEnd = (int)payloadEndLong;
        var repeatedLengthPos = (int)repeatedLengthPosLong;
        if ((long)repeatedLengthPos + 4L > data.Length ||
            BinaryUtils.ReadUInt32(data, repeatedLengthPos, false) != paletteLength ||
            stringOffset >= paletteLength)
        {
            return false;
        }

        var stringStart = payloadStart + (int)stringOffset;
        // Offsets point to the beginning of a NUL-delimited entry, never into the middle of one.
        if (stringOffset > 0 && data[stringStart - 1] != 0)
        {
            return false;
        }

        var remaining = payloadEnd - stringStart;
        var terminator = Array.IndexOf(
            data,
            (byte)0,
            stringStart,
            Math.Min(remaining, MaxInlineStringBytes + 1));
        var byteCount = terminator - stringStart;
        if (terminator < 0 || byteCount is <= 0 or > MaxInlineStringBytes)
        {
            return false;
        }

        value = Encoding.ASCII.GetString(data, stringStart, byteCount);
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool HasReadableSpan(byte[] data, BlockInfo block, int minimumSize)
    {
        return block.DataOffset >= 0 && block.Size >= minimumSize &&
               (long)block.DataOffset + block.Size <= data.LongLength;
    }

    private static bool TryResolveRequiredString(
        NifInfo nif,
        int stringIndex,
        out string value)
    {
        if (stringIndex >= 0 && stringIndex < nif.Strings.Count &&
            !string.IsNullOrWhiteSpace(nif.Strings[stringIndex]))
        {
            value = nif.Strings[stringIndex];
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool IsUnsupportedTransformInterpolator(string typeName)
    {
        return typeName.Contains("TransformInterpolator", StringComparison.Ordinal) ||
               typeName is "BSRotAccumTransfInterpolator" or "BSTreadTransfInterpolator";
    }

    private static bool IsFiniteAuthored(float value)
    {
        return float.IsFinite(value) && MathF.Abs(value) < 1e30f;
    }

    private static bool IsValidTrack(NifNodeTrack track)
    {
        if (string.IsNullOrWhiteSpace(track.NodeName) ||
            !float.IsFinite(track.Frequency) || !float.IsFinite(track.Phase) ||
            !Enum.IsDefined(track.RotationInterpolation) ||
            !Enum.IsDefined(track.TranslationInterpolation) ||
            !Enum.IsDefined(track.ScaleInterpolation) ||
            (track.RotationInterpolation == NifKeyInterpolation.XyzEuler) != track.HasEulerRotation ||
            track.TranslationInterpolation == NifKeyInterpolation.XyzEuler ||
            track.ScaleInterpolation == NifKeyInterpolation.XyzEuler ||
            (track.RotationKeys.Length > 0 && track.HasEulerRotation))
        {
            return false;
        }

        return IsAscendingAndValid(
                   track.RotationKeys,
                   static key => key.Time,
                   static key =>
                   {
                       var lengthSquared = key.Value.LengthSquared();
                       return IsFinite(key.Value) && float.IsFinite(lengthSquared) &&
                              lengthSquared > 1e-12f;
                   }) &&
               IsAscendingAndValid(
                   track.TranslationKeys,
                   static key => key.Time,
                   static key => IsFinite(key.Value)) &&
               IsAscendingAndValid(
                   track.ScaleKeys,
                   static key => key.Time,
                   static key => float.IsFinite(key.Value)) &&
               IsAscendingAndValid(
                   track.EulerXKeys ?? [],
                   static key => key.Time,
                   static key => float.IsFinite(key.Value)) &&
               IsAscendingAndValid(
                   track.EulerYKeys ?? [],
                   static key => key.Time,
                   static key => float.IsFinite(key.Value)) &&
               IsAscendingAndValid(
                   track.EulerZKeys ?? [],
                   static key => key.Time,
                   static key => float.IsFinite(key.Value));
    }

    private static bool IsAscendingAndValid<T>(
        T[] keys,
        Func<T, float> timeSelector,
        Func<T, bool> valueValidator)
    {
        var previous = float.NegativeInfinity;
        foreach (var key in keys)
        {
            var time = timeSelector(key);
            if (!float.IsFinite(time) || time < previous || !valueValidator(key))
            {
                return false;
            }

            previous = time;
        }

        return true;
    }

    private static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    private static bool IsFinite(Quaternion value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) &&
               float.IsFinite(value.Z) && float.IsFinite(value.W);
    }
}
