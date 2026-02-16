namespace FalloutXbox360Utils.Core.VersionTracking.Models;

/// <summary>
///     Lightweight NPC snapshot for version tracking.
/// </summary>
public record TrackedNpc
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }
    public uint? RaceFormId { get; init; }
    public uint? ClassFormId { get; init; }
    public uint? ScriptFormId { get; init; }
    public byte[]? SpecialStats { get; init; }
    public byte[]? Skills { get; init; }
    public List<uint> FactionFormIds { get; init; } = [];
    public List<uint> SpellFormIds { get; init; } = [];
    public ushort? Level { get; init; }
}
