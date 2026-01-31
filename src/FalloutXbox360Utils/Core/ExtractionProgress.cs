namespace FalloutXbox360Utils.Core;

/// <summary>
///     Progress information for extraction operations.
/// </summary>
public class ExtractionProgress
{
    public int FilesProcessed { get; set; }
    public int TotalFiles { get; set; }
    public string CurrentOperation { get; set; } = "";
    public double PercentComplete { get; set; }
}
