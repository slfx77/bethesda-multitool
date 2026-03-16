namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     Result of attempting to extract save data from an STFS container.
///     Always includes diagnostics; payload is null on failure.
/// </summary>
internal sealed class StfsExtractionResult
{
    public StfsExtractionResult(byte[]? payload, StfsHeaderInfo? header, StfsFileEntry? fileEntry,
        IReadOnlyList<string> diagnostics, StfsExtractionMethod method)
    {
        Payload = payload;
        Header = header;
        FileEntry = fileEntry;
        Diagnostics = diagnostics;
        Method = method;
    }

    public byte[]? Payload { get; }
    public StfsHeaderInfo? Header { get; }
    public StfsFileEntry? FileEntry { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public StfsExtractionMethod Method { get; }

    /// <summary>Whether extraction succeeded (payload is available).</summary>
    public bool Success => Payload != null;

    /// <summary>Human-readable summary for error messages.</summary>
    public string DiagnosticSummary => string.Join("; ", Diagnostics);

    public static StfsExtractionResult Fail(string reason, List<string> diagnostics,
        StfsHeaderInfo? header = null)
    {
        diagnostics.Add(reason);
        return new StfsExtractionResult(null, header, null, diagnostics, StfsExtractionMethod.Failed);
    }
}