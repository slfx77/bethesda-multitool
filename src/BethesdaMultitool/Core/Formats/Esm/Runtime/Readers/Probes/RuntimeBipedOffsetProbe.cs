using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Probes;

/// <summary>
///     Probes the per-build offset of <c>Character.pBiped</c> (BipedAnim*). The MemDebug
///     PDB puts it at +452, but proto builds shift the Character layout — and possibly
///     BipedAnim's own internals — so the probe makes no slot-stride assumption: a
///     candidate pointer field wins when its pointee contains multiple 4-byte-aligned
///     pointers to ARMO/WEAP/HAIR forms (the only types that legitimately appear in
///     biped slots) within the first <see cref="PointeeScanBytes" /> bytes.
///     Cross-sample distinctness of the resolved addresses is required so a shared
///     global pointer can't win.
/// </summary>
internal static class RuntimeBipedOffsetProbe
{
    private const int PdbBipedPtrOffset = 452;
    private const int CharacterStructSize = 472;

    // Scan most of the Character struct (offset 100..700) rather than a narrow
    // window around +452 — proto layouts can differ substantially from the PDB.
    private const int MinShift = -352;
    private const int MaxShift = 248;
    private const int ShiftStep = 4;
    private const int MaxSamples = 24;
    private const int MaxScorePerSample = 5;

    // BipedAnim is 692 bytes in the final PDB; scan a little past that.
    internal const int PointeeScanBytes = 768;

    // A populated biped references at least an outfit and hair or a weapon. Junk
    // pointers that happen to land on TESForms virtually never resolve to two or
    // more forms of exactly these types.
    private const int MinSlotFormsPerSample = 2;

    /// <summary>
    ///     Returns the winning shift relative to the PDB offset (+452), or null when no
    ///     candidate scored on at least two actors with distinct pointee addresses.
    /// </summary>
    public static int? Probe(
        RuntimeMemoryContext context,
        IReadOnlyList<RuntimeEditorIdEntry> entries,
        Action<string>? log = null)
    {
        var samples = SelectLoadedActorSamples(context, entries);
        log?.Invoke(
            $"  [BipedPtr Probe] {entries.Count(e => e.FormType == 0x3B)} actor entries, " +
            $"{samples.Count} samples selected");
        if (samples.Count == 0)
        {
            return null;
        }

        var bufferSize = CharacterStructSize + MaxShift;
        var bestShift = 0;
        var bestScore = 0;
        var runnerUpScore = 0;

        for (var shift = MinShift; shift <= MaxShift; shift += ShiftStep)
        {
            var score = 0;
            var distinctPointees = new HashSet<uint>();

            foreach (var sample in samples)
            {
                var buffer = context.ReadTesFormBytes(sample, bufferSize);
                if (buffer == null ||
                    BinaryUtils.ReadUInt32BE(buffer, 12) != sample.FormId)
                {
                    continue;
                }

                var bipedPtr = BinaryUtils.ReadUInt32BE(buffer, PdbBipedPtrOffset + shift);
                var slotForms = CountEquippableFormPointers(context, bipedPtr);
                if (slotForms >= MinSlotFormsPerSample)
                {
                    score += Math.Min(slotForms, MaxScorePerSample);
                    distinctPointees.Add(bipedPtr);
                }
            }

            if (score > 0)
            {
                log?.Invoke(
                    $"  [BipedPtr Probe] +{PdbBipedPtrOffset + shift}: raw {score}, " +
                    $"distinct {distinctPointees.Count}");
            }

            // Distinctness gate: a shared global pointer resolves identically for every
            // actor and must not win just by appearing valid across all samples.
            if (distinctPointees.Count < 2)
            {
                score = 0;
            }

            if (score > bestScore)
            {
                runnerUpScore = bestScore;
                bestScore = score;
                bestShift = shift;
            }
            else if (score > runnerUpScore)
            {
                runnerUpScore = score;
            }
        }

        if (bestScore == 0)
        {
            // Observed on Debug/proto dumps: pBiped exists (e.g. +436 per the proto Debug
            // PDB) but points into a low-VA heap the minidump never captured, so no
            // candidate can validate. Falling back to the PDB offset is correct there.
            log?.Invoke("  [BipedPtr Probe] No candidate offset scored; runtime equipment unavailable");
            return null;
        }

        log?.Invoke(
            $"  [BipedPtr Probe] Best: pBiped at +{PdbBipedPtrOffset + bestShift} " +
            $"(shift {bestShift:+0;-0;+0}, score {bestScore}, margin {bestScore - runnerUpScore}, " +
            $"samples {samples.Count})");
        return bestShift;
    }

    /// <summary>
    ///     Unloaded actors have a null biped and give the probe nothing to score, and the
    ///     hash-table entry order says nothing about loadedness. Loaded actors carry many
    ///     live heap pointers (process, biped, parent cell, AI data) in their Character
    ///     struct, so rank candidates by heap-pointer density and probe the densest ones.
    /// </summary>
    private static List<RuntimeEditorIdEntry> SelectLoadedActorSamples(
        RuntimeMemoryContext context,
        IReadOnlyList<RuntimeEditorIdEntry> entries)
    {
        const int maxCandidates = 512;
        var bufferSize = CharacterStructSize + MaxShift;
        var ranked = new List<(RuntimeEditorIdEntry Entry, int HeapPointers)>();

        foreach (var entry in entries)
        {
            if (entry.FormType != 0x3B || entry.TesFormOffset == null)
            {
                continue;
            }

            if (ranked.Count >= maxCandidates)
            {
                break;
            }

            var buffer = context.ReadTesFormBytes(entry, bufferSize);
            if (buffer == null || BinaryUtils.ReadUInt32BE(buffer, 12) != entry.FormId)
            {
                continue;
            }

            var heapPointers = 0;
            for (var pos = 0; pos + 4 <= buffer.Length; pos += 4)
            {
                var value = BinaryUtils.ReadUInt32BE(buffer, pos);
                if (IsDataPointer(context, value))
                {
                    heapPointers++;
                }
            }

            ranked.Add((entry, heapPointers));
        }

        return ranked
            .OrderByDescending(r => r.HeapPointers)
            .Take(MaxSamples)
            .Select(r => r.Entry)
            .ToList();
    }

    /// <summary>
    ///     Heap allocations sit below module space on every observed build, but the heap
    ///     BASE varies (Release ~0x4xxxxxxx, Debug allocators much lower), so validity is
    ///     "aligned, below 0x80000000, and captured in the dump" rather than a VA range.
    /// </summary>
    internal static bool IsDataPointer(RuntimeMemoryContext context, uint va)
    {
        return va is not 0 and < 0x80000000 &&
               (va & 3) == 0 &&
               context.VaToFileOffset(va) != null;
    }

    /// <summary>
    ///     Counts distinct ARMO/WEAP/HAIR forms referenced by 4-byte-aligned pointers in
    ///     the first <see cref="PointeeScanBytes" /> bytes of the candidate BipedAnim.
    /// </summary>
    private static int CountEquippableFormPointers(RuntimeMemoryContext context, uint bipedPtr)
    {
        if (!IsDataPointer(context, bipedPtr))
        {
            return 0;
        }

        var pointee = context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(bipedPtr), PointeeScanBytes);
        if (pointee == null)
        {
            return 0;
        }

        var seen = new HashSet<uint>();
        for (var pos = 0; pos + 4 <= pointee.Length; pos += 4)
        {
            var itemPtr = BinaryUtils.ReadUInt32BE(pointee, pos);
            if (!IsDataPointer(context, itemPtr))
            {
                continue;
            }

            var header = context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(itemPtr), 16);
            if (header == null)
            {
                continue;
            }

            var formType = header[4];
            var formId = BinaryUtils.ReadUInt32BE(header, 12);
            if (formType is 0x0C or 0x18 or 0x28 && formId is not (0 or 0xFFFFFFFF))
            {
                seen.Add(formId);
            }
        }

        return seen.Count;
    }
}
