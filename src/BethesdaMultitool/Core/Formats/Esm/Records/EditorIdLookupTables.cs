using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Records;

/// <summary>
///     Provides dialogue line extraction and the pAllForms hash table walk that resolves
///     LAND/REFR/ACHR/ACRE entries (which lack editor IDs). Delegates validation, string
///     reading, and constants to <see cref="EsmEditorIdValidator" />,
///     <see cref="EsmEditorIdStringReader" />, and <see cref="EsmEditorIdConstants" />.
/// </summary>
internal static class EditorIdLookupTables
{
    #region Dialogue Extraction

    /// <summary>
    ///     Detect INFO FormType from EditorID patterns, then read dialogue prompt text.
    /// </summary>
    internal static void ExtractDialogueLinesForInfoEntries(
        RuntimeMemoryContext memoryContext,
        EsmRecordScanResult scanResult,
        int startIndex,
        Logger log)
    {
        var infoFormType = DetectInfoFormType(scanResult.RuntimeEditorIds, startIndex);
        if (!infoFormType.HasValue)
        {
            log.Debug("EditorIDs: Could not detect INFO FormType - no dialogue extraction");
            return;
        }

        log.Debug("EditorIDs: Detected INFO FormType = {0} (0x{0:X2})", infoFormType.Value);
        var dialogueCount = 0;
        var infoCount = 0;
        for (var i = startIndex; i < scanResult.RuntimeEditorIds.Count; i++)
        {
            var entry = scanResult.RuntimeEditorIds[i];
            if (entry.FormType == infoFormType.Value && entry.TesFormOffset.HasValue)
            {
                infoCount++;
                var dialogueLine = EsmEditorIdStringReader.ReadFromTesFormEntry(
                    memoryContext, entry, EsmEditorIdConstants.InfoPromptOffset);
                if (dialogueLine is { } line)
                {
                    entry.DialogueLine = line.Text;
                    entry.DialogueLineStringOffset = line.StringFileOffset;
                    dialogueCount++;
                }
            }
        }

        log.Debug("EditorIDs: Extracted {0:N0} dialogue lines from {1:N0} INFO entries",
            dialogueCount, infoCount);
    }

    /// <summary>
    ///     Detect the runtime FormType value for INFO records by matching EditorID naming
    ///     conventions. The FormType enum shifts between game builds, so we calibrate from
    ///     actual data rather than using hardcoded values.
    /// </summary>
    internal static byte? DetectInfoFormType(List<RuntimeEditorIdEntry> entries, int startIndex)
    {
        // INFO EditorIDs in Fallout: New Vegas reliably contain "Topic"
        // (e.g., aBHTopicAgree, VDialogueDocMitchellTopic001)
        var formTypeCounts = new Dictionary<byte, int>();
        for (var i = startIndex; i < entries.Count; i++)
        {
            if (entries[i].EditorId.Contains("Topic", StringComparison.OrdinalIgnoreCase))
            {
                formTypeCounts.TryGetValue(entries[i].FormType, out var count);
                formTypeCounts[entries[i].FormType] = count + 1;
            }
        }

        if (formTypeCounts.Count == 0)
        {
            return null;
        }

        // Return the FormType with the most Topic matches (require at least 5)
        var best = formTypeCounts.MaxBy(kv => kv.Value);
        return best.Value >= 5 ? best.Key : null;
    }

    #endregion

    #region AllForms Hash Table (LAND/REFR/ACHR/ACRE)

    /// <summary>
    ///     Extract LAND and REFR/ACHR/ACRE form entries from the pAllForms hash table
    ///     (NiTMapBase&lt;uint, TESForm*&gt;). These record types often lack editor IDs,
    ///     so they're absent from pAllFormsByEditorID. The pAllForms table maps ALL FormIDs
    ///     to TESForm pointers. Auto-detects FormTypes by cross-referencing with ESM-scanned FormIDs.
    /// </summary>
    internal static void ExtractLandFormsFromAllFormsTable(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo,
        EsmRecordScanResult scanResult,
        uint allFormsVa,
        Logger log)
    {
        log.Debug("EditorIDs: Walking pAllForms hash table at VA 0x{0:X8} for LAND/REFR entries...", allFormsVa);
        var memoryContext = new RuntimeMemoryContext(
            new MmfMemoryAccessor(accessor), fileSize, minidumpInfo);

        // Read NiTMapBase header: vfptr(4) + hashSize(4) + bucketArrayVa(4) + count(4) = 16 bytes
        var htFileOffset = minidumpInfo.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(allFormsVa));
        if (!htFileOffset.HasValue || htFileOffset.Value + 16 > fileSize)
        {
            log.Debug("EditorIDs: pAllForms VA 0x{0:X8} not in captured memory", allFormsVa);
            return;
        }

        var htBuffer = new byte[16];
        accessor.ReadArray(htFileOffset.Value, htBuffer, 0, 16);

        var hashSize = BinaryUtils.ReadUInt32BE(htBuffer, 4);
        var bucketArrayVa = BinaryUtils.ReadUInt32BE(htBuffer, 8);
        var entryCount = BinaryUtils.ReadUInt32BE(htBuffer, 12);

        log.Debug("EditorIDs: pAllForms: hashSize={0}, buckets=0x{1:X8}, count={2}",
            hashSize, bucketArrayVa, entryCount);

        if (hashSize < 64 || hashSize > 262144)
        {
            log.Debug("EditorIDs: pAllForms invalid hash size {0}", hashSize);
            return;
        }

        var bucketFileOffset = minidumpInfo.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(bucketArrayVa));
        if (!bucketFileOffset.HasValue)
        {
            log.Debug("EditorIDs: pAllForms bucket array not in captured memory");
            return;
        }

        // Build sets of known FormIDs from ESM record scanning for FormType auto-detection
        var knownLandFormIds = scanResult.LandRecords
            .Select(land => land.Header.FormId)
            .Where(id => id != 0)
            .ToHashSet();

        var knownRefrFormIds = scanResult.RefrRecords
            .Select(refr => refr.Header.FormId)
            .Where(id => id != 0)
            .ToHashSet();

        log.Debug("EditorIDs: pAllForms: {0} known LAND, {1} known REFR FormIDs from ESM scan for calibration",
            knownLandFormIds.Count, knownRefrFormIds.Count);

        // Pass 1: Walk entire table, collecting FormID->FormType mappings for known FormIDs
        // and building a full FormID->(FormType, FileOffset, VA) index for later filtering
        var allEntries = new List<(uint FormId, byte FormType, long FileOffset, long Va)>();
        var landFormTypeCounts = new Dictionary<byte, int>();
        var refrFormTypeCounts = new Dictionary<byte, int>();
        var chainErrors = 0;
        var bucketBuffer = new byte[4];

        for (uint i = 0; i < hashSize; i++)
        {
            var bOff = bucketFileOffset.Value + i * 4;
            if (bOff + 4 > fileSize)
            {
                break;
            }

            accessor.ReadArray(bOff, bucketBuffer, 0, 4);
            var itemVa = BinaryUtils.ReadUInt32BE(bucketBuffer);

            if (itemVa != 0 && Xbox360MemoryUtils.IsValidPointerInDump(itemVa, minidumpInfo))
            {
                WalkAllFormsBucketChainCollect(
                    memoryContext, itemVa, ref chainErrors,
                    knownLandFormIds, landFormTypeCounts,
                    knownRefrFormIds, refrFormTypeCounts,
                    allEntries);
            }
        }

        // Determine LAND FormType: the FormType most commonly associated with known LAND FormIDs.
        // Sentinel byte value (0xFF, an unused FormType slot) means "no high-confidence detection
        // — don't populate RuntimeLandFormEntries". This is critical: the previous default of 0x45
        // was provably wrong (0x45 is DIAL in our observed builds), causing DIAL records to be
        // mis-classified as LAND and downstream readers to receive garbage. Better to populate
        // nothing than wrong entries.
        // ⚠ Do NOT substitute the PDB's answer here. pdb_layouts.json is generated from the FINAL
        // build's PDB, but the corpus spans development builds whose record enumeration was still
        // moving — that is the entire reason this detection is empirical rather than a constant.
        // Hardcoding the final byte would repeat the old 0x45 default's mistake in a new form.
        byte landFormType = 0xFF;
        if (landFormTypeCounts.Count > 0)
        {
            var best = landFormTypeCounts.MaxBy(kv => kv.Value);
            if (best.Value >= 3)
            {
                landFormType = best.Key;
                log.Debug(
                    "EditorIDs: pAllForms: detected LAND FormType = 0x{0:X2} ({1} matches from {2} known LAND FormIDs)",
                    landFormType, best.Value, knownLandFormIds.Count);
            }
            else
            {
                log.Warn(
                    "EditorIDs: pAllForms: LAND FormType detection low-confidence (best.Value={0}, need >=3) — skipping LAND population",
                    best.Value);
            }
        }
        else
        {
            log.Warn("EditorIDs: pAllForms: no known LAND FormIDs matched — skipping LAND population");
        }

        // Determine REFR FormType cluster: REFR/ACHR/ACRE are consecutive (base, base+1, base+2)
        byte refrBaseFormType = 0x3A; // Default fallback
        if (refrFormTypeCounts.Count > 0)
        {
            var best = refrFormTypeCounts.MaxBy(kv => kv.Value);
            if (best.Value >= 3)
            {
                refrBaseFormType = best.Key;
                log.Debug(
                    "EditorIDs: pAllForms: detected REFR base FormType = 0x{0:X2} ({1} matches from {2} known REFR FormIDs)",
                    refrBaseFormType, best.Value, knownRefrFormIds.Count);
            }
        }
        else
        {
            log.Debug("EditorIDs: pAllForms: no known REFR FormIDs matched - using default 0x3A");
        }

        // Pass 2: Filter allEntries for detected FormTypes
        var landCount = 0;
        var refrCount = 0;
        // When the FormID-correlation heuristic could not name this build's LAND type, stage the
        // types it could still plausibly be so the enricher can settle it by evidence. The filter is
        // the build-independent invariant: a LAND record has no EditorID, so any FormType observed
        // carrying one in THIS dump is excluded. That leaves a handful of candidates (3 on
        // Fallout_Debug.xex2), not the whole table.
        //
        // Exclusion requires TWO named entries, not one: the editor-ID walk's string reads are
        // fallible on damaged chains, and a single false-positive string attributed to the true
        // LAND byte would silently remove it from candidacy (the dump then reports "no runtime
        // terrain" with nothing to see). A wrongly-admitted type costs only sweep time — the mesh
        // gate yields 0 for every non-LAND type — so the failure modes are asymmetric.
        var landTypesWithEditorIds = new HashSet<byte>();
        if (landFormType == 0xFF)
        {
            var namedCountsByType = new Dictionary<byte, int>();
            foreach (var entry in scanResult.RuntimeEditorIds)
            {
                namedCountsByType.TryGetValue(entry.FormType, out var count);
                namedCountsByType[entry.FormType] = count + 1;
            }

            foreach (var (formType, count) in namedCountsByType)
            {
                if (count >= 2)
                {
                    landTypesWithEditorIds.Add(formType);
                }
                else
                {
                    log.Debug(
                        "EditorIDs: pAllForms: FormType 0x{0:X2} has only {1} named entry — kept as a LAND " +
                        "candidate despite it (single string reads are fallible; the mesh gate arbitrates)",
                        formType, count);
                }
            }
        }

        foreach (var (formId, formType, fileOffset, va) in allEntries)
        {
            if (formId == 0)
            {
                continue;
            }

            if (formType == landFormType)
            {
                scanResult.RuntimeLandFormEntries.Add(new RuntimeEditorIdEntry
                {
                    EditorId = $"__LAND_{formId:X8}",
                    FormId = formId,
                    FormType = formType,
                    TesFormOffset = fileOffset,
                    TesFormPointer = va
                });
                landCount++;
            }
            else if (landFormType == 0xFF
                     && !landTypesWithEditorIds.Contains(formType)
                     && (formType < refrBaseFormType || formType > refrBaseFormType + 2))
            {
                // The REFR/ACHR/ACRE range is excluded explicitly, not just via the editor-ID
                // filter: refr detection (arm 3 below) is independent of LAND detection and already
                // trusted. Without this, a low-confidence-LAND dump whose capture happens to hold
                // zero NAMED refs of one placed type (small interior-only captures can plausibly
                // have no named ACRE) would divert that type's entire population into the LAND
                // candidate sweep and silently lose its runtime placed-ref enrichment.
                scanResult.RuntimeLandCandidateEntries.Add(new RuntimeEditorIdEntry
                {
                    EditorId = $"__LAND_{formId:X8}",
                    FormId = formId,
                    FormType = formType,
                    TesFormOffset = fileOffset,
                    TesFormPointer = va
                });
            }
            else if (formType >= refrBaseFormType && formType <= refrBaseFormType + 2)
            {
                // REFR (base), ACHR (base+1), ACRE (base+2)
                var typeCode = (formType - refrBaseFormType) switch
                {
                    0 => "REFR",
                    1 => "ACHR",
                    2 => "ACRE",
                    _ => "REFR"
                };
                scanResult.RuntimeRefrFormEntries.Add(new RuntimeEditorIdEntry
                {
                    EditorId = $"__{typeCode}_{formId:X8}",
                    FormId = formId,
                    FormType = formType,
                    TesFormOffset = fileOffset,
                    TesFormPointer = va
                });
                refrCount++;
            }
        }

        log.Debug(
            "EditorIDs: pAllForms walk complete - {0:N0} LAND (0x{1:X2}), {2:N0} REFR/ACHR/ACRE (0x{3:X2}-0x{4:X2}), {5} chain errors, {6:N0} total forms",
            landCount, landFormType, refrCount, refrBaseFormType, (byte)(refrBaseFormType + 2), chainErrors,
            allEntries.Count);
    }

    /// <summary>
    ///     Walk a bucket chain collecting all FormID->FormType entries, and calibrating
    ///     the LAND and REFR FormTypes by checking against known FormIDs from ESM scanning.
    ///     Uses VA-based region validation to prevent reading garbage across memory region gaps.
    /// </summary>
    private static void WalkAllFormsBucketChainCollect(
        RuntimeMemoryContext memoryContext,
        uint itemVa,
        ref int chainErrors,
        HashSet<uint> knownLandFormIds,
        Dictionary<byte, int> landFormTypeCounts,
        HashSet<uint> knownRefrFormIds,
        Dictionary<byte, int> refrFormTypeCounts,
        List<(uint FormId, byte FormType, long FileOffset, long Va)> allEntries)
    {
        var chainDepth = 0;
        // A corrupt page whose next-pointer loops back into the chain would otherwise re-append the
        // same entries until the depth cap — dictionary collapse keeps downstream results correct,
        // but allEntries (and every sweep over it) inflates ~1000x per cyclic node.
        var visited = new HashSet<uint>();

        while (itemVa != 0 && chainDepth < 1000)
        {
            if (!visited.Add(itemVa))
            {
                chainErrors++;
                break;
            }

            chainDepth++;

            var itemVaLong = Xbox360MemoryUtils.VaToLong(itemVa);

            // Read by VA so adjacent capture regions may be stored out of order in the dump,
            // while an actual virtual-address gap remains a hard boundary.
            var itemBytes = memoryContext.ReadBytesAtVa(itemVaLong, 12);
            if (itemBytes is null)
            {
                chainErrors++;
                break;
            }

            var nextVa = BinaryUtils.ReadUInt32BE(itemBytes);
            var keyFormId = BinaryUtils.ReadUInt32BE(itemBytes, 4);
            var valVa = BinaryUtils.ReadUInt32BE(itemBytes, 8);

            if (memoryContext.IsValidPointer(valVa))
            {
                var valVaLong = Xbox360MemoryUtils.VaToLong(valVa);
                var formFileOffset = memoryContext.VaToFileOffset(valVa);
                var tesFormBytes = memoryContext.ReadBytesAtVa(
                    valVaLong, TesFormHeaderProbe.RequiredBufferSize);
                if (formFileOffset.HasValue &&
                    tesFormBytes is not null &&
                    keyFormId != 0 &&
                    TesFormHeaderProbe.TryProbe(
                        tesFormBytes, out var formType, out _, keyFormId))
                {
                    // The map value is TESForm*, so identity is always TESForm-relative
                    // (+4/+12), even when this is an interior subobject of MSTT or FLOR.
                    allEntries.Add((keyFormId, formType, formFileOffset.Value, valVaLong));

                    // Calibrate: if this FormID is a known LAND record, record its FormType
                    if (knownLandFormIds.Contains(keyFormId))
                    {
                        landFormTypeCounts.TryGetValue(formType, out var count);
                        landFormTypeCounts[formType] = count + 1;
                    }

                    // Calibrate: if this FormID is a known REFR/ACHR/ACRE, record its FormType
                    if (knownRefrFormIds.Contains(keyFormId))
                    {
                        refrFormTypeCounts.TryGetValue(formType, out var rCount);
                        refrFormTypeCounts[formType] = rCount + 1;
                    }
                }
            }

            itemVa = nextVa;
        }
    }

    #endregion
}
