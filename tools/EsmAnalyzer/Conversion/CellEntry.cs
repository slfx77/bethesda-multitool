namespace EsmAnalyzer.Conversion;

/// <summary>
///     Entry for a CELL record in the conversion index.
/// </summary>
/// <param name="FormId">The cell FormID.</param>
/// <param name="Offset">Offset in input data.</param>
/// <param name="Flags">Record flags.</param>
/// <param name="DataSize">Record data size.</param>
/// <param name="IsExterior">Whether this is an exterior cell (has XCLC subrecord).</param>
/// <param name="GridX">Grid X coordinate for exterior cells.</param>
/// <param name="GridY">Grid Y coordinate for exterior cells.</param>
/// <param name="WorldId">Parent world FormID for exterior cells.</param>
public sealed record CellEntry(
    uint FormId,
    int Offset,
    uint Flags,
    uint DataSize,
    bool IsExterior,
    int? GridX,
    int? GridY,
    uint? WorldId);