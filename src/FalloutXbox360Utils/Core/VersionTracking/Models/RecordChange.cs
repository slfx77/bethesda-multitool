namespace FalloutXbox360Utils.Core.VersionTracking.Models;

/// <summary>
///     Type of change detected for a record.
/// </summary>
public enum ChangeType
{
    Added,
    Removed,
    Changed
}

/// <summary>
///     Describes a change to a single record between two build snapshots.
/// </summary>
public record RecordChange
{
    /// <summary>FormID of the changed record.</summary>
    public required uint FormId { get; init; }

    /// <summary>Editor ID (from whichever snapshot has it).</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name (from whichever snapshot has it).</summary>
    public string? FullName { get; init; }

    /// <summary>ESM record type (e.g., "QUST", "NPC_", "WEAP").</summary>
    public required string RecordType { get; init; }

    /// <summary>Whether this record was added, removed, or changed.</summary>
    public required ChangeType ChangeType { get; init; }

    /// <summary>Field-level changes (empty for Added/Removed, populated for Changed).</summary>
    public List<FieldChange> FieldChanges { get; init; } = [];
}
