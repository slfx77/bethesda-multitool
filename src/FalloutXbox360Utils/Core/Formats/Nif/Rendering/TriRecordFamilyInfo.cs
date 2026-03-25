namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Describes a decompilation-confirmed FRTRI003 record family after the GECK
///     has materialized it into the generation context. These offsets are context
///     object offsets, not raw on-disk byte offsets. Where payload metadata is
///     present, the nested payload has been confirmed as a plain contiguous typed
///     array in the materialized record.
/// </summary>
internal readonly record struct TriRecordFamilyInfo(
    string Name,
    int CountHint,
    int RecordSize,
    TriRecordPayloadKind PayloadKind,
    int PayloadElementSize,
    int GenerationContextOffset,
    int? MaterializedPayloadRootOffset,
    int? MaterializedPayloadBeginOffset,
    int? MaterializedPayloadEndOffset,
    int? MaterializedPayloadCapacityOffset,
    int? PreservedScalarOffset);