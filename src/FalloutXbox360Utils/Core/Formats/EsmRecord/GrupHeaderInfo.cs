namespace FalloutXbox360Utils.Core.Formats.EsmRecord;

/// <summary>
///     Represents a parsed GRUP header with its file offset.
///     Used for tracking GRUP locations in memory map visualization.
/// </summary>
public record GrupHeaderInfo
{
    /// <summary>GRUP header is always 24 bytes.</summary>
    public const int HeaderSize = 24;

    /// <summary>File offset where this GRUP header starts.</summary>
    public long Offset { get; init; }

    /// <summary>Total size of the group including header (24 bytes) and all contents.</summary>
    public uint GroupSize { get; init; }

    /// <summary>Group label (4 bytes) - interpretation depends on GroupType.</summary>
    public byte[] Label { get; init; } = [];

    /// <summary>Group type (0-10).</summary>
    public int GroupType { get; init; }

    /// <summary>Timestamp from file.</summary>
    public uint Stamp { get; init; }

    /// <summary>Label interpreted as signature string (for type 0 groups).</summary>
    public string LabelAsSignature => EsmRecordTypes.SignatureToString(Label);
}
