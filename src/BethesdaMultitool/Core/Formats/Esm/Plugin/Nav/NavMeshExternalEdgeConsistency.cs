using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Nav;

/// <summary>
///     Enforces the NVTR ↔ NVEX invariant the FNV engine requires: an NVTR triangle edge may be
///     flagged EXTERNAL (triangle Flags bit 0/1/2 marks edge slot 0/1/2) only when its edge value
///     is a valid index into this navmesh's NVEX array. When the flag is set the engine reads
///     <c>NVEX[edgeValue]</c> (and the runtime <c>EdgeExtraInfo</c> it projects) to cross into the
///     linked navmesh; a flag standing over an invalid index deref's out of bounds.
///
///     <para>
///     This is the TheStripWorld AI-pathing access violation: <see cref="NavMeshAdjacencyRebuild" />
///     historically clobbered external edges to the −1 boundary sentinel while leaving the flag set,
///     and runtime-reconstructed navmeshes (<c>RuntimeNavMeshDiscovery</c>) carry NVTR external flags
///     with no NVEX array at all. Either way the engine reads <c>NVEX[-1]</c>. This pass is the
///     safety invariant run after the whole NVTR/NVEX repair chain: any external-flagged edge whose
///     value isn't a live NVEX index gets its flag cleared and the edge reset to −1 (a plain,
///     self-contained border). What survives is exactly the set of edges backed by a real NVEX link.
///     </para>
/// </summary>
internal static class NavMeshExternalEdgeConsistency
{
    private const int NvtrEntrySize = 16;
    private const int NvexEntrySize = 10;
    private const int FlagsOffset = 12;

    // Edge-slot byte offsets within an NVTR entry, indexed by edge ordinal (0=V0V1, 1=V1V2, 2=V2V0).
    private static readonly int[] EdgeSlotOffset = [6, 8, 10];

    /// <summary>
    ///     Reconciles the NVTR subrecord's external-edge flags against the NVEX entry count found in
    ///     the same subrecord list. Mutates the NVTR bytes in place. Returns the number of external
    ///     flags cleared.
    /// </summary>
    public static int Enforce(IReadOnlyList<EncodedSubrecord> subrecords)
    {
        var nvexCount = 0;
        byte[]? nvtr = null;
        foreach (var sub in subrecords)
        {
            if (sub.Signature == "NVEX")
            {
                nvexCount = sub.Bytes.Length / NvexEntrySize;
            }
            else if (sub.Signature == "NVTR")
            {
                nvtr = sub.Bytes;
            }
        }

        return nvtr is null ? 0 : ClearInvalidExternalFlags(nvtr, nvexCount);
    }

    /// <summary>
    ///     Clears every external flag in <paramref name="nvtrBytes" /> whose edge value is not a live
    ///     index in [0, <paramref name="nvexCount" />), resetting that edge to −1. Mutates in place;
    ///     returns the number of flags cleared. Used both by <see cref="Enforce" /> and as the
    ///     self-contained fallback when a later pass drops NVEX entries out from under the flags.
    /// </summary>
    public static int ClearInvalidExternalFlags(byte[] nvtrBytes, int nvexCount)
    {
        if (nvtrBytes.Length < NvtrEntrySize || nvtrBytes.Length % NvtrEntrySize != 0)
        {
            return 0;
        }

        var cleared = 0;
        var triangleCount = nvtrBytes.Length / NvtrEntrySize;
        for (var t = 0; t < triangleCount; t++)
        {
            var baseOff = t * NvtrEntrySize;
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(nvtrBytes.AsSpan(baseOff + FlagsOffset, 2));
            var newFlags = flags;
            for (var e = 0; e < 3; e++)
            {
                if ((flags & (1 << e)) == 0)
                {
                    continue; // internal edge — value is a neighbor triangle index, leave it.
                }

                var value = BinaryPrimitives.ReadInt16LittleEndian(nvtrBytes.AsSpan(baseOff + EdgeSlotOffset[e], 2));
                if (value >= 0 && value < nvexCount)
                {
                    continue; // valid external link — a live NVEX index.
                }

                newFlags = (ushort)(newFlags & ~(1 << e));
                BinaryPrimitives.WriteInt16LittleEndian(nvtrBytes.AsSpan(baseOff + EdgeSlotOffset[e], 2), (short)-1);
                cleared++;
            }

            if (newFlags != flags)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(nvtrBytes.AsSpan(baseOff + FlagsOffset, 2), newFlags);
            }
        }

        return cleared;
    }
}
