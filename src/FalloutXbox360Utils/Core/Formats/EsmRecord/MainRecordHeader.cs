namespace FalloutXbox360Utils.Core.Formats.EsmRecord;

/// <summary>
///     Main record header (24 bytes for FNV).
/// </summary>
public record MainRecordHeader
{
    public required string Signature { get; init; }
    public uint DataSize { get; init; }
    public uint Flags { get; init; }
    public uint FormId { get; init; }
    public uint VersionControl1 { get; init; }
    public uint VersionControl2 { get; init; }

    public bool IsCompressed => (Flags & 0x00040000) != 0;
    public bool IsDeleted => (Flags & 0x00000020) != 0;
    public bool IsIgnored => (Flags & 0x00001000) != 0;
}
