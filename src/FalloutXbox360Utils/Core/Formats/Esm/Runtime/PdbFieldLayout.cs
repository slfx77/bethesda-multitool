namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime;

/// <summary>
///     A single field in a flattened PDB struct layout.
/// </summary>
internal sealed record PdbFieldLayout(
    string Name,
    int Offset,
    int Size,
    string Kind,
    string? Owner,
    string? TypeDetail);
