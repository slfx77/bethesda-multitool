using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     The NiTimeController base header shared by every controller block (nif.xml NiTimeController):
///     next-controller ref, flags, frequency/phase, start/stop times, target ref. Type-specific fields
///     start at offset 26. Cycle behavior lives in bits 1-2 of <see cref="Flags" />
///     (0 = loop, 1 = reverse, 2 = clamp) and bit 3 is the active flag.
/// </summary>
/// <remarks>
///     The header is lossless: <see cref="Flags" /> is the stored word, and each clock float was built from its stored bits
///     by <see cref="BitConverter.UInt32BitsToSingle" /> and only copied since, so the <c>*Bits</c> members return the
///     stored patterns exactly, the -FLT_MAX / +FLT_MAX sentinels (0xFF7FFFFF / 0x7F7FFFFF) included. The flag members
///     name every bit nif.xml's TimeControllerFlags defines; <see cref="RawCycle" /> keeps the undefined cycle value 3,
///     which <see cref="CycleType" /> casts to an undefined enum value.
/// </remarks>
internal readonly record struct NifTimeControllerHeader(
    int NextControllerRef,
    ushort Flags,
    float Frequency,
    float Phase,
    float StartTime,
    float StopTime,
    int TargetRef)
{
    /// <summary>Offset of the first type-specific field after the shared header.</summary>
    public const int HeaderSize = 26;

    public NifCycleType CycleType => (NifCycleType)((Flags & 0x6) >> 1);
    public bool IsActive => (Flags & 0x8) != 0;

    /// <summary>The stored cycle bits 1-2 as a number, 0 to 3 (3 is undefined and kept as stored).</summary>
    public int RawCycle => (Flags >> 1) & 0x3;

    /// <summary>Bit 0, the animation type: APP_INIT when set, APP_TIME when clear.</summary>
    public bool IsAppInit => (Flags & 0x1) != 0;

    /// <summary>Bit 4, play backwards.</summary>
    public bool PlayBackwards => (Flags & 0x10) != 0;

    /// <summary>Bit 5, manager controlled (the controller is driven through an NiControllerSequence).</summary>
    public bool IsManagerControlled => (Flags & 0x20) != 0;

    /// <summary>Bit 6, compute scaled time.</summary>
    public bool ComputeScaledTime => (Flags & 0x40) != 0;

    /// <summary>The stored bits of <see cref="Frequency" />.</summary>
    public uint FrequencyBits => BitConverter.SingleToUInt32Bits(Frequency);

    /// <summary>The stored bits of <see cref="Phase" />.</summary>
    public uint PhaseBits => BitConverter.SingleToUInt32Bits(Phase);

    /// <summary>The stored bits of <see cref="StartTime" /> (0x7F7FFFFF is the +FLT_MAX sentinel).</summary>
    public uint StartTimeBits => BitConverter.SingleToUInt32Bits(StartTime);

    /// <summary>The stored bits of <see cref="StopTime" /> (0xFF7FFFFF is the -FLT_MAX sentinel).</summary>
    public uint StopTimeBits => BitConverter.SingleToUInt32Bits(StopTime);
}

/// <summary>NiTimeController cycle behavior (flags bits 1-2).</summary>
internal enum NifCycleType : byte
{
    Loop = 0,
    Reverse = 1,
    Clamp = 2
}

/// <summary>Reads the shared NiTimeController base header from any controller block.</summary>
internal static class NifTimeControllerReader
{
    internal static bool TryRead(byte[] data, BlockInfo block, bool be, out NifTimeControllerHeader header)
    {
        header = default;
        if (block.Size < NifTimeControllerHeader.HeaderSize)
        {
            return false;
        }

        var pos = block.DataOffset;
        header = new NifTimeControllerHeader(
            BinaryUtils.ReadInt32(data, pos, be),
            BinaryUtils.ReadUInt16(data, pos + 4, be),
            BinaryUtils.ReadFloat(data, pos + 6, be),
            BinaryUtils.ReadFloat(data, pos + 10, be),
            BinaryUtils.ReadFloat(data, pos + 14, be),
            BinaryUtils.ReadFloat(data, pos + 18, be),
            BinaryUtils.ReadInt32(data, pos + 22, be));
        return true;
    }
}
