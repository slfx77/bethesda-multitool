namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;

/// <summary>
///     Best-effort contiguous raw region candidate for the differential family.
///     This is inferred from the current anchor samples and from the
///     decompilation-confirmed runtime materialization path, not from a full
///     byte-perfect parser for the whole post-vector tail.
/// </summary>
internal readonly record struct TriDifferentialRegionCandidate(
    int Offset,
    int Length,
    int RecordCount,
    int VertexCount,
    int PackedDeltaPayloadLengthPerRecord,
    TriDifferentialRecordCandidate[] Records);
