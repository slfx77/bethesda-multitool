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

    // Additional columns for inventory/faction sub-items (4-column layout)
    public string? Col1 { get; init; } // Editor ID
    public string? Col2 { get; init; } // Full Name
    public string? Col3 { get; init; } // Form ID
    public string? Col4 { get; init; } // Quantity/Rank

    /// <summary>Raw FormID for the main Value field (top-level FormID references).</summary>
    public uint? LinkedFormId { get; init; }

    /// <summary>Raw FormID for Col3 (sub-item FormID column).</summary>
    public uint? Col3FormId { get; init; }

    /// <summary>Raw FormID for Col4 (sub-item FormID column).</summary>
    public uint? Col4FormId { get; init; }
}
