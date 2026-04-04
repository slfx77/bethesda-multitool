namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Describes a family of raw-tail tri records for the packed statistical apply path.
/// </summary>
internal readonly record struct TriRawTailFamilyInfo(
    string Name,
    int CountHint,
    int RecordSize,
    bool IsNameBearing,
    TriRecordPayloadKind PayloadKind,
    int PayloadElementSize,
    bool UsesSharedPayloadStream,
    bool UsesRunningBaseOffset,
    bool FeedsPackedApplyTail);
