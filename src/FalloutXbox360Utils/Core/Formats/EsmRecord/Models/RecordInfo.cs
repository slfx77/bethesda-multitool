namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Basic record info from scanning.
/// </summary>
public record RecordInfo
{
    public required string Signature { get; init; }
    public uint FormId { get; init; }
    public long Offset { get; init; }
    public uint DataSize { get; init; }
}
