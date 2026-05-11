namespace FalloutXbox360Utils.Core.Formats.Esm.Reporting;

/// <summary>
///     Severity of a conversion progress event.
/// </summary>
public enum ConversionEventSeverity
{
    Info,
    Decision,
    Warning,
    Error
}

/// <summary>
///     A single observable event emitted by the DMP→ESP conversion pipeline.
/// </summary>
public sealed record ConversionProgressEvent
{
    public required DateTimeOffset Timestamp { get; init; }
    public required ConversionEventSeverity Severity { get; init; }
    public required string Phase { get; init; }
    public string? FormType { get; init; }
    public uint? FormId { get; init; }
    public required string Message { get; init; }

    /// <summary>
    ///     Optional aggregation key — used by the GUI to coalesce repetitive events
    ///     (e.g., "v1.skipped:CELL"). Null for one-off events.
    /// </summary>
    public string? Code { get; init; }
}
