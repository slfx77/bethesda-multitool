namespace FalloutXbox360Utils.Core.Formats.SaveGame;

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
