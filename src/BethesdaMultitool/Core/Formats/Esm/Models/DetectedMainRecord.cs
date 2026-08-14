namespace BethesdaMultitool.Core.Formats.Esm.Models;

/// <summary>
///     Main record header detected in memory dump.
///     Modern structure: [TYPE:4][SIZE:4][FLAGS:4][FORMID:4][VERSION CONTROL INFO 1:4]
///     [FORM VERSION:2][VERSION CONTROL INFO 2:2] = 24 bytes. Oblivion stops after version-control
///     info 1 (20 bytes).
/// </summary>
public record DetectedMainRecord(
    string RecordType,
    uint DataSize,
    uint Flags,
    uint FormId,
    long Offset,
    bool IsBigEndian)
{
    /// <summary>
    ///     Size in bytes of this record's header (where its subrecord data begins, relative to
    ///     <see cref="Offset" />). 24 for FO3/FNV/Skyrim/FO4/Starfield and DMP-derived records;
    ///     20 for Oblivion. Defaults to 24 so existing call sites are unchanged.
    /// </summary>
    public int HeaderSize { get; init; } = 24;

    /// <summary>
    ///     Semantic record form version from header offset 20, endian-corrected. Null means the record
    ///     has no version trailer (Oblivion/TES3) or was synthesized without an on-disk header; zero is
    ///     a real modern-header value and must not be treated as unknown.
    /// </summary>
    public ushort? FormVersion { get; init; }

    /// <summary>Whether this is a compressed record.</summary>
    public bool IsCompressed => (Flags & 0x00040000) != 0;

    /// <summary>Whether this is a deleted record.</summary>
    public bool IsDeleted => (Flags & 0x00000020) != 0;

    /// <summary>Whether this is a persistent reference (0x0400 flag).</summary>
    public bool IsPersistent => (Flags & 0x00000400) != 0;

    /// <summary>Whether this record has the Initially Disabled flag (0x0800).</summary>
    public bool IsInitiallyDisabled => (Flags & 0x00000800) != 0;

    /// <summary>Plugin index from FormID (upper 8 bits).</summary>
    public byte PluginIndex => (byte)(FormId >> 24);

    /// <summary>Local FormID (lower 24 bits).</summary>
    public uint LocalFormId => FormId & 0x00FFFFFF;
}
