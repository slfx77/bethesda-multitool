namespace FalloutXbox360Utils.Core.VersionTracking.Models;

/// <summary>
///     Lightweight terminal snapshot for version tracking.
/// </summary>
public record TrackedTerminal
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }
    public string? HeaderText { get; init; }
    public byte Difficulty { get; init; }
    public byte Flags { get; init; }
    public List<string> MenuItemTexts { get; init; } = [];
    public int MenuItemCount { get; init; }
    public string? Password { get; init; }
}
