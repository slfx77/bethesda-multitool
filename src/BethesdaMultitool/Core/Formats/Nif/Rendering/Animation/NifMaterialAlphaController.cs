using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     One manager-controlled <c>NiAlphaController</c> bound to an <c>NiMaterialProperty</c>.
///     The two Gamebryo clocks are retained separately: the selected <c>NiControllerSequence</c>
///     maps renderer time first, then the controller applies its own frequency/phase/window.
/// </summary>
internal sealed record NifMaterialAlphaController(
    int MaterialPropertyRef,
    string TargetName,
    NifKeyInterpolation Interpolation,
    NifFloatKey[] Keys,
    float? ConstantValue,
    NifAlphaControllerClock SequenceClock,
    NifAlphaControllerClock ControllerClock)
{
    /// <summary>
    ///     Samples the authored target-property alpha value. Values are bounded to the legal material
    ///     opacity range so malformed animation data cannot amplify a transparent draw.
    /// </summary>
    internal float Sample(float rendererTimeSeconds)
    {
        var time = float.IsFinite(rendererTimeSeconds) ? rendererTimeSeconds : 0f;
        time = SequenceClock.Map(time);
        time = ControllerClock.Map(time);

        var value = Keys.Length > 0
            ? SampleKeys(time)
            : ConstantValue.GetValueOrDefault(1f);
        return float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 1f;
    }

    /// <summary>
    ///     Resolves the alpha value written to the target <c>NiMaterialProperty</c>. The controller
    ///     REPLACES that property's stored alpha; it is not a multiplier for the static value loaded
    ///     from the NIF. FNV's recovered <c>NiAlphaController::Update</c> (0x82DCB390) writes the
    ///     interpolator result directly to target + 0x3C and increments the target revision at + 0x44.
    ///     This distinction is load-bearing for <c>SandDust02</c>: its two storm-sheet materials start
    ///     at alpha zero and are made visible solely by their controller keys. Turning animation off
    ///     parks the sequence at its authored time-zero frame, matching the renderer's other clocks.
    /// </summary>
    internal float ResolveTargetAlpha(float rendererTimeSeconds, bool animationsEnabled)
    {
        return Sample(animationsEnabled ? rendererTimeSeconds : 0f);
    }

    private float SampleKeys(float time)
    {
        if (Keys.Length == 1 || time <= Keys[0].Time)
        {
            return Keys[0].Value;
        }

        for (var i = 1; i < Keys.Length; i++)
        {
            var next = Keys[i];
            if (time >= next.Time)
            {
                continue;
            }

            var previous = Keys[i - 1];
            if (Interpolation == NifKeyInterpolation.Constant)
            {
                return previous.Value;
            }

            // LINEAR is exact. QUADRATIC/TBC retain their authored basis in Interpolation but use
            // the same explicitly-labelled linear stand-in as the existing NIF track sampler.
            var span = next.Time - previous.Time;
            var fraction = span <= 1e-6f
                ? 1f
                : Math.Clamp((time - previous.Time) / span, 0f, 1f);
            return float.Lerp(previous.Value, next.Value, fraction);
        }

        return Keys[^1].Value;
    }
}

/// <summary>A single NiTimeController-style clock and cycle window.</summary>
internal readonly record struct NifAlphaControllerClock(
    float Frequency,
    float Phase,
    float StartTime,
    float StopTime,
    NifCycleType Cycle)
{
    internal float Map(float timeSeconds)
    {
        var frequency = float.IsFinite(Frequency) ? Frequency : 1f;
        var phase = float.IsFinite(Phase) ? Phase : 0f;
        var localTime = timeSeconds * frequency + phase;
        if (!float.IsFinite(StartTime) || !float.IsFinite(StopTime) || StopTime <= StartTime)
        {
            return float.IsFinite(localTime) ? localTime : 0f;
        }

        if (!float.IsFinite(localTime))
        {
            return localTime > 0f ? StopTime : StartTime;
        }

        var length = StopTime - StartTime;
        return Cycle switch
        {
            NifCycleType.Loop => StartTime + PositiveModulo(localTime - StartTime, length),
            NifCycleType.Reverse => MapReverse(localTime, length),
            _ => Math.Clamp(localTime, StartTime, StopTime)
        };
    }

    private float MapReverse(float localTime, float length)
    {
        var offset = PositiveModulo(localTime - StartTime, length * 2f);
        return offset <= length
            ? StartTime + offset
            : StopTime - (offset - length);
    }

    private static float PositiveModulo(float value, float modulus)
    {
        var result = value % modulus;
        return result < 0f ? result + modulus : result;
    }
}

/// <summary>
///     Collects the Bethesda 20.2.0.7 string-table form used by Fallout 3/New Vegas ambient effects.
///     Only an Idle-named sequence auto-plays, and a present BSXFlags block must carry the Animated bit.
///     This is deliberately table-driven; it never scans arbitrary aligned words for controller refs.
/// </summary>
internal static class NifMaterialAlphaControllerCollector
{
    private const int ControlledBlockStride = 29;
    private const int NodeNameFieldOffset = 9;
    private const int PropertyTypeFieldOffset = 13;
    private const int ControllerTypeFieldOffset = 17;
    private const int SequenceTailSize = 32;
    private const int MaxControlledBlocks = 512;
    private const float SentinelMagnitude = 1e30f;

    internal static IReadOnlyList<NifMaterialAlphaController> Collect(byte[] data, NifInfo nif)
    {
        if (nif.BinaryVersion != NifVersions.Gamebryo202007 || nif.BsVersion == 0 ||
            (ReadBsxFlags(data, nif) is { } bsxFlags && (bsxFlags & 0x1) == 0))
        {
            return [];
        }

        var sequence = SelectIdleSequence(data, nif);
        if (sequence is null || sequence.Size < 12)
        {
            return [];
        }

        var be = nif.IsBigEndian;
        var end = Math.Min(data.Length, sequence.DataOffset + sequence.Size);
        var count = BinaryUtils.ReadUInt32(data, sequence.DataOffset + 4, be);
        if (count == 0 || count > MaxControlledBlocks)
        {
            return [];
        }

        var controlledStart = sequence.DataOffset + 12;
        var tailLong = controlledStart + (long)count * ControlledBlockStride;
        if (tailLong > int.MaxValue)
        {
            return [];
        }

        var tail = (int)tailLong;
        if (tail + SequenceTailSize > end)
        {
            return [];
        }

        var sequenceClock = new NifAlphaControllerClock(
            BinaryUtils.ReadFloat(data, tail + 12, be),
            0f,
            BinaryUtils.ReadFloat(data, tail + 16, be),
            BinaryUtils.ReadFloat(data, tail + 20, be),
            ReadCycle(BinaryUtils.ReadInt32(data, tail + 8, be)));

        var result = new List<NifMaterialAlphaController>((int)count);
        for (var i = 0; i < count; i++)
        {
            var controlled = controlledStart + i * ControlledBlockStride;
            var interpolatorRef = BinaryUtils.ReadInt32(data, controlled, be);
            var controllerRef = BinaryUtils.ReadInt32(data, controlled + 4, be);
            if (!TryReadString(data, nif, controlled + NodeNameFieldOffset, out var targetName) ||
                !TryReadString(data, nif, controlled + PropertyTypeFieldOffset, out var propertyType) ||
                !TryReadString(data, nif, controlled + ControllerTypeFieldOffset, out var controllerType) ||
                !string.Equals(propertyType, "NiMaterialProperty", StringComparison.Ordinal) ||
                !string.Equals(controllerType, "NiAlphaController", StringComparison.Ordinal) ||
                !TryReadController(data, nif, controllerRef, out var propertyRef, out var controllerClock) ||
                !TryReadInterpolator(data, nif, interpolatorRef, out var interpolation, out var keys,
                    out var constantValue))
            {
                continue;
            }

            result.Add(new NifMaterialAlphaController(
                propertyRef, targetName, interpolation, keys, constantValue,
                sequenceClock, controllerClock));
        }

        return result;
    }

    private static bool TryReadController(
        byte[] data,
        NifInfo nif,
        int controllerRef,
        out int materialPropertyRef,
        out NifAlphaControllerClock clock)
    {
        materialPropertyRef = -1;
        clock = default;
        if (controllerRef < 0 || controllerRef >= nif.Blocks.Count ||
            nif.Blocks[controllerRef].TypeName != "NiAlphaController" ||
            !NifTimeControllerReader.TryRead(
                data, nif.Blocks[controllerRef], nif.IsBigEndian, out var header) ||
            !header.IsActive || header.TargetRef < 0 || header.TargetRef >= nif.Blocks.Count ||
            nif.Blocks[header.TargetRef].TypeName != "NiMaterialProperty")
        {
            return false;
        }

        materialPropertyRef = header.TargetRef;
        clock = new NifAlphaControllerClock(
            header.Frequency, header.Phase, header.StartTime, header.StopTime, header.CycleType);
        return true;
    }

    private static bool TryReadInterpolator(
        byte[] data,
        NifInfo nif,
        int interpolatorRef,
        out NifKeyInterpolation interpolation,
        out NifFloatKey[] keys,
        out float? constantValue)
    {
        interpolation = NifKeyInterpolation.Linear;
        keys = [];
        constantValue = null;
        if (interpolatorRef < 0 || interpolatorRef >= nif.Blocks.Count)
        {
            return false;
        }

        var interpolator = nif.Blocks[interpolatorRef];
        if (interpolator.TypeName != "NiFloatInterpolator" || interpolator.Size < 8)
        {
            return false;
        }

        var be = nif.IsBigEndian;
        var poseValue = BinaryUtils.ReadFloat(data, interpolator.DataOffset, be);
        var dataRef = BinaryUtils.ReadInt32(data, interpolator.DataOffset + 4, be);
        if (dataRef >= 0 && dataRef < nif.Blocks.Count &&
            nif.Blocks[dataRef].TypeName == "NiFloatData")
        {
            var floatData = nif.Blocks[dataRef];
            var pos = floatData.DataOffset;
            var end = Math.Min(data.Length, floatData.DataOffset + floatData.Size);
            if (NifKeyGroupReader.TryReadFloatKeys(
                    data, ref pos, end, be, out interpolation, out keys) &&
                keys.Length > 0 && IsValidKeyGroup(interpolation, keys))
            {
                return true;
            }
        }

        if (!float.IsFinite(poseValue) || MathF.Abs(poseValue) >= SentinelMagnitude)
        {
            return false;
        }

        constantValue = poseValue;
        return true;
    }

    private static bool IsValidKeyGroup(NifKeyInterpolation interpolation, NifFloatKey[] keys)
    {
        if (interpolation is not (NifKeyInterpolation.Linear or NifKeyInterpolation.Quadratic
            or NifKeyInterpolation.Tbc or NifKeyInterpolation.Constant))
        {
            return false;
        }

        var priorTime = float.NegativeInfinity;
        foreach (var key in keys)
        {
            if (!float.IsFinite(key.Time) || !float.IsFinite(key.Value) || key.Time < priorTime)
            {
                return false;
            }

            priorTime = key.Time;
        }

        return true;
    }

    private static bool TryReadString(
        byte[] data, NifInfo nif, int fieldOffset, out string value)
    {
        var index = BinaryUtils.ReadInt32(data, fieldOffset, nif.IsBigEndian);
        if (index >= 0 && index < nif.Strings.Count)
        {
            value = nif.Strings[index];
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static BlockInfo? SelectIdleSequence(byte[] data, NifInfo nif)
    {
        foreach (var block in nif.Blocks)
        {
            if (block.TypeName != "NiControllerSequence" || block.Size < 4)
            {
                continue;
            }

            var nameIndex = BinaryUtils.ReadInt32(data, block.DataOffset, nif.IsBigEndian);
            if (nameIndex >= 0 && nameIndex < nif.Strings.Count &&
                nif.Strings[nameIndex].Contains("idle", StringComparison.OrdinalIgnoreCase))
            {
                return block;
            }
        }

        return null;
    }

    private static uint? ReadBsxFlags(byte[] data, NifInfo nif)
    {
        foreach (var block in nif.Blocks)
        {
            if (block.TypeName == "BSXFlags" && block.Size >= 8)
            {
                return BinaryUtils.ReadUInt32(data, block.DataOffset + 4, nif.IsBigEndian);
            }
        }

        return null;
    }

    private static NifCycleType ReadCycle(int value)
    {
        return value switch
        {
            0 => NifCycleType.Loop,
            1 => NifCycleType.Reverse,
            _ => NifCycleType.Clamp
        };
    }
}
