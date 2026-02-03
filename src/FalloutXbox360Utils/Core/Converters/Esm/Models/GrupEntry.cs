namespace FalloutXbox360Utils.Core.Converters.Esm;

/// <summary>
///     Entry for a GRUP in the conversion index.
/// </summary>
/// <param name="Type">GRUP type (0-10).</param>
/// <param name="Label">GRUP label value.</param>
/// <param name="Offset">Offset in input data.</param>
/// <param name="Size">Total GRUP size including header.</param>
public sealed record GrupEntry(int Type, uint Label, int Offset, int Size);
