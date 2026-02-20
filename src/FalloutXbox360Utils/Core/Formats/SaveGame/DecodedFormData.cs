namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     Represents a single decoded field from a changed form's Data[] bytes.
/// </summary>
public sealed class DecodedField
{
    /// <summary>Flag name or field label (e.g., "FORM_FLAGS", "Position X").</summary>
    public required string Name { get; init; }

    /// <summary>Typed value: uint, int, float, string, SaveRefId, byte[], List&lt;T&gt;, etc.</summary>
    public object? Value { get; init; }

    /// <summary>Human-readable display string.</summary>
    public required string DisplayValue { get; init; }

    /// <summary>Offset within the Data[] where this field was read.</summary>
    public int DataOffset { get; init; }

    /// <summary>Number of bytes consumed by this field.</summary>
    public int DataLength { get; init; }

    /// <summary>Optional sub-fields for structured data (e.g., inventory items, quest stages).</summary>
    public List<DecodedField>? Children { get; init; }

    public override string ToString() => $"{Name} = {DisplayValue}";
}

/// <summary>
///     The result of decoding a ChangedForm's Data[] bytes.
///     Contains decoded fields and tracks how much data was consumed.
/// </summary>
public sealed class DecodedFormData
{
    /// <summary>Ordered list of decoded fields.</summary>
    public List<DecodedField> Fields { get; } = [];

    /// <summary>Total bytes consumed from Data[] during decoding.</summary>
    public int BytesConsumed { get; set; }

    /// <summary>Total bytes in the original Data[].</summary>
    public int TotalBytes { get; set; }

    /// <summary>Number of bytes that could not be decoded (TotalBytes - BytesConsumed).</summary>
    public int UndecodedBytes => TotalBytes - BytesConsumed;

    /// <summary>True if all data bytes were consumed by decoding.</summary>
    public bool FullyDecoded => BytesConsumed >= TotalBytes;

    /// <summary>Warnings or diagnostic messages generated during decoding.</summary>
    public List<string> Warnings { get; } = [];
}
