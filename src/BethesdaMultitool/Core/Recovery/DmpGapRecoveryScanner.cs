using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Coverage;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;

namespace BethesdaMultitool.Core.Recovery;

/// <summary>
///     Scans uncovered minidump regions for validated raw ESM records and RTTI-backed runtime TESForm structs.
/// </summary>
internal static class DmpGapRecoveryScanner
{
    private static readonly Dictionary<uint, string> NormalSignatures = BuildSignatureMap(reverseBytes: false);
    private static readonly Dictionary<uint, string> ReversedSignatures = BuildSignatureMap(reverseBytes: true);

    private static readonly string[] DialogueNeedles =
    [
        "Ulysses",
        "VDialogue",
        "Dialogue",
        "Topic",
        "DIAL",
        "INFO",
        "QUST",
        "NAM1"
    ];

    /// <summary>Scans the coverage report's unrecognized gaps for validated raw ESM records and RTTI-backed runtime structs, returning the recoverable candidates and summary counts.</summary>
    public static DmpGapRecoveryResult Scan(
        AnalysisResult result,
        CoverageResult coverage,
        IMemoryAccessor accessor,
        RttiReader? rttiReader,
        DmpGapRecoveryOptions? options = null)
    {
        options ??= DmpGapRecoveryOptions.DiscoverOnly;
        if (coverage.Error != null || result.MinidumpInfo is not { IsValid: true } minidump)
        {
            return new DmpGapRecoveryResult();
        }

        var moduleRanges = minidump.Modules
            .Select(m => (Start: (uint)m.BaseAddress, End: (uint)(m.BaseAddress + m.Size)))
            .OrderBy(m => m.Start)
            .ToArray();

        var knownRuntimeForms = BuildKnownRuntimeFormOffsetSet(result.EsmRecords);
        var rttiCache = new Dictionary<uint, RttiResult?>();
        var runtimeReader = CreateRuntimeReader(accessor, result, minidump);
        var dialogueParentage = runtimeReader == null || result.EsmRecords == null
            ? new Dictionary<uint, (uint TopicFormId, uint QuestFormId)>()
            : BuildDialogueParentage(result.EsmRecords, runtimeReader);

        var candidates = new List<DmpGapRecoveryCandidate>();
        var scannedGaps = 0;
        var directEvidenceGaps = 0;
        var rawRecordGaps = 0;
        var tesFormGaps = 0;
        var dialogueTextHits = 0;
        var esmSignatureHits = 0;

        foreach (var gap in coverage.Gaps)
        {
            if (gap.Size < options.MinGapSize || gap.Classification == GapClassification.ZeroFill)
            {
                continue;
            }

            var scanSize = (int)Math.Min(gap.Size, Math.Max(1024, options.MaxScanBytesPerGap));
            if (scanSize < 16 || gap.FileOffset + scanSize > coverage.FileSize)
            {
                continue;
            }

            var buffer = new byte[scanSize];
            accessor.ReadArray(gap.FileOffset, buffer, 0, scanSize);
            scannedGaps++;

            var beforeCount = candidates.Count;

            ScanRawRecords(buffer, gap, coverage.FileSize, candidates);
            ScanRuntimeForms(
                buffer,
                gap,
                result,
                minidump,
                moduleRanges,
                rttiReader,
                rttiCache,
                knownRuntimeForms,
                runtimeReader,
                dialogueParentage,
                candidates);

            var strings = ExtractStrings(buffer, 48);
            dialogueTextHits += strings.Count(HasDialogueNeedle);
            esmSignatureHits += CountSignatureHits(buffer);

            var added = candidates.Count - beforeCount;
            if (added <= 0)
            {
                continue;
            }

            var addedForGap = candidates.Skip(beforeCount).ToList();
            directEvidenceGaps++;
            if (addedForGap.Any(c => c.Kind == DmpGapRecoveryCandidateKind.RawEsmRecord))
            {
                rawRecordGaps++;
            }

            if (addedForGap.Any(c => c.Kind != DmpGapRecoveryCandidateKind.RawEsmRecord))
            {
                tesFormGaps++;
            }
        }

        var rawRecordCandidates = candidates.Count(c => c.Kind == DmpGapRecoveryCandidateKind.RawEsmRecord);
        var runtimeDialogueCandidates = candidates.Count(c =>
            c.Kind is DmpGapRecoveryCandidateKind.RuntimeDialogueInfo
                or DmpGapRecoveryCandidateKind.RuntimeDialogTopic);
        var runtimePlacedCandidates = candidates.Count(c =>
            c.Kind == DmpGapRecoveryCandidateKind.RuntimePlacedReference);

        return new DmpGapRecoveryResult
        {
            Candidates = candidates
                .OrderByDescending(c => c.Confidence)
                .ThenBy(c => c.FileOffset)
                .ToList(),
            Summary = new DmpGapRecoverySummary
            {
                ScannedGaps = scannedGaps,
                DirectEvidenceGaps = directEvidenceGaps,
                RawRecordGaps = rawRecordGaps,
                TesFormGaps = tesFormGaps,
                RawRecordCandidates = rawRecordCandidates,
                RuntimeTesFormCandidates = candidates.Count - rawRecordCandidates,
                RuntimeDialogueCandidates = runtimeDialogueCandidates,
                RuntimePlacedReferenceCandidates = runtimePlacedCandidates,
                DialogueTextHits = dialogueTextHits,
                EsmSignatureHits = esmSignatureHits
            }
        };
    }

    private static void ScanRawRecords(
        byte[] buffer,
        CoverageGap gap,
        long fileSize,
        List<DmpGapRecoveryCandidate> candidates)
    {
#pragma warning disable S127 // Intentional skip-ahead after a validated raw record.
        for (var i = 0; i <= buffer.Length - 4; i++)
        {
            if (RecordValidator.IsKnownFalsePositive(buffer, i))
            {
                continue;
            }

            var word = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(i, 4));
            DmpGapRecoveryCandidate? candidate = null;
            if (NormalSignatures.TryGetValue(word, out var normalSig))
            {
                candidate = TryCreateRawRecordCandidate(buffer, i, normalSig, false, gap, fileSize);
            }

            if (candidate == null && ReversedSignatures.TryGetValue(word, out var reversedSig))
            {
                candidate = TryCreateRawRecordCandidate(buffer, i, reversedSig, true, gap, fileSize);
            }

            if (candidate == null)
            {
                continue;
            }

            candidates.Add(candidate);
            var skip = (int)Math.Min(candidate.Length - 1, buffer.Length - i - 1);
            if (skip > 0)
            {
                i += skip;
            }
        }
#pragma warning restore S127
    }

    private static DmpGapRecoveryCandidate? TryCreateRawRecordCandidate(
        byte[] buffer,
        int offset,
        string signature,
        bool xboxEndian,
        CoverageGap gap,
        long fileSize)
    {
        if (offset + 24 > buffer.Length)
        {
            return null;
        }

        var remainingGapBytes = gap.Size - offset;
        if (remainingGapBytes < 24)
        {
            return null;
        }

        var sizeField = xboxEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset + 4, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 4, 4));

        if (signature == "GRUP")
        {
            if (sizeField < 24 || sizeField > remainingGapBytes ||
                gap.FileOffset + offset + sizeField > fileSize)
            {
                return null;
            }

            var groupType = xboxEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset + 12, 4))
                : BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 12, 4));
            if (groupType > 10)
            {
                return null;
            }

            return new DmpGapRecoveryCandidate
            {
                Kind = DmpGapRecoveryCandidateKind.RawEsmRecord,
                RecordType = signature,
                FileOffset = gap.FileOffset + offset,
                VirtualAddress = AddOffset(gap.VirtualAddress, offset),
                Length = sizeField,
                Confidence = 80,
                Evidence = $"valid GRUP header; groupType={groupType}; size={sizeField:N0}",
                Disposition = DmpGapRecoveryDisposition.AuditOnly,
                GapFileOffset = gap.FileOffset,
                GapSize = gap.Size,
                GapClassification = gap.Classification.ToString(),
                GapContext = gap.Context,
                IsBigEndian = xboxEndian,
                RawDataSize = sizeField
            };
        }

        if (sizeField > remainingGapBytes - 24 ||
            gap.FileOffset + offset + 24L + sizeField > fileSize)
        {
            return null;
        }

        var flags = xboxEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset + 8, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 8, 4));
        var formId = xboxEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset + 12, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 12, 4));

        if (!RecordValidator.IsValidMainRecordHeader(signature, sizeField, flags, formId))
        {
            return null;
        }

        // Gap buffers are uncovered heap, exactly where garbage that satisfies the scalar header gates
        // lives. Apply the same first-subrecord plausibility gate as the main dump scanner (one shared
        // implementation) so this recovery path can't fabricate phantom records — a genuine record's data
        // begins with a known subrecord signature. Header size is 24 for the FO3/FNV records this scans.
        if (!RecordValidator.HasPlausibleFirstSubrecord(buffer, offset, 24, buffer.Length, xboxEndian, flags))
        {
            return null;
        }

        return new DmpGapRecoveryCandidate
        {
            Kind = DmpGapRecoveryCandidateKind.RawEsmRecord,
            RecordType = signature,
            FormId = formId,
            FileOffset = gap.FileOffset + offset,
            VirtualAddress = AddOffset(gap.VirtualAddress, offset),
            Length = 24L + sizeField,
            Confidence = IsDialogueRecordType(signature) ? 98 : 95,
            Evidence = $"valid {(xboxEndian ? "Xbox" : "PC")} main-record header; dataSize={sizeField:N0}",
            Disposition = DmpGapRecoveryDisposition.PromoteRawRecord,
            GapFileOffset = gap.FileOffset,
            GapSize = gap.Size,
            GapClassification = gap.Classification.ToString(),
            GapContext = gap.Context,
            IsBigEndian = xboxEndian,
            RawDataSize = sizeField,
            RawFlags = flags
        };
    }

    private static void ScanRuntimeForms(
        byte[] buffer,
        CoverageGap gap,
        AnalysisResult result,
        MinidumpInfo minidump,
        (uint Start, uint End)[] moduleRanges,
        RttiReader? rttiReader,
        Dictionary<uint, RttiResult?> rttiCache,
        HashSet<long> knownRuntimeForms,
        RuntimeStructReader? runtimeReader,
        Dictionary<uint, (uint TopicFormId, uint QuestFormId)> dialogueParentage,
        List<DmpGapRecoveryCandidate> candidates)
    {
        var firstAligned = (int)((4 - (gap.FileOffset & 3)) & 3);
        for (var i = firstAligned; i <= buffer.Length - 16; i += 4)
        {
            var fileOffset = gap.FileOffset + i;
            if (knownRuntimeForms.Contains(fileOffset))
            {
                continue;
            }

            if (!TryReadRuntimeTesForm(
                    buffer,
                    i,
                    gap,
                    result.FileSize,
                    minidump,
                    moduleRanges,
                    rttiReader,
                    rttiCache,
                    out var candidate))
            {
                continue;
            }

            candidate = EnrichRuntimeCandidate(candidate, runtimeReader, dialogueParentage);
            candidates.Add(candidate);
        }
    }

    private static bool TryReadRuntimeTesForm(
        byte[] buffer,
        int offset,
        CoverageGap gap,
        long fileSize,
        MinidumpInfo minidump,
        (uint Start, uint End)[] moduleRanges,
        RttiReader? rttiReader,
        Dictionary<uint, RttiResult?> rttiCache,
        out DmpGapRecoveryCandidate candidate)
    {
        candidate = new DmpGapRecoveryCandidate();
        var vtable = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, 4));
        if (!IsModulePointer(vtable, moduleRanges))
        {
            return false;
        }

        var rtti = ResolveRtti(vtable, rttiReader, rttiCache);
        if (rtti == null || rtti.ObjectOffset != 0 || !IsTesFormClass(rtti))
        {
            return false;
        }

        var formType = buffer[offset + 4];
        var recordType = RuntimeBuildOffsets.GetRecordTypeCode(formType);
        if (recordType == null)
        {
            return false;
        }

        var formId = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset + 12, 4));
        if (!IsPlausibleFormId(formId))
        {
            return false;
        }

        var structSize = RuntimeBuildOffsets.GetStructSize(formType);
        if (structSize <= 0 ||
            offset + structSize > gap.Size ||
            gap.FileOffset + offset + structSize > fileSize)
        {
            return false;
        }

        var va = AddOffset(gap.VirtualAddress, offset);
        if (va.HasValue && !minidump.IsVaRangeCaptured(va.Value, structSize))
        {
            return false;
        }

        var kind = recordType switch
        {
            "INFO" => DmpGapRecoveryCandidateKind.RuntimeDialogueInfo,
            "DIAL" => DmpGapRecoveryCandidateKind.RuntimeDialogTopic,
            "REFR" or "ACHR" or "ACRE" => DmpGapRecoveryCandidateKind.RuntimePlacedReference,
            _ => DmpGapRecoveryCandidateKind.RuntimeTesForm
        };

        candidate = new DmpGapRecoveryCandidate
        {
            Kind = kind,
            RecordType = recordType,
            FormType = formType,
            ClassName = rtti.ClassName,
            FormId = formId,
            FileOffset = gap.FileOffset + offset,
            VirtualAddress = va,
            Length = structSize,
            Confidence = 90,
            Evidence = $"RTTI={rtti.ClassName}; vtable=0x{vtable:X8}; structSize={structSize:N0}",
            Disposition = DmpGapRecoveryDisposition.AuditOnly,
            GapFileOffset = gap.FileOffset,
            GapSize = gap.Size,
            GapClassification = gap.Classification.ToString(),
            GapContext = gap.Context,
            IsBigEndian = true
        };
        return true;
    }

    private static DmpGapRecoveryCandidate EnrichRuntimeCandidate(
        DmpGapRecoveryCandidate candidate,
        RuntimeStructReader? runtimeReader,
        Dictionary<uint, (uint TopicFormId, uint QuestFormId)> dialogueParentage)
    {
        if (runtimeReader == null || candidate.FormId is not { } formId)
        {
            return candidate;
        }

        if (candidate.Kind == DmpGapRecoveryCandidateKind.RuntimeDialogueInfo &&
            candidate.VirtualAddress is >= 0 and <= uint.MaxValue)
        {
            var info = runtimeReader.ReadRuntimeDialogueInfoFromVA((uint)candidate.VirtualAddress.Value);
            if (info == null || info.FormId != formId)
            {
                return candidate with
                {
                    Confidence = 75,
                    Evidence = candidate.Evidence + "; TESTopicInfo field decode failed"
                };
            }

            var parentage = dialogueParentage.GetValueOrDefault(formId);
            var hasParentage = parentage.TopicFormId != 0 && parentage.QuestFormId != 0;
            return candidate with
            {
                Confidence = hasParentage ? 98 : 92,
                Evidence = candidate.Evidence +
                           $"; decoded INFO index={info.InfoIndex}; quest=0x{info.QuestFormId.GetValueOrDefault():X8}; " +
                           $"linksTo={info.LinkToTopicFormIds.Count}; addTopics={info.AddTopicFormIds.Count}" +
                           (hasParentage
                               ? $"; parent topic=0x{parentage.TopicFormId:X8}, quest=0x{parentage.QuestFormId:X8}"
                               : "; no verified topic/quest parentage"),
                DecodedText = info.PromptText,
                TopicFormId = hasParentage ? parentage.TopicFormId : null,
                QuestFormId = hasParentage ? parentage.QuestFormId : info.QuestFormId,
                Disposition = hasParentage
                    ? DmpGapRecoveryDisposition.PromoteRuntimeDialogue
                    : DmpGapRecoveryDisposition.AuditOnly
            };
        }

        if (candidate.Kind == DmpGapRecoveryCandidateKind.RuntimeDialogTopic)
        {
            var entry = ToRuntimeEntry(candidate);
            var topic = runtimeReader.ReadRuntimeDialogTopic(entry);
            if (topic == null)
            {
                return candidate with
                {
                    Confidence = 78,
                    Evidence = candidate.Evidence + "; TESTopic field decode failed"
                };
            }

            var links = runtimeReader.WalkTopicQuestInfoList(entry);
            var linkedInfos = links.Sum(l => l.InfoEntries.Count);
            return candidate with
            {
                Confidence = linkedInfos > 0 ? 98 : 92,
                Evidence = candidate.Evidence +
                           $"; decoded DIAL topicType={topic.TopicType}; questLinks={links.Count}; infoLinks={linkedInfos}",
                DecodedText = topic.FullName ?? topic.DummyPrompt,
                Disposition = DmpGapRecoveryDisposition.PromoteRuntimeDialogue
            };
        }

        if (candidate.Kind == DmpGapRecoveryCandidateKind.RuntimePlacedReference)
        {
            var refr = runtimeReader.ReadRuntimeRefr(ToRuntimeEntry(candidate));
            if (refr?.Position == null || refr.BaseFormId == 0 || refr.ParentCellFormId is null or 0)
            {
                return candidate with
                {
                    Evidence = candidate.Evidence + "; placed-ref decode missing base/position/parent cell"
                };
            }

            return candidate with
            {
                Confidence = 98,
                Evidence = candidate.Evidence +
                           $"; base=0x{refr.BaseFormId:X8}; parentCell=0x{refr.ParentCellFormId.Value:X8}",
                Disposition = DmpGapRecoveryDisposition.PromotePlacedReference
            };
        }

        return candidate;
    }

    private static RuntimeStructReader? CreateRuntimeReader(
        IMemoryAccessor accessor,
        AnalysisResult result,
        MinidumpInfo minidump)
    {
        if (result.EsmRecords == null)
        {
            return null;
        }

        var npcEntries = result.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x2A).ToList();
        var worldEntries = result.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x41).ToList();
        var cellEntries = result.EsmRecords.RuntimeEditorIds.Where(entry => entry.FormType == 0x39).ToList();
        return RuntimeStructReader.CreateWithAutoDetect(
            accessor,
            result.FileSize,
            minidump,
            result.EsmRecords.RuntimeRefrFormEntries,
            npcEntries,
            worldEntries,
            cellEntries,
            result.EsmRecords.RuntimeEditorIds,
            result.EsmRecords.RuntimeLandFormEntries);
    }

    private static Dictionary<uint, (uint TopicFormId, uint QuestFormId)> BuildDialogueParentage(
        EsmRecordScanResult scanResult,
        RuntimeStructReader runtimeReader)
    {
        var result = new Dictionary<uint, (uint TopicFormId, uint QuestFormId)>();
        foreach (var entry in scanResult.RuntimeEditorIds.Where(e =>
                     RuntimeBuildOffsets.GetRecordTypeCode(e.FormType) == "DIAL"))
        {
            var links = runtimeReader.WalkTopicQuestInfoList(entry);
            foreach (var link in links)
            {
                foreach (var info in link.InfoEntries)
                {
                    result.TryAdd(info.FormId, (entry.FormId, link.QuestFormId));
                }
            }
        }

        return result;
    }

    private static RuntimeEditorIdEntry ToRuntimeEntry(DmpGapRecoveryCandidate candidate)
    {
        return new RuntimeEditorIdEntry
        {
            EditorId = $"__RECOVERED_{candidate.RecordType}_{candidate.FormId.GetValueOrDefault():X8}",
            FormId = candidate.FormId.GetValueOrDefault(),
            FormType = candidate.FormType.GetValueOrDefault(),
            TesFormOffset = candidate.FileOffset,
            TesFormPointer = candidate.VirtualAddress
        };
    }

    private static HashSet<long> BuildKnownRuntimeFormOffsetSet(EsmRecordScanResult? scanResult)
    {
        var offsets = new HashSet<long>();
        if (scanResult == null)
        {
            return offsets;
        }

        foreach (var entry in scanResult.RuntimeEditorIds
                     .Concat(scanResult.RuntimeLandFormEntries)
                     .Concat(scanResult.RuntimeRefrFormEntries))
        {
            if (entry.TesFormOffset is > 0)
            {
                offsets.Add(entry.TesFormOffset.Value);
            }
        }

        return offsets;
    }

    private static int CountSignatureHits(byte[] buffer)
    {
        var count = 0;
        for (var i = 0; i <= buffer.Length - 4; i++)
        {
            var word = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(i, 4));
            if (NormalSignatures.ContainsKey(word) || ReversedSignatures.ContainsKey(word))
            {
                count++;
            }
        }

        return count;
    }

    private static RttiResult? ResolveRtti(
        uint vtable,
        RttiReader? rttiReader,
        Dictionary<uint, RttiResult?> rttiCache)
    {
        if (rttiReader == null)
        {
            return null;
        }

        if (rttiCache.TryGetValue(vtable, out var cached))
        {
            return cached;
        }

        var resolved = rttiReader.ResolveVtable(vtable);
        rttiCache[vtable] = resolved;
        return resolved;
    }

    private static bool IsTesFormClass(RttiResult rtti)
    {
        if (rtti.ClassName is "TESForm" or "TESObject")
        {
            return true;
        }

        return rtti.BaseClasses?.Any(b =>
            b.ClassName is "TESForm" or "TESObject") ?? false;
    }

    private static bool IsPlausibleFormId(uint formId)
    {
        if (formId is 0 or 0xFFFFFFFF)
        {
            return false;
        }

        var loadOrder = formId >> 24;
        return loadOrder <= 0x0F || loadOrder == 0xFF;
    }

    private static bool IsModulePointer(uint value, (uint Start, uint End)[] moduleRanges)
    {
        foreach (var (start, end) in moduleRanges)
        {
            if (value >= start && value < end)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDialogueRecordType(string sig)
    {
        return sig is "DIAL" or "INFO" or "QUST" or "PACK" or "IDLE" or "MESG" or "NOTE";
    }

    private static long? AddOffset(long? value, long offset)
    {
        return value.HasValue ? value.Value + offset : null;
    }

    private static Dictionary<uint, string> BuildSignatureMap(bool reverseBytes)
    {
        var map = new Dictionary<uint, string>();
        foreach (var signature in EsmRecordTypes.MainRecordTypes.Keys)
        {
            var bytes = Encoding.ASCII.GetBytes(signature);
            if (reverseBytes)
            {
                Array.Reverse(bytes);
            }

            map[BinaryPrimitives.ReadUInt32BigEndian(bytes)] = signature;
        }

        return map;
    }

    private static List<string> ExtractStrings(byte[] buffer, int limit)
    {
        var strings = new List<string>();
        var start = -1;
        for (var i = 0; i <= buffer.Length; i++)
        {
            var atEnd = i == buffer.Length;
            var printable = !atEnd && buffer[i] is >= 0x20 and <= 0x7E;

            if (printable)
            {
                if (start < 0)
                {
                    start = i;
                }

                continue;
            }

            if (start >= 0 && i - start >= 4)
            {
                var text = Encoding.ASCII.GetString(buffer, start, i - start);
                if (strings.Count == 0 || !string.Equals(strings[^1], text, StringComparison.Ordinal))
                {
                    strings.Add(text);
                    if (strings.Count >= limit)
                    {
                        return strings;
                    }
                }
            }

            start = -1;
        }

        return strings;
    }

    private static bool HasDialogueNeedle(string text)
    {
        return DialogueNeedles.Any(needle =>
            text.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }
}
