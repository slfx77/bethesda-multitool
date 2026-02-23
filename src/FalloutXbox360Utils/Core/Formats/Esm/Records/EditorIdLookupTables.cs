using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Provides lookup tables, validation, and auxiliary extraction routines used by
///     <see cref="EsmEditorIdExtractor" />. Includes FormType-to-offset mappings,
///     EditorID validation, dialogue line extraction, and the pAllForms hash table
///     walk that resolves LAND/REFR/ACHR/ACRE entries (which lack editor IDs).
/// </summary>
internal static class EditorIdLookupTables
{
    #region Constants

    /// <summary>
    ///     Maps runtime FormType byte values to the offset of the TESFullName pointer
    ///     within the C++ class hierarchy for each record type.
    /// </summary>
    internal static readonly Dictionary<byte, int> FullNameOffsetByFormType = new()
    {
        [0x08] = 44, // FACT - TESFaction
        [0x0A] = 44, // HAIR - TESHair (TESForm->TESFullName->TESModel->TESHair)
        [0x0B] = 44, // EYES - TESEyes (TESForm->TESFullName->TESModel->TESEyes)
        [0x0C] = 44, // RACE - TESRace
        [0x15] = 68, // ACTI - TESObjectACTI
        [0x18] = 68, // ARMO - TESObjectARMO
        [0x19] = 68, // BOOK - TESObjectBOOK
        [0x1B] = 80, // CONT - TESObjectCONT
        [0x1C] = 68, // DOOR - TESObjectDOOR
        [0x1F] = 68, // MISC - TESObjectMISC
        [0x28] = 68, // WEAP - TESObjectWEAP
        [0x29] = 68, // AMMO - TESAmmo
        [0x2A] = 228, // NPC_ - TESNPC
        [0x2E] = 68, // KEYM - TESKey
        [0x2F] = 68, // ALCH - AlchemyItem
        [0x33] = 68 // PROJ - BGSProjectile (TESBoundObject->TESFullName->TESModel->...)
    };

    internal const int InfoPromptOffset = 44;

    #endregion

    #region EditorID Validation

    /// <summary>
    ///     Validate an Editor ID string (alphanumeric + underscore, starts with letter or digit).
    /// </summary>
    internal static bool IsValidEditorId(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length < 2 || name.Length > 200)
        {
            return false;
        }

        if (!char.IsLetterOrDigit(name[0]))
        {
            return false;
        }

        // Require 100% valid characters (alphanumeric + underscore)
        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        // Reject repeated-pattern junk (e.g., "katSkatSkatS...")
        if (name.Length >= 8 && HasRepeatedPattern(name))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Detect repeated substring patterns (e.g., "katSkatSkatS" repeats "katS").
    /// </summary>
    private static bool HasRepeatedPattern(string s)
    {
        // Check for patterns of length 2-6 that repeat 3+ times
        for (var patLen = 2; patLen <= Math.Min(6, s.Length / 3); patLen++)
        {
            var pattern = s[..patLen];
            var repeatCount = 0;
            for (var i = 0; i + patLen <= s.Length; i += patLen)
            {
                if (s.AsSpan(i, patLen).SequenceEqual(pattern))
                {
                    repeatCount++;
                }
                else
                {
                    break;
                }
            }

            if (repeatCount >= 3)
            {
                return true;
            }
        }

        return false;
    }

    #endregion

    #region String Reading

    /// <summary>
    ///     Read a BSStringT&lt;char&gt; string from a TESForm object in the dump.
    ///     BSStringT layout (8 bytes, big-endian on Xbox 360):
    ///     Offset 0: pString (char* pointer, 4 bytes BE)
    ///     Offset 4: sLen (uint16 BE)
    /// </summary>
    internal static string? ReadBSStringT(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo,
        long tesFormFileOffset,
        int fieldOffset)
    {
        var bstOffset = tesFormFileOffset + fieldOffset;
        if (bstOffset + 8 > fileSize)
        {
            return null;
        }

        var bstBuffer = new byte[8];
        accessor.ReadArray(bstOffset, bstBuffer, 0, 8);

        var pString = BinaryUtils.ReadUInt32BE(bstBuffer);
        var sLen = BinaryUtils.ReadUInt16BE(bstBuffer, 4);

        if (pString == 0 || sLen == 0 || sLen > 4096)
        {
            return null;
        }

        if (!Xbox360MemoryUtils.IsValidPointerInDump(pString, minidumpInfo))
        {
            return null;
        }

        var strFileOffset = minidumpInfo.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(pString));
        if (!strFileOffset.HasValue || strFileOffset.Value + sLen > fileSize)
        {
            return null;
        }

        var strBuffer = new byte[sLen];
        accessor.ReadArray(strFileOffset.Value, strBuffer, 0, sLen);

        // Validate: should be mostly printable ASCII
        var printable = 0;
        for (var i = 0; i < sLen; i++)
        {
            var c = strBuffer[i];
            if ((c >= 32 && c <= 126) || c == '\n' || c == '\r' || c == '\t')
            {
                printable++;
            }
        }

        if (printable < sLen * 0.8)
        {
            return null;
        }

        return Encoding.ASCII.GetString(strBuffer, 0, sLen);
    }

    #endregion

    #region Dialogue Extraction

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

    /// <summary>
    ///     Detect INFO FormType from EditorID patterns, then read dialogue prompt text.
    /// </summary>
    internal static void ExtractDialogueLinesForInfoEntries(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo,
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
                var dialogueLine = ReadBSStringT(accessor, fileSize, minidumpInfo,
                    entry.TesFormOffset.Value, InfoPromptOffset);
                if (dialogueLine != null)
                {
                    entry.DialogueLine = dialogueLine;
                    dialogueCount++;
                }
            }
        }

        log.Debug("EditorIDs: Extracted {0:N0} dialogue lines from {1:N0} INFO entries",
            dialogueCount, infoCount);
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
                    accessor, fileSize, minidumpInfo, itemVa, ref chainErrors,
                    knownLandFormIds, landFormTypeCounts,
                    knownRefrFormIds, refrFormTypeCounts,
                    allEntries);
            }
        }

        // Determine LAND FormType: the FormType most commonly associated with known LAND FormIDs
        byte landFormType = 0x45; // Default fallback
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
        }
        else
        {
            log.Debug("EditorIDs: pAllForms: no known LAND FormIDs matched - using default 0x45");
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
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo,
        uint itemVa,
        ref int chainErrors,
        HashSet<uint> knownLandFormIds,
        Dictionary<byte, int> landFormTypeCounts,
        HashSet<uint> knownRefrFormIds,
        Dictionary<byte, int> refrFormTypeCounts,
        List<(uint FormId, byte FormType, long FileOffset, long Va)> allEntries)
    {
        var chainDepth = 0;
        var itemBuffer = new byte[12];
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
            var nextVa = BinaryUtils.ReadUInt32BE(itemBuffer);
            var keyFormId = BinaryUtils.ReadUInt32BE(itemBuffer, 4);
            var valVa = BinaryUtils.ReadUInt32BE(itemBuffer, 8);

            if (valVa != 0 && Xbox360MemoryUtils.IsValidPointerInDump(valVa, minidumpInfo))
            {
                var valVaLong = Xbox360MemoryUtils.VaToLong(valVa);

                // Validate 24-byte TESForm header is fully within a captured memory region
                if (minidumpInfo.IsVaRangeCaptured(valVaLong, 24))
                {
                    var formFileOffset = minidumpInfo.VirtualAddressToFileOffset(valVaLong);
                    if (formFileOffset.HasValue && formFileOffset.Value + 24 <= fileSize)
                    {
                        accessor.ReadArray(formFileOffset.Value, tesFormBuffer, 0, 24);
                        var formType = tesFormBuffer[4];
                        var structFormId = BinaryUtils.ReadUInt32BE(tesFormBuffer, 12);

                        // Verify FormID consistency
                        if (structFormId == keyFormId && keyFormId != 0)
                        {
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
                }
            }

            itemVa = nextVa;
        }
    }

    #endregion
}
