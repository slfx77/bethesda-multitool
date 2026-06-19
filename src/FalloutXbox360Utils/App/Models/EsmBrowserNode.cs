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

    private List<EsmPropertyEntry>? _properties;

    /// <summary>
    ///     Optional deferred builder for <see cref="Properties" />. Record nodes set THIS instead of
    ///     eagerly assigning <see cref="Properties" />: expanding one large record-type node would
    ///     otherwise materialize ~80 <see cref="EsmPropertyEntry" /> objects PER sibling record up
    ///     front, even though only the SELECTED node's properties are ever displayed. (A 922 MB memory
    ///     dump expanded ~14M EsmPropertyEntry / ~1.9 GB this way \u2014 dump-confirmed.) The factory runs
    ///     once on first <see cref="Properties" /> read (UI-thread selection) and the result is cached.
    /// </summary>
    public Func<List<EsmPropertyEntry>>? PropertyFactory { get; init; }

    /// <summary>
    ///     Detail-panel properties. When <see cref="PropertyFactory" /> is set, built on demand and
    ///     cached on first read; otherwise holds the eagerly-assigned list (small/bounded node kinds).
    /// </summary>
    public List<EsmPropertyEntry> Properties
    {
        get => _properties ??= PropertyFactory?.Invoke() ?? [];
        init => _properties = value;
    }
}
