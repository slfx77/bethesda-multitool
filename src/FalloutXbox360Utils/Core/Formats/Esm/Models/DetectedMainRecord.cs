namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Main record header detected in memory dump.
///     Structure: [TYPE:4][SIZE:4][FLAGS:4][FORMID:4][VCS1:4][VCS2:4] = 24 bytes
/// </summary>
public record DetectedMainRecord(
    string RecordType,
    uint DataSize,
    uint Flags,
    uint FormId,
    long Offset,
    bool IsBigEndian)
{
    /// <summary>Whether this is a compressed record.</summary>
    public bool IsCompressed => (Flags & 0x00040000) != 0;

    /// <summary>Whether this is a deleted record.</summary>
    public bool IsDeleted => (Flags & 0x00000020) != 0;

    /// <summary>Whether this record has the Initially Disabled flag (0x0800).</summary>
    public bool IsInitiallyDisabled => (Flags & 0x00000800) != 0;

    /// <summary>Plugin index from FormID (upper 8 bits).</summary>
    public byte PluginIndex => (byte)(FormId >> 24);

    /// <summary>Local FormID (lower 24 bits).</summary>
    public uint LocalFormId => FormId & 0x00FFFFFF;
}
