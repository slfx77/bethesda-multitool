namespace FalloutXbox360Utils.Core.Formats.Esm.Enums;

/// <summary>
///     A single named bit within a flags field.
/// </summary>
public readonly record struct FlagBit(uint Mask, string Name);
