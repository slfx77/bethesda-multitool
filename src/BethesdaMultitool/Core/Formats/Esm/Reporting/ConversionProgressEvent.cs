namespace BethesdaMultitool.Core.Formats.Esm.Reporting;

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
///     A single observable event emitted by the DMP→ESM conversion pipeline.
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
    ///     Optional machine-readable detail fields for reports that need more than the
    ///     stable event identity. Keys are event-code specific; consumers must tolerate
    ///     this property being absent so schema-v1 logs written before metadata was added
    ///     remain valid.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? Metadata { get; init; }

    /// <summary>
    ///     Optional aggregation key — used by the GUI to coalesce repetitive events
    ///     (e.g., "skipped:CELL"). Null for one-off events.
    /// </summary>
    public string? Code { get; init; }
}
