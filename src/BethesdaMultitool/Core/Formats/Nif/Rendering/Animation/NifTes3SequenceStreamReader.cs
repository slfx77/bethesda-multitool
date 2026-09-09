using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Bounded TES3 external controller-cycle route. One rooted sequence helper links a text extra,
///     then name extras paired with keyframe controllers. Only a compatible active Reverse group
///     with translation-only Linear/Constant/Quadratic keys is emitted. Other controller groups
///     remain outside this explicitly named clip; no unsupported interpolation is approximated.
/// </summary>
internal static class NifTes3SequenceStreamReader
{
    private const int MaximumControllers = 4096;
    private const int MaximumNameBytes = 512;
    private const int MaximumPayloadBytes = 64 * 1024 * 1024;

    internal static NifNameTargetedAnimationClip[] ReadAll(byte[] data, NifInfo nif)
    {
        if (!TryReadHeads(data, nif, out var textBlock, out var controllerRef))
        {
            return [];
        }

        var extraRef = BinaryUtils.ReadInt32(data, textBlock.DataOffset, false);
        var extraRefs = new HashSet<int>();
        var controllerRefs = new HashSet<int>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tracks = new List<NifNodeTrack>();
        NifTimeControllerHeader? reverseClock = null;
        while (extraRef != -1 || controllerRef != -1)
        {
            if (controllerRefs.Count >= MaximumControllers ||
                !extraRefs.Add(extraRef) || !controllerRefs.Add(controllerRef) ||
                !TryGetBlock(nif, extraRef, "NiStringExtraData", out var extraBlock) ||
                !TryGetBlock(nif, controllerRef, "NiKeyframeController", out var controllerBlock) ||
                !TryReadTargetName(data, extraBlock, out var name, out var nextExtra) ||
                !names.Add(name) || controllerBlock.Size != NifTimeControllerHeader.HeaderSize + 4 ||
                !NifTimeControllerReader.TryRead(data, controllerBlock, false, out var header) ||
                !ValidClock(header))
            {
                return [];
            }

            var dataRef = NifKeyframeDataTrackReader.ReadControllerDataRef(data, controllerBlock, false);
            if (!TryGetBlock(nif, dataRef, "NiKeyframeData", out _))
            {
                return [];
            }

            if (header.CycleType == NifCycleType.Reverse)
            {
                if (!TryReadReverseTrack(data, nif, dataRef, name, header, reverseClock, out var track))
                {
                    return [];
                }

                reverseClock ??= header;
                tracks.Add(track);
            }

            extraRef = nextExtra;
            controllerRef = header.NextControllerRef;
        }

        if (reverseClock is not { } clock || tracks.Count == 0)
        {
            return [];
        }

        return
        [
            new NifNameTargetedAnimationClip(
                "External TES3 Controller Cycle",
                clock.Frequency,
                clock.StartTime,
                clock.StopTime,
                NifCycleType.Reverse,
                null,
                tracks.ToArray(),
                NifTextKeyReader.Read(data, nif, textBlock),
                0)
        ];
    }

    private static bool TryReadHeads(
        byte[] data, NifInfo nif, out BlockInfo textBlock, out int controllerRef)
    {
        textBlock = null!;
        controllerRef = -1;
        if (!TryGetRoot(data, nif, out var helper))
        {
            return false;
        }

        var position = helper.DataOffset;
        var end = position + helper.Size;
        if (!TryReadName(data, ref position, end, true, out _) || position + 8 != end)
        {
            return false;
        }

        var textRef = BinaryUtils.ReadInt32(data, position, false);
        controllerRef = BinaryUtils.ReadInt32(data, position + 4, false);
        return TryGetBlock(nif, textRef, "NiTextKeyExtraData", out textBlock) &&
               ValidTextKeys(data, textBlock);
    }

    private static bool TryReadReverseTrack(
        byte[] data, NifInfo nif, int dataRef, string name,
        NifTimeControllerHeader header, NifTimeControllerHeader? previous, out NifNodeTrack track)
    {
        track = null!;
        if (!HasExactTranslationData(data, nif.Blocks[dataRef]) ||
            (previous is { } clock && !SameClock(clock, header)))
        {
            return false;
        }

        var candidate = NifKeyframeDataTrackReader.TryReadTrack(
            data, nif, dataRef, name, header.Frequency, header.Phase);
        if (!ValidReverseTranslation(candidate))
        {
            return false;
        }

        track = candidate!;
        return true;
    }

    private static bool TryGetRoot(byte[] data, NifInfo nif, out BlockInfo helper)
    {
        helper = null!;
        if (data.Length > MaximumPayloadBytes || nif.BinaryVersion != 0x04000002 ||
            nif.IsBigEndian || nif.UserVersion != 0 || nif.BsVersion != 0 || !nif.HasInlineStrings ||
            nif.Blocks.Count is < 1 or > MaximumControllers * 3 + 2 || nif.BlockCount != nif.Blocks.Count)
        {
            return false;
        }

        var helpers = 0;
        foreach (var block in nif.Blocks)
        {
            if (block.DataOffset < 0 || block.Size < 0 || block.Size > data.Length ||
                block.DataOffset > data.Length - block.Size || block.TypeName is not
                    ("NiSequenceStreamHelper" or "NiTextKeyExtraData" or "NiStringExtraData" or
                    "NiKeyframeController" or "NiKeyframeData"))
            {
                return false;
            }

            if (block.TypeName == "NiSequenceStreamHelper")
            {
                helpers++;
            }
        }

        var last = nif.Blocks[^1];
        var footer = last.DataOffset + last.Size;
        if (helpers != 1 || footer != data.Length - 8 ||
            BinaryUtils.ReadUInt32(data, footer, false) != 1)
        {
            return false;
        }

        return TryGetBlock(nif, BinaryUtils.ReadInt32(data, footer + 4, false),
            "NiSequenceStreamHelper", out helper);
    }

    private static bool TryGetBlock(NifInfo nif, int reference, string type, out BlockInfo block)
    {
        block = null!;
        if ((uint)reference >= (uint)nif.Blocks.Count || nif.Blocks[reference].TypeName != type)
        {
            return false;
        }

        block = nif.Blocks[reference];
        return true;
    }

    private static bool TryReadTargetName(
        byte[] data, BlockInfo block, out string name, out int nextExtra)
    {
        name = string.Empty;
        nextExtra = -1;
        if (block.Size < 12 || BinaryUtils.ReadUInt32(data, block.DataOffset + 4, false) != block.Size - 8)
        {
            return false;
        }

        nextExtra = BinaryUtils.ReadInt32(data, block.DataOffset, false);
        var position = block.DataOffset + 8;
        var end = block.DataOffset + block.Size;
        return TryReadName(data, ref position, end, false, out name) && position == end;
    }

    private static bool ValidTextKeys(byte[] data, BlockInfo block)
    {
        if (block.Size < 12)
        {
            return false;
        }

        var count = BinaryUtils.ReadUInt32(data, block.DataOffset + 8, false);
        if (count > MaximumControllers)
        {
            return false;
        }

        var position = block.DataOffset + 12;
        var end = block.DataOffset + block.Size;
        for (var index = 0; index < count; index++)
        {
            if (position > end - 4 || !float.IsFinite(BinaryUtils.ReadFloat(data, position, false)))
            {
                return false;
            }

            position += 4;
            if (!TryReadName(data, ref position, end, false, out _))
            {
                return false;
            }
        }

        return position == end;
    }

    private static bool TryReadName(
        byte[] data, ref int position, int end, bool allowEmpty, out string name)
    {
        name = string.Empty;
        if (position > end - 4)
        {
            return false;
        }

        var length = BinaryUtils.ReadUInt32(data, position, false);
        position += 4;
        if (length > MaximumNameBytes || length > end - position)
        {
            return false;
        }

        for (var index = 0; index < length; index++)
        {
            if (data[position + index] is 0 or > 127)
            {
                return false;
            }
        }

        name = Encoding.ASCII.GetString(data, position, (int)length);
        position += (int)length;
        return allowEmpty || !string.IsNullOrWhiteSpace(name);
    }

    private static bool ValidClock(NifTimeControllerHeader header)
    {
        return header.IsActive && header.TargetRef == -1 && Enum.IsDefined(header.CycleType) &&
               header.Frequency.Equals(1f) && header.Phase.Equals(0f) &&
               float.IsFinite(header.StartTime) && float.IsFinite(header.StopTime) &&
               header.StopTime > header.StartTime && float.IsFinite(header.StopTime - header.StartTime);
    }

    private static bool HasExactTranslationData(byte[] data, BlockInfo block)
    {
        // The 4.0.0.2 translation-only body has zero rotation count, one Vector3 key group,
        // then zero scale count. Require exact consumption even if supplied metadata was altered.
        if (block.Size < 16 || BinaryUtils.ReadUInt32(data, block.DataOffset, false) != 0)
        {
            return false;
        }

        var count = BinaryUtils.ReadUInt32(data, block.DataOffset + 4, false);
        var basis = (NifKeyInterpolation)BinaryUtils.ReadUInt32(data, block.DataOffset + 8, false);
        var stride = basis switch
        {
            NifKeyInterpolation.Linear or NifKeyInterpolation.Constant => 16,
            NifKeyInterpolation.Quadratic => 40,
            _ => 0
        };
        return count >= 2 && stride > 0 && 16L + count * stride == block.Size &&
               BinaryUtils.ReadUInt32(data, block.DataOffset + block.Size - 4, false) == 0;
    }

    private static bool SameClock(NifTimeControllerHeader left, NifTimeControllerHeader right)
    {
        return left.Frequency.Equals(right.Frequency) && left.Phase.Equals(right.Phase) &&
               left.StartTime.Equals(right.StartTime) && left.StopTime.Equals(right.StopTime);
    }

    private static bool ValidReverseTranslation(NifNodeTrack? track)
    {
        if (track is null || track.RotationKeys.Length != 0 || track.ScaleKeys.Length != 0 ||
            track.HasEulerRotation || track.TranslationKeys.Length < 2 ||
            track.TranslationInterpolation is not
                (NifKeyInterpolation.Linear or NifKeyInterpolation.Constant or NifKeyInterpolation.Quadratic))
        {
            return false;
        }

        var previous = float.NegativeInfinity;
        foreach (var key in track.TranslationKeys)
        {
            if (!float.IsFinite(key.Time) || key.Time <= previous ||
                !NifQuadraticVectorCurve.IsFiniteAuthored(key.Value) ||
                key.HasQuadraticTangents != (track.TranslationInterpolation == NifKeyInterpolation.Quadratic) ||
                (key.HasQuadraticTangents &&
                 (!NifQuadraticVectorCurve.IsFiniteAuthored(key.Forward) ||
                  !NifQuadraticVectorCurve.IsFiniteAuthored(key.Backward))))
            {
                return false;
            }

            previous = key.Time;
        }

        return true;
    }
}
