namespace BethesdaMultitool.Core.Formats.Esm.Parsing;

/// <summary>
///     Main record header (24 bytes for FNV).
///     Layout: Signature(4) + DataSize(4) + Flags(4) + FormId(4) + VersionControlInfo1(4) +
///     FormVersion(2) + VersionControlInfo2(2). The historical property names below are retained for
///     API compatibility.
/// </summary>
/// <remarks>
///     ⚠ The trailing field NAMES are historically transposed relative to the engine's
///     semantics, and the parser/writer both use this mapping (so master bytes round-trip):
///     <see cref="VcsInfo" /> occupies header offset 20, which the FNV runtime reads as the
///     record's FORM VERSION (retail FNV records carry 15 here); <see cref="Version" />
///     occupies offset 22, the second version-control word (retail carries small VC values
///     or 0). Any code SYNTHESIZING a header from scratch must put
///     <c>Tes4HeaderBuilder.RecordVersion</c> into <see cref="VcsInfo" /> — records shipping
///     0 at offset 20 are "form version 0" to the engine and are mishandled by the
///     ESM-flagged load path (v95 uninitialized-forms class). Renaming the fields is a
///     follow-up; every initializer and test fixture would need the swap applied.
/// </remarks>
public record MainRecordHeader
{
    public required string Signature { get; init; }
    public uint DataSize { get; init; }
    public uint Flags { get; init; }
    public uint FormId { get; init; }
    /// <summary>xEdit's four-byte Version Control Info 1 field at offset 16.</summary>
    public uint Timestamp { get; init; }
    public ushort VcsInfo { get; init; }
    public ushort Version { get; init; }

    /// <summary>
    ///     Semantic form version from header offset 20. Null for formats without the version trailer;
    ///     unlike <see cref="VcsInfo" />, this distinguishes an absent field from a present value of zero.
    /// </summary>
    public ushort? FormVersion { get; init; }

    public bool IsCompressed => (Flags & 0x00040000) != 0;
    public bool IsDeleted => (Flags & 0x00000020) != 0;
    public bool IsIgnored => (Flags & 0x00001000) != 0;
}
