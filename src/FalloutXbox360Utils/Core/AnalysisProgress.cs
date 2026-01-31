namespace FalloutXbox360Utils.Core;

/// <summary>
///     Progress information for analysis operations.
/// </summary>
public class AnalysisProgress
{
    public long BytesProcessed { get; set; }
    public long TotalBytes { get; set; }
    public int FilesFound { get; set; }

    /// <summary>
    ///     Current phase of analysis (e.g., "Scanning", "Parsing", "Metadata").
    /// </summary>
    public string Phase { get; set; } = "Scanning";

    /// <summary>
    ///     Overall progress percentage across all phases (0-100).
    /// </summary>
    public double PercentComplete { get; set; }
}
