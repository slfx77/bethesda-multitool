namespace FalloutXbox360Utils.Core.Converters;

/// <summary>
///     Result from batch DDX to DDS conversion.
/// </summary>
public sealed class BatchConversionResult
{
    public int TotalFiles { get; set; }
    public int Converted { get; set; }
    public int Failed { get; set; }
    public int Unsupported { get; set; }
    public int ExitCode { get; set; }
    public bool WasCancelled { get; set; }
    public List<string> Errors { get; } = [];
}
