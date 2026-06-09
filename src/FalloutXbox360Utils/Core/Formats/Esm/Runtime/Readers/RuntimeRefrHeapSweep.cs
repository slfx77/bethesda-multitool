using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers;

/// <summary>
///     Direct vtable heap sweep for TESObjectREFR/ACHR/ACRE structs that the engine's
///     <c>pAllForms</c> hash-table walk misses. On a partial memory dump the form hash table
///     has broken bucket chains (chain errors), so the editor-ID-table-driven scan that seeds
///     <see cref="EsmRecordScanResult.RuntimeRefrFormEntries" /> never sees a large pool of
///     resident refs. This complements it by scanning raw heap bytes for the REFR struct shape
///     (validated by vtable / FormID / position / scale / parent-cell pointer), and appends an
///     entry for each <b>master ref</b> the table missed so the existing
///     <see cref="RuntimeRefrReader" /> reads it and the authority/parent attribution then places
///     it in its owning cell. Scoped to master FormIDs (recovery is "sparse but pure" — only
///     refs the proto actually kept in memory, attributed via the bundled master, never invented).
/// </summary>
internal static class RuntimeRefrHeapSweep
{
    // TESForm header fields (NOT shifted across builds): vtable@0, FormID@12.
    private const int VftableOffset = 0;
    private const int FormIdOffset = 12;

    // OBJ_REFR data fields (final layout; early-era dumps use shift = -4).
    private const int LocationXOffset = 64;
    private const int LocationYOffset = 68;
    private const int LocationZOffset = 72;
    private const int RefScaleOffset = 76;
    private const int ParentCellOffset = 80;
    private const int RefrStructTail = 92;

    private const uint ModuleVaLo = 0x82000000u;
    private const uint ModuleVaHi = 0x84000000u;
    private const uint HeapVaLo = 0x40000000u;
    private const float WorldExtent = 500_000f;
    private const float MaxZ = 200_000f;

    /// <summary>
    ///     Append entries for master REFR/ACHR/ACRE structs resident in the dump but absent from
    ///     the form-table scan. Returns the number of entries added.
    /// </summary>
    public static int AppendMissingMasterRefrEntries(
        RecordParserContext context,
        IReadOnlySet<uint> masterFormIds)
    {
        if (context.MinidumpInfo is null || context.Accessor is null || masterFormIds.Count == 0)
        {
            return 0;
        }

        var scanResult = context.ScanResult;
        var existing = new HashSet<uint>();
        // REFR base form code = the lowest table FormType (REFR < ACHR < ACRE). The scan is
        // scoped to master REFR FormIDs (caller filter), so every swept hit is a REFR — no
        // formtype byte read (it drifts across builds and is unreliable for the struct itself).
        byte refrFormType = byte.MaxValue;
        foreach (var entry in scanResult.RuntimeRefrFormEntries)
        {
            existing.Add(entry.FormId);
            if (entry.FormType < refrFormType) refrFormType = entry.FormType;
        }

        if (refrFormType == byte.MaxValue)
        {
            refrFormType = 0x3A; // canonical FNV REFR code when there's nothing to calibrate from
        }

        var info = context.MinidumpInfo;
        var accessor = context.Accessor;

        var shift = ProbeShift(accessor, info, masterFormIds, existing);

        var addedFids = new HashSet<uint>();
        var added = 0;
        foreach (var region in info.MemoryRegions)
        {
            var buffer = ReadRegion(accessor, region);
            if (buffer == null)
            {
                continue;
            }

            ScanRegion(buffer, region, shift, masterFormIds, existing, addedFids,
                emit: hit =>
                {
                    scanResult.RuntimeRefrFormEntries.Add(new RuntimeEditorIdEntry
                    {
                        EditorId = $"__SWEPT_REFR_{hit.FormId:X8}",
                        FormId = hit.FormId,
                        FormType = refrFormType,
                        TesFormOffset = region.FileOffset + hit.Start,
                        TesFormPointer = region.VirtualAddress + hit.Start
                    });
                    added++;
                });
        }

        return added;
    }

    /// <summary>
    ///     Pick the field-shift (0 final / -4 early) that yields the most master-FormID hits over
    ///     a bounded sample of region bytes. The form-header fields used for the master-FormID and
    ///     vtable gate are shift-independent, so the winner is decided by the shifted
    ///     position/scale/parent-cell validators.
    /// </summary>
    private static int ProbeShift(
        IMemoryAccessor accessor,
        MinidumpInfo info,
        IReadOnlySet<uint> masterFormIds,
        HashSet<uint> existing)
    {
        const long sampleBudget = 64 * 1024 * 1024;
        var best = 0;
        var bestHits = -1;
        foreach (var shift in new[] { 0, -4 })
        {
            var hits = 0;
            long scanned = 0;
            var seen = new HashSet<uint>();
            foreach (var region in info.MemoryRegions)
            {
                if (scanned >= sampleBudget)
                {
                    break;
                }

                var buffer = ReadRegion(accessor, region);
                if (buffer == null)
                {
                    continue;
                }

                scanned += buffer.Length;
                ScanRegion(buffer, region, shift, masterFormIds, existing, seen,
                    emit: _ => hits++);
            }

            if (hits > bestHits)
            {
                bestHits = hits;
                best = shift;
            }
        }

        return best;
    }

    private static byte[]? ReadRegion(IMemoryAccessor accessor, MinidumpMemoryRegion region)
    {
        if (region.Size < RefrStructTail || region.Size > int.MaxValue)
        {
            return null;
        }

        var buffer = new byte[region.Size];
        try
        {
            var read = accessor.ReadArray(region.FileOffset, buffer, 0, (int)region.Size);
            return read <= 0 ? null : buffer;
        }
        catch
        {
            return null;
        }
    }

    private static void ScanRegion(
        byte[] buffer,
        MinidumpMemoryRegion region,
        int shift,
        IReadOnlySet<uint> masterFormIds,
        HashSet<uint> existing,
        HashSet<uint> addedFids,
        Action<RefrHit> emit)
    {
        var xOff = LocationXOffset + shift;
        var yOff = LocationYOffset + shift;
        var zOff = LocationZOffset + shift;
        var scaleOff = RefScaleOffset + shift;
        var pCellOff = ParentCellOffset + shift;
        var tail = Math.Max(zOff + 4, pCellOff + 4);
        if (buffer.Length < tail)
        {
            return;
        }

        var maxStart = buffer.Length - tail;
        for (var start = 0; start <= maxStart; start += 4)
        {
            var vft = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(start + VftableOffset));
            if (vft < ModuleVaLo || vft >= ModuleVaHi)
            {
                continue;
            }

            var fid = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(start + FormIdOffset));
            if (!masterFormIds.Contains(fid) || existing.Contains(fid) || addedFids.Contains(fid))
            {
                continue;
            }

            var x = BinaryPrimitives.ReadSingleBigEndian(buffer.AsSpan(start + xOff));
            if (!IsBounded(x, WorldExtent))
            {
                continue;
            }

            var y = BinaryPrimitives.ReadSingleBigEndian(buffer.AsSpan(start + yOff));
            if (!IsBounded(y, WorldExtent))
            {
                continue;
            }

            var z = BinaryPrimitives.ReadSingleBigEndian(buffer.AsSpan(start + zOff));
            if (!IsBounded(z, MaxZ))
            {
                continue;
            }

            var scale = BinaryPrimitives.ReadSingleBigEndian(buffer.AsSpan(start + scaleOff));
            if (float.IsNaN(scale) || scale <= 0.01f || scale > 100f)
            {
                continue;
            }

            var pCell = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(start + pCellOff));
            if (pCell != 0 && (pCell < HeapVaLo || pCell >= ModuleVaHi))
            {
                continue;
            }

            addedFids.Add(fid);
            emit(new RefrHit(start, fid));
        }
    }

    private static bool IsBounded(float value, float extent)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value) && value >= -extent && value <= extent;
    }

    private readonly record struct RefrHit(int Start, uint FormId);
}
