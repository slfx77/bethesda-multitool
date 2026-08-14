using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Records;

namespace BethesdaMultitool.Core.Recovery;

/// <summary>
///     Applies opt-in recoverable gap candidates to the existing semantic scan result.
/// </summary>
internal static class DmpGapRecoveryPromoter
{
    /// <summary>Merges the opt-in-enabled gap candidates into <paramref name="scanResult" /> as raw records, runtime dialogue, or placed refs, skipping duplicates, and returns promotion counts.</summary>
    public static DmpGapRecoveryPromotionResult Apply(
        EsmRecordScanResult scanResult,
        IReadOnlyList<DmpGapRecoveryCandidate> candidates,
        DmpGapRecoveryOptions options)
    {
        if (!options.AnyPromotion || candidates.Count == 0)
        {
            return new DmpGapRecoveryPromotionResult
            {
                CandidatesConsidered = candidates.Count,
                SkippedCandidates = candidates.Count
            };
        }

        var existingRawOffsets = scanResult.MainRecords
            .Select(r => r.Offset)
            .ToHashSet();
        var existingRuntimeDialogue = scanResult.RuntimeEditorIds
            .Where(e => e.TesFormOffset.HasValue)
            .Select(e => e.TesFormOffset!.Value)
            .ToHashSet();
        var existingRuntimeRefs = scanResult.RuntimeRefrFormEntries
            .Where(e => e.TesFormOffset.HasValue)
            .Select(e => e.TesFormOffset!.Value)
            .ToHashSet();

        var rawPromoted = 0;
        var dialoguePromoted = 0;
        var refsPromoted = 0;
        var skipped = 0;

        foreach (var candidate in candidates)
        {
            switch (candidate.Disposition)
            {
                case DmpGapRecoveryDisposition.PromoteRawRecord when options.PromoteRawRecords:
                    if (TryPromoteRawRecord(scanResult, candidate, existingRawOffsets))
                    {
                        rawPromoted++;
                    }
                    else
                    {
                        skipped++;
                    }

                    break;

                case DmpGapRecoveryDisposition.PromoteRuntimeDialogue when options.PromoteRuntimeDialogue:
                    if (TryPromoteRuntimeDialogue(scanResult, candidate, existingRuntimeDialogue))
                    {
                        dialoguePromoted++;
                    }
                    else
                    {
                        skipped++;
                    }

                    break;

                case DmpGapRecoveryDisposition.PromotePlacedReference when options.PromotePlacedRefs:
                    if (TryPromotePlacedReference(scanResult, candidate, existingRuntimeRefs))
                    {
                        refsPromoted++;
                    }
                    else
                    {
                        skipped++;
                    }

                    break;

                default:
                    skipped++;
                    break;
            }
        }

        if (rawPromoted > 0)
        {
            scanResult.MainRecords.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        }

        return new DmpGapRecoveryPromotionResult
        {
            CandidatesConsidered = candidates.Count,
            RawRecordsPromoted = rawPromoted,
            RuntimeDialogueEntriesPromoted = dialoguePromoted,
            RuntimePlacedReferenceEntriesPromoted = refsPromoted,
            SkippedCandidates = skipped
        };
    }

    private static bool TryPromoteRawRecord(
        EsmRecordScanResult scanResult,
        DmpGapRecoveryCandidate candidate,
        HashSet<long> existingRawOffsets)
    {
        if (candidate.RecordType is null ||
            candidate.FormId is not { } formId ||
            candidate.RawDataSize is not { } dataSize ||
            !existingRawOffsets.Add(candidate.FileOffset))
        {
            return false;
        }

        scanResult.MainRecords.Add(new DetectedMainRecord(
            candidate.RecordType,
            dataSize,
            candidate.RawFlags.GetValueOrDefault(),
            formId,
            candidate.FileOffset,
            candidate.IsBigEndian)
        {
            FormVersion = candidate.FormVersion
        });
        return true;
    }

    private static bool TryPromoteRuntimeDialogue(
        EsmRecordScanResult scanResult,
        DmpGapRecoveryCandidate candidate,
        HashSet<long> existingRuntimeDialogue)
    {
        if (candidate.FormId is not { } formId ||
            candidate.FormType is not { } formType ||
            !existingRuntimeDialogue.Add(candidate.FileOffset))
        {
            return false;
        }

        if (candidate.RecordType is not ("DIAL" or "INFO"))
        {
            return false;
        }

        scanResult.RuntimeEditorIds.Add(new RuntimeEditorIdEntry
        {
            EditorId = $"__RECOVERED_{candidate.RecordType}_{formId:X8}",
            FormId = formId,
            FormType = formType,
            TesFormOffset = candidate.FileOffset,
            TesFormPointer = candidate.VirtualAddress,
            DialogueLine = candidate.DecodedText
        });
        return true;
    }

    private static bool TryPromotePlacedReference(
        EsmRecordScanResult scanResult,
        DmpGapRecoveryCandidate candidate,
        HashSet<long> existingRuntimeRefs)
    {
        if (candidate.FormId is not { } formId ||
            candidate.FormType is not { } formType ||
            candidate.RecordType is not ("REFR" or "ACHR" or "ACRE") ||
            !existingRuntimeRefs.Add(candidate.FileOffset))
        {
            return false;
        }

        scanResult.RuntimeRefrFormEntries.Add(new RuntimeEditorIdEntry
        {
            EditorId = $"__RECOVERED_{candidate.RecordType}_{formId:X8}",
            FormId = formId,
            FormType = formType,
            TesFormOffset = candidate.FileOffset,
            TesFormPointer = candidate.VirtualAddress
        });
        return true;
    }
}
