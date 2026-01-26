namespace EsmAnalyzer.Conversion;

/// <summary>
///     Entry for a WRLD record in the conversion index.
/// </summary>
/// <param name="FormId">The world FormID.</param>
/// <param name="Offset">Offset in input data.</param>
public sealed record WorldEntry(uint FormId, int Offset);