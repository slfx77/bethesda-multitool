namespace BethesdaMultitool;

/// <summary>Per-file outcome of a carve-and-extract operation.</summary>
public enum ExtractionStatus
{
    NotExtracted,
    Extracted,

    /// <summary>
    ///     Extracted, but part of the file was never captured in the dump and is zero-filled.
    ///     The analysis pass has measured this all along (<c>CarvedFileInfo.IsTruncated</c>) —
    ///     it just had nowhere to say so, so a half-present file showed a green checkmark.
    /// </summary>
    Partial,
    Failed,
    Skipped
}
