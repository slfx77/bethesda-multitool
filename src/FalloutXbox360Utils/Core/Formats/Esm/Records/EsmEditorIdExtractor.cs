using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Extracts runtime Editor IDs from Xbox 360 memory dumps by locating and walking
///     the game's NiTMapBase hash table (BSTCaseInsensitiveStringMap).
/// </summary>
internal static class EsmEditorIdExtractor
{
    #region Nested Types

    /// <summary>
    ///     Represents a candidate hash table found during dynamic scanning.
    /// </summary>
    private readonly record struct HashTableCandidate(
        long FileOffset,
        long VirtualAddress,
        uint HashSize,
        long BucketArrayVa,
        long BucketArrayFileOffset,
        int ValidationScore,
        uint AllFormsVa = 0);

    #endregion

    #region Runtime EditorID Extraction

    /// <summary>
    ///     Extract runtime Editor IDs by dynamically locating the hash table structure in memory.
    ///     Scans for NiTMapBase signatures and validates candidates before extraction.
    /// </summary>
    internal static void ExtractRuntimeEditorIds(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo? minidumpInfo,
        EsmRecordScanResult scanResult,
        bool verbose = false)
    {
        var sw = Stopwatch.StartNew();
        var log = Logger.Instance;

        log.Debug("EditorIDs: Starting dynamic hash table detection...");

        if (minidumpInfo == null || minidumpInfo.MemoryRegions.Count == 0)
        {
            log.Debug("EditorIDs: No minidump info - skipping");
            return;
        }

        // Use dynamic hash table detection (scans memory for signature)
        var hashTableCount = TryFindAndExtractFromHashTable(accessor, fileSize, minidumpInfo, scanResult, log);
        sw.Stop();

        if (hashTableCount > 0)
        {
            log.Debug("EditorIDs: Dynamic detection extracted {0:N0} EditorIDs in {1:N0} ms",
                hashTableCount, sw.ElapsedMilliseconds);
        }
        else
        {
            log.Debug("EditorIDs: No valid hash table found in captured memory");
        }
    }

    /// <summary>
    ///     Locate and extract EditorIDs from the game's hash table using a three-stage approach:
    ///     Stage 1: PE-guided PDB offset lookup (cheapest, most reliable)
    ///     Stage 2: Data section triple-pointer scan (targeted fallback)
    ///     Stage 3: Full memory brute-force scan (last resort)
    /// </summary>
    private static int TryFindAndExtractFromHashTable(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo,
        EsmRecordScanResult scanResult,
        Logger log)
    {
        log.Debug("EditorIDs: Starting PE-guided hash table detection...");

        // Find game module and parse PE sections
        var gameModule = MinidumpAnalyzer.FindGameModule(minidumpInfo);
        if (gameModule == null)
        {
            log.Debug("EditorIDs: No game module found");
            return 0;
        }

        log.Debug("EditorIDs: Game module: {0} at VA 0x{1:X8}, size={2:N0}",
            Path.GetFileName(gameModule.Name), gameModule.BaseAddress, gameModule.Size);

        var sections = EsmEditorIdPeParser.EnumeratePeSections(accessor, fileSize, minidumpInfo, gameModule);
        if (sections == null || sections.Count == 0)
        {
            log.Debug("EditorIDs: Failed to parse PE sections");
            return 0;
        }

        log.Debug("EditorIDs: Found {0} PE sections:", sections.Count);
        foreach (var s in sections)
        {
            log.Debug("EditorIDs:   [{0}] '{1}' RVA=0x{2:X8} Size=0x{3:X8} Chars=0x{4:X8}",
                s.Index + 1, s.Name, s.VirtualAddress, s.VirtualSize, s.Characteristics);
        }

        // Scan .data section for global pointer triple (pAllForms, pAlteredForms, pAllFormsByEditorID)
        log.Debug("EditorIDs: Scanning data sections for global pointer triple...");
        var candidate = ScanDataSectionForGlobalTriple(
            accessor, fileSize, minidumpInfo, gameModule, sections, log);

        if (!candidate.HasValue)
        {
            log.Debug("EditorIDs: No hash table candidate found via data section scan");
            return 0;
        }

        log.Debug("EditorIDs: Extracting from candidate at VA 0x{0:X8}, hashSize={1}, score={2}",
            candidate.Value.VirtualAddress, candidate.Value.HashSize, candidate.Value.ValidationScore);

        var count = ExtractFromHashTableCandidate(
            accessor, fileSize, minidumpInfo, scanResult, candidate.Value, log);

        log.Debug("EditorIDs: Extracted {0:N0} EditorIDs", count);
        return count;
    }

    #endregion

    #region Hash Table Scanning

    /// <summary>
    ///     Scan the game module's writable data sections for the global pointer triple:
    ///     pAllForms + pAlteredForms + pAllFormsByEditorID (12 consecutive bytes, all valid BE pointers).
    /// </summary>
    private static HashTableCandidate? ScanDataSectionForGlobalTriple(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo,
        MinidumpModule gameModule,
        List<PeSectionInfo> sections,
        Logger log)
    {
        // Find writable data sections; also include section 7 (0-based index 6)
        // IMAGE_SCN_MEM_WRITE = 0x80000000, IMAGE_SCN_CNT_INITIALIZED_DATA = 0x00000040
        var dataSections = sections
            .Where(s => (s.Characteristics & 0x80000040) == 0x80000040 || s.Index == 6)
            .OrderByDescending(s => s.Index == 6 ? 1 : 0)
            .ToList();

        log.Debug("EditorIDs: Scanning {0} data sections for global pointer triple", dataSections.Count);

        foreach (var section in dataSections)
        {
            var sectionVaStart = gameModule.BaseAddress + section.VirtualAddress;

            log.Debug("EditorIDs: Scanning section '{0}' at VA 0x{1:X8}, size={2:N0} bytes",
                section.Name, sectionVaStart, section.VirtualSize);

            var sectionFileOffset = minidumpInfo.VirtualAddressToFileOffset(sectionVaStart);
            if (!sectionFileOffset.HasValue)
            {
                log.Debug("EditorIDs:   Section not in captured memory");
                continue;
            }

            var sectionSize = (int)Math.Min(section.VirtualSize, fileSize - sectionFileOffset.Value);
            if (sectionSize < 12)
            {
                continue;
            }

            var buffer = new byte[sectionSize];
            accessor.ReadArray(sectionFileOffset.Value, buffer, 0, sectionSize);

            // Scan for triple-pointer pattern at 4-byte alignment
            for (var i = 0; i <= sectionSize - 12; i += 4)
            {
                var ptr1 = BinaryUtils.ReadUInt32BE(buffer, i);
                var ptr2 = BinaryUtils.ReadUInt32BE(buffer, i + 4);
                var ptr3 = BinaryUtils.ReadUInt32BE(buffer, i + 8);

                if (ptr1 == 0 || ptr2 == 0 || ptr3 == 0)
                {
                    continue;
                }

                if (!Xbox360MemoryUtils.IsValidPointerInDump(ptr1, minidumpInfo) ||
                    !Xbox360MemoryUtils.IsValidPointerInDump(ptr2, minidumpInfo) ||
                    !Xbox360MemoryUtils.IsValidPointerInDump(ptr3, minidumpInfo))
                {
                    continue;
                }

                // Follow ptr3 (should be pAllFormsByEditorID)
                var candidate = ValidateHashTableAtAddress(accessor, fileSize, minidumpInfo, ptr3, log);
                if (candidate.HasValue && candidate.Value.ValidationScore >= 3)
                {
                    log.Debug("EditorIDs: Found triple at section '{0}' offset 0x{1:X4}, score={2}",
                        section.Name, i, candidate.Value.ValidationScore);
                    // Also capture ptr1 (pAllForms) for FormID-based lookups (e.g., LAND records)
                    return candidate.Value with { AllFormsVa = ptr1 };
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     Validate a hash table at a specific virtual address (the target of the global pointer).
    ///     Reads the NiTMapBase layout and validates the structure.
    /// </summary>
    private static HashTableCandidate? ValidateHashTableAtAddress(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo,
        uint hashTableVa,
        Logger log)
    {
        var htFileOffset = minidumpInfo.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(hashTableVa));
        if (!htFileOffset.HasValue || htFileOffset.Value + 16 > fileSize)
        {
            log.Debug("EditorIDs:   Hash table VA 0x{0:X8} not in captured memory", hashTableVa);
            return null;
        }

        var htBuffer = new byte[16];
        accessor.ReadArray(htFileOffset.Value, htBuffer, 0, 16);

        var vfptr = BinaryUtils.ReadUInt32BE(htBuffer);
        var hashSize = BinaryUtils.ReadUInt32BE(htBuffer, 4);
        var bucketArrayVa = BinaryUtils.ReadUInt32BE(htBuffer, 8);
        var entryCount = BinaryUtils.ReadUInt32BE(htBuffer, 12);

        log.Debug("EditorIDs:   HashTable at 0x{0:X8}: vfptr=0x{1:X8}, hashSize={2}, buckets=0x{3:X8}, count={4}",
            hashTableVa, vfptr, hashSize, bucketArrayVa, entryCount);

        // BSTCaseInsensitiveStringMap may use non-power-of-2 hash sizes (e.g., 131213 observed in Beta build)
        if (hashSize < 64 || hashSize > 262144)
        {
            log.Debug("EditorIDs:   Invalid hash size {0}", hashSize);
            return null;
        }

        if (!Xbox360MemoryUtils.IsValidPointerInDump(vfptr, minidumpInfo))
        {
            log.Debug("EditorIDs:   Invalid vfptr 0x{0:X8}", vfptr);
            return null;
        }

        var bucketFileOffset = minidumpInfo.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(bucketArrayVa));
        if (!bucketFileOffset.HasValue)
        {
            log.Debug("EditorIDs:   Bucket array 0x{0:X8} not in captured memory", bucketArrayVa);
            return null;
        }

        var score = SampleBucketsForValidation(accessor, fileSize, minidumpInfo,
            bucketFileOffset.Value, hashSize);

        log.Debug("EditorIDs:   Validation score = {0}", score);

        if (score < 1)
        {
            return null;
        }

        return new HashTableCandidate(
            htFileOffset.Value, hashTableVa, hashSize,
            bucketArrayVa, bucketFileOffset.Value, score);
    }

    /// <summary>
    ///     Extract EditorIDs from a validated hash table candidate.
    /// </summary>
    private static int ExtractFromHashTableCandidate(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo,
        EsmRecordScanResult scanResult,
        HashTableCandidate candidate,
        Logger log)
    {
        var startIndex = scanResult.RuntimeEditorIds.Count;
        var extracted = 0;
        var chainErrors = 0;
        var bucketBuffer = new byte[4];

        // Pass 1: Walk every bucket, following linked-list chains to collect EditorID entries
        for (uint i = 0; i < candidate.HashSize; i++)
        {
            var bucketOffset = candidate.BucketArrayFileOffset + i * 4;
            if (bucketOffset + 4 > fileSize)
            {
                break;
            }

            accessor.ReadArray(bucketOffset, bucketBuffer, 0, 4);
            var itemVa = BinaryUtils.ReadUInt32BE(bucketBuffer);

            if (itemVa != 0)
            {
                extracted += WalkBucketChain(accessor, fileSize, minidumpInfo, scanResult,
                    itemVa, ref chainErrors);
            }
        }

        log.Debug("EditorIDs: Hash table walk complete - {0:N0} extracted, {1} chain errors", extracted, chainErrors);

        // Pass 2: Detect INFO FormType from EditorID patterns, then extract dialogue lines
        EditorIdLookupTables.ExtractDialogueLinesForInfoEntries(
            accessor, fileSize, minidumpInfo, scanResult, startIndex, log);

        // Pass 3: Walk pAllForms hash table for LAND entries (LAND records lack editor IDs)
        if (candidate.AllFormsVa != 0)
        {
            EditorIdLookupTables.ExtractLandFormsFromAllFormsTable(
                accessor, fileSize, minidumpInfo, scanResult, candidate.AllFormsVa, log);
        }

        return extracted;
    }

    /// <summary>
    ///     Walk a single bucket's linked-list chain, extracting EditorID entries.
    /// </summary>
    private static int WalkBucketChain(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo,
        EsmRecordScanResult scanResult,
        uint itemVa,
        ref int chainErrors)
    {
        var extracted = 0;
        var chainDepth = 0;
        var itemBuffer = new byte[12]; // NiTMapItem: m_pkNext(4) + m_key(4) + m_val(4)
        var stringBuffer = new byte[256];
        var tesFormBuffer = new byte[24];

        while (itemVa != 0 && chainDepth < 1000)
        {
            chainDepth++;

            var itemVaLong = Xbox360MemoryUtils.VaToLong(itemVa);

            // Validate 12-byte NiTMapItem is fully within a captured memory region
            if (!minidumpInfo.IsVaRangeCaptured(itemVaLong, 12))
            {
                chainErrors++;
                break;
            }

            var itemFileOffset = minidumpInfo.VirtualAddressToFileOffset(itemVaLong);
            if (!itemFileOffset.HasValue || itemFileOffset.Value + 12 > fileSize)
            {
                chainErrors++;
                break;
            }

            accessor.ReadArray(itemFileOffset.Value, itemBuffer, 0, 12);
            var nextVa = BinaryUtils.ReadUInt32BE(itemBuffer); // m_pkNext
            var keyVa = BinaryUtils.ReadUInt32BE(itemBuffer, 4); // m_key (const char*)
            var valVa = BinaryUtils.ReadUInt32BE(itemBuffer, 8); // m_val (TESForm*)

            // Read EditorID string from m_key
            string? editorId = null;
            long stringFileOffset = 0;
            if (keyVa != 0 && Xbox360MemoryUtils.IsValidPointerInDump(keyVa, minidumpInfo))
            {
                var keyFileOffset = minidumpInfo.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(keyVa));
                if (keyFileOffset.HasValue && keyFileOffset.Value + 4 <= fileSize)
                {
                    stringFileOffset = keyFileOffset.Value;
                    var toRead = (int)Math.Min(stringBuffer.Length, fileSize - keyFileOffset.Value);
                    accessor.ReadArray(keyFileOffset.Value, stringBuffer, 0, toRead);

                    var len = 0;
                    while (len < toRead && stringBuffer[len] != 0)
                    {
                        len++;
                    }

                    if (len > 0 && len < toRead)
                    {
                        editorId = Encoding.ASCII.GetString(stringBuffer, 0, len);
                    }
                }
            }

            // Read FormID from TESForm at m_val
            uint formId = 0;
            byte formType = 0;
            long? tesFormFileOffset = null;
            if (valVa != 0 && Xbox360MemoryUtils.IsValidPointerInDump(valVa, minidumpInfo))
            {
                var valVaLong = Xbox360MemoryUtils.VaToLong(valVa);

                // Validate 24-byte TESForm header is fully within a captured memory region
                if (minidumpInfo.IsVaRangeCaptured(valVaLong, 24))
                {
                    var formFileOffset = minidumpInfo.VirtualAddressToFileOffset(valVaLong);
                    if (formFileOffset.HasValue && formFileOffset.Value + 24 <= fileSize)
                    {
                        tesFormFileOffset = formFileOffset.Value;
                        accessor.ReadArray(formFileOffset.Value, tesFormBuffer, 0, 24);
                        formType = tesFormBuffer[4]; // Offset 0x04: cFormType
                        formId = BinaryUtils.ReadUInt32BE(tesFormBuffer, 12); // Offset 0x0C: iFormID
                    }
                }
            }

            // Read display name from TESForm fields (dialogue deferred to pass 2)
            string? displayName = null;
            if (tesFormFileOffset.HasValue
                && EsmEditorIdConstants.FullNameOffsetByFormType.TryGetValue(formType, out var fullNameOffset))
            {
                displayName = EsmEditorIdStringReader.ReadBSStringT(accessor, fileSize, minidumpInfo,
                    tesFormFileOffset.Value, fullNameOffset);
            }

            if (editorId != null && editorId.Length >= 4 && EsmEditorIdValidator.IsValidEditorId(editorId))
            {
                scanResult.RuntimeEditorIds.Add(new RuntimeEditorIdEntry
                {
                    EditorId = editorId,
                    FormId = formId,
                    FormType = formType,
                    StringOffset = stringFileOffset,
                    TesFormOffset = tesFormFileOffset,
                    TesFormPointer = Xbox360MemoryUtils.VaToLong(valVa),
                    DisplayName = displayName
                });
                extracted++;
            }

            itemVa = nextVa;
        }

        return extracted;
    }

    /// <summary>
    ///     Sample a subset of hash table buckets, scoring how many contain valid EditorID strings.
    /// </summary>
    private static int SampleBucketsForValidation(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo,
        long bucketFileOffset,
        uint hashSize)
    {
        var score = 0;
        var bucketBuf = new byte[4];
        var itemBuf = new byte[12];
        var strBuf = new byte[64];
        var step = Math.Max(1, (int)(hashSize / 50));

        for (uint si = 0; si < hashSize && score < 20; si += (uint)step)
        {
            var bOff = bucketFileOffset + si * 4;
            if (bOff + 4 > fileSize)
            {
                break;
            }

            accessor.ReadArray(bOff, bucketBuf, 0, 4);
            var itemVa = BinaryUtils.ReadUInt32BE(bucketBuf);
            if (itemVa == 0 || !Xbox360MemoryUtils.IsValidPointerInDump(itemVa, minidumpInfo))
            {
                continue;
            }

            var itemFo = minidumpInfo.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(itemVa));
            if (!itemFo.HasValue || itemFo.Value + 12 > fileSize)
            {
                continue;
            }

            accessor.ReadArray(itemFo.Value, itemBuf, 0, 12);
            var keyVa = BinaryUtils.ReadUInt32BE(itemBuf, 4);
            if (!Xbox360MemoryUtils.IsValidPointerInDump(keyVa, minidumpInfo))
            {
                continue;
            }

            var keyFo = minidumpInfo.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(keyVa));
            if (!keyFo.HasValue || keyFo.Value + 4 > fileSize)
            {
                continue;
            }

            accessor.ReadArray(keyFo.Value, strBuf, 0, Math.Min(64, (int)(fileSize - keyFo.Value)));
            var len = 0;
            while (len < strBuf.Length && strBuf[len] != 0)
            {
                len++;
            }

            if (len >= 4 && len < 64)
            {
                var str = Encoding.ASCII.GetString(strBuf, 0, len);
                if (EsmEditorIdValidator.IsValidEditorId(str))
                {
                    score++;
                }
            }
        }

        return score;
    }

    #endregion
}
