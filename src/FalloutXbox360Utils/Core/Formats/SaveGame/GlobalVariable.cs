namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     A global variable entry from Global Data Type 3.
/// </summary>
public readonly record struct GlobalVariable(SaveRefId RefId, float Value);