namespace BethesdaMultitool.Core.Formats.SaveGame.Decoding;

/// <summary>
///     Defines a single change flag: a bitmask and its human-readable name.
/// </summary>
public readonly record struct ChangeFlagDef(uint Mask, string Name);
