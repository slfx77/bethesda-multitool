using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.RuntimeBuffer;

namespace BethesdaMultitool.Core.Recovery;

public sealed record DialogueRecoveryText(int ResponseNumber, string Text, string Origin);
public sealed record DialogueRecoveryCandidate(string Text, long StringFileOffset, long? StringVirtualAddress,
    bool AmbiguousOwners, RuntimeStringOwnershipCandidate Candidate)
{
    /// <summary>How this candidate is associated with the current INFO; not a new ownership claim.</summary>
    public string LinkBasis { get; init; } = "form-id";
}

public sealed record DialogueRecoveryRow
{
    public uint FormId { get; init; }
    public int Variant { get; init; }
    public string? EditorId { get; init; }
    public string RecordStatus { get; init; } = "info-not-found-in-parsed-records";
    public string ResponseStatus { get; init; } = "Unavailable";
    public string MappingStatus { get; init; } = "not-applicable";
    public string MappingBoundary { get; init; } = "No runtime INFO";
    public string? PromptText { get; init; }
    public uint? SpeakerFormId { get; init; }
    public long RawRecordOffset { get; init; }
    public long RuntimeStructOffset { get; init; }
    public uint TesFileOffset { get; init; }
    public long? TargetVirtualAddress { get; init; }
    public long? MappedDumpOffset { get; init; }
    public int CalibrationSegmentCount { get; init; }
    public IReadOnlyList<DialogueRecoveryText> Responses { get; init; } = [];
    public IReadOnlyList<DialogueResultScript> RecoveredResultScripts { get; init; } = [];
    public string OwnershipStatus { get; init; } = "not-run";
    public IReadOnlyList<DialogueRecoveryCandidate> OwnershipCandidates { get; init; } = [];
}

/// <summary>Per-capture evidence projection. Cross-capture results never replace the current capture's bytes.</summary>
internal static class DialogueCorpusRecovery
{
    internal static IReadOnlyList<DialogueRecoveryRow> BuildRows(IEnumerable<uint> targets,
        IEnumerable<DialogueRecord> dialogues, Func<DialogueRecord, DialogueInfoProvenanceReport> inspect,
        RuntimeStringOwnershipAnalysis? ownership, CancellationToken cancellationToken = default)
    {
        var records = dialogues.ToLookup(d => d.FormId);
        var candidates = ownership?.AllHits.SelectMany(hit => (hit.OwnerResolution?.Candidates ?? [])
                .Select(c => new DialogueRecoveryCandidate(hit.Text, hit.FileOffset, hit.VirtualAddress,
                    hit.OwnerResolution!.HasAmbiguousOwners, c))).ToArray() ?? [];
        var candidatesByFormId = candidates.Where(c => c.Candidate.OwnerFormId.HasValue)
            .ToLookup(c => c.Candidate.OwnerFormId!.Value);
        // Anonymous runtime INFOs may have a validated RTTI/field claim without an EditorID entry.
        // Keep an exact same-capture object association without inventing a candidate FormID.
        var anonymousByOwnerOffset = candidates.Where(c => c.Candidate.OwnerFormId is null &&
                c.Candidate.OwnerRecordType == "INFO" && c.Candidate.OwnerFileOffset is > 0)
            .ToLookup(c => c.Candidate.OwnerFileOffset!.Value);
        var rows = new List<DialogueRecoveryRow>();
        foreach (var formId in targets.Distinct().Order())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var linkedById = candidatesByFormId[formId].ToArray();
            var ownershipStatus = ownership == null ? "not-run" : linkedById.Length == 0 ? "no-linked-candidates" : "candidates-retained";
            var variants = records[formId].ToArray();
            if (variants.Length == 0)
            {
                rows.Add(new DialogueRecoveryRow { FormId = formId, OwnershipCandidates = linkedById, OwnershipStatus = ownershipStatus });
                continue;
            }
            for (var index = 0; index < variants.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = variants[index];
                var linked = record.RuntimeStructOffset > 0
                    ? linkedById.Concat(anonymousByOwnerOffset[record.RuntimeStructOffset]
                        .Select(candidate => candidate with { LinkBasis = "runtime-owner-offset" })).ToArray()
                    : linkedById;
                var variantOwnershipStatus = ownership == null ? "not-run"
                    : linked.Length == 0 ? "no-linked-candidates" : "candidates-retained";
                var report = inspect(record);
                var mapping = report.ResultScriptRecovery;
                var responses = record.Responses.Where(r => !string.IsNullOrEmpty(r.Text))
                    .Select(r => new DialogueRecoveryText(r.ResponseNumber, r.Text!, "parsed INFO response")).ToList();
                if (mapping.RecordDataBytes is { } bytes)
                {
                    var recovered = InfoResponseTextExtractor.Extract(new ParsedMainRecord
                    {
                        Header = new MainRecordHeader { Signature = "INFO", FormId = formId },
                        Subrecords = EsmParser.ParseSubrecords(bytes, bigEndian: true)
                    });
                    responses.AddRange(recovered.Select(pair => new DialogueRecoveryText(pair.Key, pair.Value, "mapped TES-file INFO payload")));
                }
                rows.Add(new DialogueRecoveryRow
                {
                    FormId = formId, Variant = index, EditorId = record.EditorId, RecordStatus = "parsed-info-present",
                    ResponseStatus = responses.Count > 0 ? "Recovered" : "Unavailable",
                    MappingStatus = mapping.Status.ToString(), MappingBoundary = DescribeMapping(report),
                    PromptText = record.PromptText, SpeakerFormId = record.SpeakerFormId,
                    RawRecordOffset = record.RawRecordOffset, RuntimeStructOffset = record.RuntimeStructOffset,
                    TesFileOffset = record.TesFileOffset, TargetVirtualAddress = mapping.TargetVirtualAddress,
                    MappedDumpOffset = mapping.MappedDumpOffset, CalibrationSegmentCount = report.TesFileSegments.Count,
                    Responses = responses, RecoveredResultScripts = mapping.Scripts,
                    OwnershipCandidates = linked, OwnershipStatus = variantOwnershipStatus
                });
            }
        }
        return rows;
    }

    private static string DescribeMapping(DialogueInfoProvenanceReport report) => report.ResultScriptRecovery.Status switch
    {
        DialogueTesFileScriptRecoveryStatus.NoTesFileOffset => "No source-file offset",
        DialogueTesFileScriptRecoveryStatus.UncalibratedBase => "Uncalibrated mapping",
        DialogueTesFileScriptRecoveryStatus.MappedPageMissing when !report.TesFileSegments.Any(s => s.Contains(report.Dialogue.TesFileOffset)) =>
            "Outside calibrated segments",
        DialogueTesFileScriptRecoveryStatus.MappedPageMissing => "Mapped page not captured",
        DialogueTesFileScriptRecoveryStatus.HeaderReadFailed => "Incomplete mapped header",
        DialogueTesFileScriptRecoveryStatus.PayloadReadFailed => "Incomplete mapped payload",
        DialogueTesFileScriptRecoveryStatus.RecordTooLarge => "Exceeds 64 KiB inspection limit",
        DialogueTesFileScriptRecoveryStatus.CompressedRecord => "Compressed payload not inspected",
        DialogueTesFileScriptRecoveryStatus.FormIdMismatch => "FormID mismatch",
        DialogueTesFileScriptRecoveryStatus.SignatureMismatch => "Signature mismatch",
        _ => "Matched INFO payload"
    };
}
