namespace FalloutXbox360Utils.Core.Formats.Esm.Merge;

/// <summary>
///     Outcome of merging a single DMP-derived record with a base ESM record.
/// </summary>
public sealed record MergeResult
{
    /// <summary>Subrecord-stream bytes for the merged record (no record header).</summary>
    public required byte[] SubrecordBytes { get; init; }

    /// <summary>Subrecord signatures whose bytes came from the DMP.</summary>
    public required IReadOnlyList<string> DmpSignaturesUsed { get; init; }

    /// <summary>Subrecord signatures retained verbatim from the ESM.</summary>
    public required IReadOnlyList<string> EsmSignaturesRetained { get; init; }

    /// <summary>DMP-only subrecords that were appended (not present in ESM).</summary>
    public required IReadOnlyList<string> DmpSignaturesAppended { get; init; }

    /// <summary>Non-fatal warnings produced during merge.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }
}
