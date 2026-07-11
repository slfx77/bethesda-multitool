using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     The NiTimeController base header shared by every controller block (nif.xml NiTimeController):
///     next-controller ref, flags, frequency/phase, start/stop times, target ref. Type-specific fields
///     start at offset 26. Cycle behavior lives in bits 1-2 of <see cref="Flags" />
///     (0 = loop, 1 = reverse, 2 = clamp) and bit 3 is the active flag.
/// </summary>
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
}

/// <summary>NiTimeController cycle behavior (flags bits 1-2).</summary>
internal enum NifCycleType : byte
{
    Loop = 0,
    Reverse = 1,
    Clamp = 2,
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
            NextControllerRef: BinaryUtils.ReadInt32(data, pos, be),
            Flags: BinaryUtils.ReadUInt16(data, pos + 4, be),
            Frequency: BinaryUtils.ReadFloat(data, pos + 6, be),
            Phase: BinaryUtils.ReadFloat(data, pos + 10, be),
            StartTime: BinaryUtils.ReadFloat(data, pos + 14, be),
            StopTime: BinaryUtils.ReadFloat(data, pos + 18, be),
            TargetRef: BinaryUtils.ReadInt32(data, pos + 22, be));
        return true;
    }
}
