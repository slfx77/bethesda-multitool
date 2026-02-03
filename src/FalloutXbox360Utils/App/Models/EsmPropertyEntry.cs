namespace FalloutXbox360Utils;

/// <summary>
///     A name/value property entry displayed in the data browser detail panel.
///     Supports expandable list properties with sub-items.
/// </summary>
public sealed class EsmPropertyEntry
{
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public string? Category { get; init; }

    /// <summary>Whether this entry is expandable (contains sub-items).</summary>
    public bool IsExpandable { get; init; }

    /// <summary>Sub-items for expandable entries (list contents).</summary>
    public List<EsmPropertyEntry>? SubItems { get; init; }
}
