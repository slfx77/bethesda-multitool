using System.Collections.ObjectModel;

namespace FalloutXbox360Utils;

/// <summary>
///     Node in the ESM data browser tree.
///     Supports lazy loading for performance.
/// </summary>
public sealed class EsmBrowserNode
{
    public string DisplayName { get; init; } = "";
    public string? FormIdHex { get; init; }
    public string? EditorId { get; init; }

    /// <summary>"Category", "RecordType", or "Record".</summary>
    public string NodeType { get; init; } = "";

    /// <summary>Parent record type name (e.g., "Weapons", "NPCs") for search grouping.</summary>
    public string? ParentTypeName { get; init; }

    /// <summary>Icon glyph inherited from parent category.</summary>
    public string? ParentIconGlyph { get; init; }

    public string? Detail { get; init; }
    public object? DataObject { get; init; }
    public long? FileOffset { get; init; }
    public ObservableCollection<EsmBrowserNode> Children { get; } = [];
    public bool HasUnrealizedChildren { get; set; }
    public string IconGlyph { get; init; } = "\uE7C3";

    public List<EsmPropertyEntry> Properties { get; init; } = [];
}
