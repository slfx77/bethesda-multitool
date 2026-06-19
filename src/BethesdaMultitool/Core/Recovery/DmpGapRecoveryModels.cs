namespace BethesdaMultitool.Core.Recovery;

/// <summary>
///     Category of recoverable evidence found inside a minidump coverage gap.
/// </summary>
public enum DmpGapRecoveryCandidateKind
{
    RawEsmRecord,
    RuntimeTesForm,
    RuntimeDialogueInfo,
    RuntimeDialogTopic,
    RuntimePlacedReference
}

/// <summary>
///     How a candidate can be used by downstream consumers.
/// </summary>
public enum DmpGapRecoveryDisposition
{
    AuditOnly,
    PromoteRawRecord,
    PromoteRuntimeDialogue,
    PromotePlacedReference
}

/// <summary>
///     One validated recoverable object inside a previously-uncovered DMP gap.
/// </summary>
public sealed record DmpGapRecoveryCandidate
{
    public DmpGapRecoveryCandidateKind Kind { get; init; }
    public string? RecordType { get; init; }
    public byte? FormType { get; init; }
    public string? ClassName { get; init; }
    public uint? FormId { get; init; }
    public long FileOffset { get; init; }
    public long? VirtualAddress { get; init; }
    public long Length { get; init; }
    public int Confidence { get; init; }
    public string Evidence { get; init; } = "";
    public DmpGapRecoveryDisposition Disposition { get; init; }
    public long GapFileOffset { get; init; }
    public long GapSize { get; init; }
    public string GapClassification { get; init; } = "";
    public string GapContext { get; init; } = "";
    public bool IsBigEndian { get; init; }
    public uint? RawDataSize { get; init; }
    public uint? RawFlags { get; init; }
    public string? DecodedText { get; init; }
    public uint? TopicFormId { get; init; }
    public uint? QuestFormId { get; init; }

    public string DisplayFamily => Kind switch
    {
        DmpGapRecoveryCandidateKind.RawEsmRecord => $"Raw ESM {RecordType ?? "record"}",
        DmpGapRecoveryCandidateKind.RuntimeDialogueInfo => "Runtime INFO",
        DmpGapRecoveryCandidateKind.RuntimeDialogTopic => "Runtime DIAL",
        DmpGapRecoveryCandidateKind.RuntimePlacedReference => $"Runtime {RecordType ?? "placed ref"}",
        _ => $"Runtime {RecordType ?? ClassName ?? "TESForm"}"
    };
}

/// <summary>
///     Aggregate counts from a gap recovery scan.
/// </summary>
public sealed record DmpGapRecoverySummary
{
    public int ScannedGaps { get; init; }
    public int DirectEvidenceGaps { get; init; }
    public int RawRecordGaps { get; init; }
    public int TesFormGaps { get; init; }
    public int RawRecordCandidates { get; init; }
    public int RuntimeTesFormCandidates { get; init; }
    public int RuntimeDialogueCandidates { get; init; }
    public int RuntimePlacedReferenceCandidates { get; init; }
    public int DialogueTextHits { get; init; }
    public int EsmSignatureHits { get; init; }
    public int Candidates => RawRecordCandidates + RuntimeTesFormCandidates;
}

/// <summary>
///     Full result of scanning DMP coverage gaps for recoverable data.
/// </summary>
public sealed record DmpGapRecoveryResult
{
    public DmpGapRecoverySummary Summary { get; init; } = new();
    public IReadOnlyList<DmpGapRecoveryCandidate> Candidates { get; init; } = [];
}

/// <summary>
///     Counts for records promoted from recoverable gap candidates into semantic parsing inputs.
/// </summary>
public sealed record DmpGapRecoveryPromotionResult
{
    public int CandidatesConsidered { get; init; }
    public int RawRecordsPromoted { get; init; }
    public int RuntimeDialogueEntriesPromoted { get; init; }
    public int RuntimePlacedReferenceEntriesPromoted { get; init; }
    public int SkippedCandidates { get; init; }
}

/// <summary>
///     Options controlling discovery and optional promotion of validated gap data.
/// </summary>
public sealed record DmpGapRecoveryOptions
{
    public bool DiscoverCandidates { get; init; }
    public bool PromoteRawRecords { get; init; }
    public bool PromoteRuntimeDialogue { get; init; }
    public bool PromotePlacedRefs { get; init; }
    public int MinGapSize { get; init; } = 32;
    public int MaxScanBytesPerGap { get; init; } = 64 * 1024;

    public bool AnyPromotion => PromoteRawRecords || PromoteRuntimeDialogue || PromotePlacedRefs;

    public static DmpGapRecoveryOptions Disabled { get; } = new();

    public static DmpGapRecoveryOptions DiscoverOnly { get; } = new()
    {
        DiscoverCandidates = true
    };

    public static DmpGapRecoveryOptions PromoteAllValidated { get; } = new()
    {
        DiscoverCandidates = true,
        PromoteRawRecords = true,
        PromoteRuntimeDialogue = true,
        PromotePlacedRefs = true
    };
}
