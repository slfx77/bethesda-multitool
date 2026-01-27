namespace Xbox360MemoryCarver.Core.Converters.Esm.Schema;

/// <summary>
///     Parsed GRUP header data.
/// </summary>
/// <param name="Size">Total size of GRUP including header.</param>
/// <param name="Label">Label value (meaning depends on Type).</param>
/// <param name="Type">GRUP type (0=Top-level, 1=World Children, etc.).</param>
/// <param name="Stamp">Timestamp.</param>
/// <param name="Unknown">Unknown field.</param>
public readonly record struct ParsedGrupHeader(
    uint Size,
    uint Label,
    uint Type,
    uint Stamp,
    uint Unknown);
