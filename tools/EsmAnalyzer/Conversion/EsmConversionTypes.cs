namespace EsmAnalyzer.Conversion;

/// <summary>
///     Entry for a WRLD record in the conversion index.
/// </summary>
/// <param name="FormId">The world FormID.</param>
/// <param name="Offset">Offset in input data.</param>
public sealed record WorldEntry(uint FormId, int Offset);

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

/// <summary>
///     Entry for a GRUP in the conversion index.
/// </summary>
/// <param name="Type">GRUP type (0-10).</param>
/// <param name="Label">GRUP label value.</param>
/// <param name="Offset">Offset in input data.</param>
/// <param name="Size">Total GRUP size including header.</param>
public sealed record GrupEntry(int Type, uint Label, int Offset, int Size);

/// <summary>
///     Index of records and groups for conversion.
///     Built during first pass through the file.
/// </summary>
public sealed class ConversionIndex
{
    /// <summary>World records by order found.</summary>
    public List<WorldEntry> Worlds { get; } = [];

    /// <summary>All cells indexed by FormID.</summary>
    public Dictionary<uint, CellEntry> CellsById { get; } = [];

    /// <summary>Exterior cells grouped by parent world FormID.</summary>
    public Dictionary<uint, List<CellEntry>> ExteriorCellsByWorld { get; } = [];

    /// <summary>Interior cells (no parent world).</summary>
    public List<CellEntry> InteriorCells { get; } = [];

    /// <summary>Cell child groups (Persistent/Temporary/VWD) indexed by (cellId, grupType).</summary>
    public Dictionary<(uint cellId, int type), List<GrupEntry>> CellChildGroups { get; } = [];
}