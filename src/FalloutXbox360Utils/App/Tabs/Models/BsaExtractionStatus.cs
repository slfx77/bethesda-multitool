namespace FalloutXbox360Utils;

/// <summary>
///     Status of a file during BSA extraction.
/// </summary>
public enum BsaExtractionStatus
{
    Pending,
    Extracting,
    Converting,
    Done,
    Skipped,
    Failed
}
