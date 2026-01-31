namespace FalloutXbox360Utils.Core.Formats.Scda;

/// <summary>
///     Information about an extracted script for analysis output.
/// </summary>
public record ScriptInfo
{
    public required long Offset { get; init; }
    public required int BytecodeSize { get; init; }
    public string? ScriptName { get; init; }
    public string? QuestName { get; init; }
    public bool HasSource { get; init; }
}
