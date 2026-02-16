namespace FalloutXbox360Utils.Core.VersionTracking.Models;

/// <summary>
///     Lightweight cell/worldspace snapshot for version tracking.
/// </summary>
public record TrackedLocation
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }

    /// <summary>"CELL" or "WRLD".</summary>
    public required string RecordType { get; init; }

    /// <summary>Cell grid X (null for interior cells and worldspaces).</summary>
    public int? GridX { get; init; }

    /// <summary>Cell grid Y (null for interior cells and worldspaces).</summary>
    public int? GridY { get; init; }

    /// <summary>Parent worldspace FormID (null for interior cells and worldspaces).</summary>
    public uint? WorldspaceFormId { get; init; }

    /// <summary>Whether this is an interior cell (always false for WRLD).</summary>
    public bool IsInterior { get; init; }
}
