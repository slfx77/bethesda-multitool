namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Main record header (24 bytes for FNV).
///     Layout: Signature(4) + DataSize(4) + Flags(4) + FormId(4) + Timestamp(4) + VcsInfo(2) + Version(2)
/// </summary>
public record MainRecordHeader
{
    public required string Signature { get; init; }
    public uint DataSize { get; init; }
    public uint Flags { get; init; }
    public uint FormId { get; init; }
    public uint Timestamp { get; init; }
    public ushort VcsInfo { get; init; }
    public ushort Version { get; init; }

    public bool IsCompressed => (Flags & 0x00040000) != 0;
    public bool IsDeleted => (Flags & 0x00000020) != 0;
    public bool IsIgnored => (Flags & 0x00001000) != 0;
}
