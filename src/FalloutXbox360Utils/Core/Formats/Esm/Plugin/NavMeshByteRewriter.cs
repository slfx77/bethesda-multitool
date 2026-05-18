using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin;

/// <summary>
///     Patches DMP-captured NAVM subrecord bytes so the record can be re-emitted under
///     a new FormID in a cell other than its original (or with cross-NAVM links remapped
///     to newly-allocated NAVMs in the same emission batch).
///     Specifically:
///     <list type="bullet">
///         <item><description><b>DATA</b> subrecord (bytes 0..3): replace with the target cell FormID.</description></item>
///         <item><description><b>NVEX</b> subrecord (per 10-byte entry, bytes 4..7): replace navmesh FormID via
///             <paramref name="navmFormIdRewrites" /> when the original is a DMP FormID we've allocated a new ID for.
///             Entries whose target isn't in the rewrites dict are left intact (master FormIDs round-trip as-is).</description></item>
///     </list>
///     Other subrecords (EDID, NVVX, NVTR, NVDP, NVCA) pass through verbatim. The caller is
///     responsible for allocating the new record-level FormID and assembling the final
///     record via <see cref="PluginRecordByteBuilder.BuildNewRecordBytes" />.
/// </summary>
internal static class NavMeshByteRewriter
{
    /// <summary>
    ///     Apply DATA-cell and NVEX-navmesh rewrites to a captured NAVM subrecord list.
    ///     Returns a new <see cref="EncodedSubrecord" /> list; the input is not mutated
    ///     (the underlying byte arrays are copied before patching).
    /// </summary>
    public static List<EncodedSubrecord> Rewrite(
        IReadOnlyList<NavMeshSubrecord> capturedSubrecords,
        uint newCellFormId,
        IReadOnlyDictionary<uint, uint> navmFormIdRewrites)
    {
        var result = new List<EncodedSubrecord>(capturedSubrecords.Count);
        foreach (var sub in capturedSubrecords)
        {
            var bytes = (byte[])sub.Bytes.Clone();
            switch (sub.Signature)
            {
                case "DATA":
                    // DATA layout (20 or 24 bytes): bytes 0..3 = Cell FormID. Patch in place.
                    if (bytes.Length >= 4)
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), newCellFormId);
                    }
                    break;
                case "NVEX":
                    // NVEX entries are 10 bytes each: uint32 Type + uint32 NavmeshFormID + uint16 Triangle.
                    // Walk the array and rewrite each NavmeshFormID via the dict where present.
                    PatchNvexEntries(bytes, navmFormIdRewrites);
                    break;
            }
            result.Add(new EncodedSubrecord(sub.Signature, bytes));
        }
        return result;
    }

    private static void PatchNvexEntries(byte[] bytes, IReadOnlyDictionary<uint, uint> rewrites)
    {
        const int EntrySize = 10;
        const int FormIdOffsetInEntry = 4;
        for (var offset = 0; offset + EntrySize <= bytes.Length; offset += EntrySize)
        {
            var span = bytes.AsSpan(offset + FormIdOffsetInEntry, 4);
            var existing = BinaryPrimitives.ReadUInt32LittleEndian(span);
            if (rewrites.TryGetValue(existing, out var replacement))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(span, replacement);
            }
        }
    }
}
