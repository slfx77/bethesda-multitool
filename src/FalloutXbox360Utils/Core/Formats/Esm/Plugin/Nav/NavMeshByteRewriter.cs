using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Nav;

/// <summary>
///     Patches DMP-captured NAVM subrecord bytes so the record can be re-emitted under
///     a new FormID in a cell other than its original (or with cross-NAVM links remapped
///     to newly-allocated NAVMs in the same emission batch).
///     Specifically:
///     <list type="bullet">
///         <item><description><b>DATA</b> subrecord (bytes 0..3): replace with the target cell FormID.</description></item>
///         <item><description><b>NVEX</b> subrecord (per 10-byte entry, bytes 4..7): replace navmesh FormID via
///             the navmFormIdRewrites map when the original is a DMP FormID we've allocated a new ID for.
///             Entries whose target isn't in the rewrites dict are left intact (master FormIDs round-trip as-is).</description></item>
///     </list>
///     Other subrecords (EDID, NVVX, NVTR, NVDP, NVCA) pass through verbatim. The caller is
///     responsible for allocating the new record-level FormID and assembling the final
///     record via <see cref="FalloutXbox360Utils.Core.Formats.Esm.Plugin.Output.PluginRecordByteBuilder.BuildNewRecordBytes" />.
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

    /// <summary>
    ///     Post-emission pass: walks an already-built NAVM record byte array, drops NVEX entries
    ///     whose target NAVM FormID isn't in <paramref name="validNavmTargets" />, and rebuilds
    ///     the record. Returns the original array unchanged when nothing was dropped, or when the
    ///     bytes don't parse as a NAVM record.
    ///
    ///     Why this exists: <see cref="Rewrite" /> rewrites NVEX target FormIDs at allocation time,
    ///     but Phase A allocates FormIDs for every DMP NAVM with a valid CellFormId — including
    ///     NAVMs whose parent cell ends up skipped by the cell loop's dedup/grid-collision logic.
    ///     The result is NVEX entries pointing at "allocated but never emitted" phantom FormIDs.
    ///     A similar dangling case is NVEX entries the rewriter left alone because the original
    ///     value was already a master-range FormID — but master may not actually have that NAVM.
    ///     Either dangling shape crashes the engine in NavMeshInfoMap setup. Counts the entries
    ///     dropped via the out parameter so callers can log.
    ///
    ///     Also patches the DATA subrecord's EdgeLinkCount field (offset 12, uint32) to match
    ///     the actual NVEX entry count after filtering. The engine reads EdgeLinkCount from DATA
    ///     and iterates that many NVEX entries; without this patch, dropped entries leave the
    ///     count stale and the engine walks off the end of NVEX, crashing at FalloutNV+0x0069DFDC.
    /// </summary>
    public static byte[] SanitizeNvexInNavmRecord(
        byte[] navmRecordBytes,
        IReadOnlySet<uint> validNavmTargets,
        out int droppedEntries)
    {
        droppedEntries = 0;
        const int RecordHeaderSize = 24;
        if (navmRecordBytes.Length < RecordHeaderSize) return navmRecordBytes;
        if (navmRecordBytes[0] != (byte)'N' || navmRecordBytes[1] != (byte)'A'
            || navmRecordBytes[2] != (byte)'V' || navmRecordBytes[3] != (byte)'M')
        {
            return navmRecordBytes;
        }
        var bodySize = BinaryPrimitives.ReadUInt32LittleEndian(navmRecordBytes.AsSpan(4, 4));
        if ((long)RecordHeaderSize + bodySize > navmRecordBytes.Length) return navmRecordBytes;

        const int NvexEntrySize = 10;
        const int NvexFormIdOffset = 4;
        var newBody = new System.IO.MemoryStream();
        var changed = false;
        var keptNvexEntries = 0;
        var existingDataEdgeCnt = -1;
        var body = navmRecordBytes.AsSpan(RecordHeaderSize, (int)bodySize);
        Span<byte> sizeBytes = stackalloc byte[2];
        var j = 0;
        while (j + 6 <= body.Length)
        {
            var sig = System.Text.Encoding.ASCII.GetString(body.Slice(j, 4));
            var subSize = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(j + 4, 2));
            if (j + 6 + subSize > body.Length) break;

            if (sig == "NVEX")
            {
                var kept = new System.IO.MemoryStream();
                for (var k = 0; k + NvexEntrySize <= subSize; k += NvexEntrySize)
                {
                    var entryStart = j + 6 + k;
                    var targetFid = BinaryPrimitives.ReadUInt32LittleEndian(
                        body.Slice(entryStart + NvexFormIdOffset, 4));
                    if (targetFid == 0 || validNavmTargets.Contains(targetFid))
                    {
                        kept.Write(body.Slice(entryStart, NvexEntrySize));
                        keptNvexEntries++;
                    }
                    else
                    {
                        droppedEntries++;
                        changed = true;
                    }
                }
                if (kept.Length > 0)
                {
                    newBody.Write("NVEX"u8);
                    BinaryPrimitives.WriteUInt16LittleEndian(sizeBytes, (ushort)kept.Length);
                    newBody.Write(sizeBytes);
                    newBody.Write(kept.GetBuffer().AsSpan(0, (int)kept.Length));
                }
                else
                {
                    changed = true;
                }
            }
            else
            {
                if (sig == "DATA" && subSize >= 16)
                {
                    existingDataEdgeCnt = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(j + 6 + 12, 4));
                }
                newBody.Write(body.Slice(j, 6 + subSize));
            }
            j += 6 + subSize;
        }

        // Even when NVEX entries weren't dropped, DATA.EdgeLinkCount may be stale relative to
        // the actual NVEX entries present (e.g. xex2's NAVM 0x01003F41 has DATA.edgeCnt=82 but
        // no NVEX subrecord at all — captured at runtime with a count field set but the
        // contents not serialized). Force a rebuild to patch DATA.
        if (existingDataEdgeCnt >= 0 && existingDataEdgeCnt != keptNvexEntries)
        {
            changed = true;
        }

        if (!changed) return navmRecordBytes;

        var newBodyBytes = newBody.GetBuffer().AsSpan(0, (int)newBody.Length);

        // Patch DATA.EdgeLinkCount (uint32 at offset 12 of DATA payload) to match the kept
        // NVEX entry count. The engine reads this count from DATA and iterates exactly that
        // many NVEX entries; if DATA's stale count is higher than the actual NVEX entries
        // present after sanitization, the engine walks off the end and crashes during
        // NavMeshInfoMap setup at FalloutNV+0x0069DFDC.
        PatchDataEdgeLinkCount(newBodyBytes, keptNvexEntries);

        var newRecord = new byte[RecordHeaderSize + newBodyBytes.Length];
        navmRecordBytes.AsSpan(0, RecordHeaderSize).CopyTo(newRecord);
        BinaryPrimitives.WriteUInt32LittleEndian(newRecord.AsSpan(4, 4), (uint)newBodyBytes.Length);
        newBodyBytes.CopyTo(newRecord.AsSpan(RecordHeaderSize));
        return newRecord;
    }

    /// <summary>
    ///     Walks a NAVM body to find its DATA subrecord and overwrites the EdgeLinkCount field
    ///     (uint32 at payload offset 12) with <paramref name="edgeCount" />. No-op if DATA isn't
    ///     found or is too short.
    /// </summary>
    private static void PatchDataEdgeLinkCount(Span<byte> body, int edgeCount)
    {
        var j = 0;
        while (j + 6 <= body.Length)
        {
            var sig = System.Text.Encoding.ASCII.GetString(body.Slice(j, 4));
            var subSize = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(j + 4, 2));
            if (j + 6 + subSize > body.Length) break;
            if (sig == "DATA" && subSize >= 16)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(j + 6 + 12, 4), (uint)edgeCount);
                return;
            }
            j += 6 + subSize;
        }
    }
}
