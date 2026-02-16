namespace FalloutXbox360Utils.Core.VersionTracking.Models;

/// <summary>
///     Lightweight script snapshot for version tracking.
/// </summary>
public record TrackedScript
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? SourceText { get; init; }
    public string ScriptType { get; init; } = "Object";
    public uint VariableCount { get; init; }
    public uint RefObjectCount { get; init; }
    public uint CompiledSize { get; init; }
}
