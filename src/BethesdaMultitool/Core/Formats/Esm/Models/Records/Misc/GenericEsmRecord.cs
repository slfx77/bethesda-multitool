using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;

namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

/// <summary>
///     Schema/identity carrier for records represented as
///     EDID + FULL + MODL + OBND + type-specific subrecords. The hand-written FNV parser
///     uses it for MSTT, TACT, CAMS, ANIO, IPDS, EFSH, RGDL, LSCR, ASPC, MSET, CHIP,
///     CSNO, DOBJ, ADDN, IDLM, IMGS, GRAS, AMEF, and FLOR; schema-primary parsers also use
///     it as their general decoded-record carrier. PWAT and TREE use dedicated typed models
///     because their runtime data contains embedded pointers/arrays the generic reader does
///     not reconstruct.
/// </summary>
public record GenericEsmRecord
{
    private readonly IReadOnlyList<DecodedNode>? _decodedTree;

    /// <summary>FormID of the record.</summary>
    public uint FormId { get; init; }

    /// <summary>4-character ESM record type signature (e.g., "MSTT", "CAMS").</summary>
    public string RecordType { get; init; } = "";

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name (FULL subrecord).</summary>
    public string? FullName { get; init; }

    /// <summary>Model file path (MODL subrecord).</summary>
    public string? ModelPath { get; init; }

    /// <summary>Object bounds (OBND subrecord).</summary>
    public ObjectBounds? Bounds { get; init; }

    /// <summary>
    ///     Type-specific subrecord fields parsed via SubrecordDataReader schemas
    ///     or stored as raw byte arrays. Keys are subrecord signatures.
    ///     <para>
    ///         Defaults to a shared empty instance rather than a fresh dictionary: the schema-driven
    ///         parser populates <see cref="DecodedTree" /> instead and never touches this, so on a
    ///         schema-primary master every record was carrying its own empty dictionary — 483,277 of
    ///         them on Fallout 76, none ever read.
    ///     </para>
    /// </summary>
    public IReadOnlyDictionary<string, object?> Fields { get; init; } =
        System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>.Empty;

    /// <summary>
    ///     The schema-decoded, ordered, labeled field tree, when this record was read by the schema-driven
    ///     reader (<see cref="Parsing.SchemaDrivenRecordParser" />). Null for records read by the
    ///     hand-written FNV handlers, which populate <see cref="Fields" /> instead.
    ///     <para>
    ///         Decoded on demand when <see cref="TreeSource" /> is attached — see that member. Reading
    ///         this in a loop over every record will decode every record.
    ///     </para>
    /// </summary>
    public IReadOnlyList<DecodedNode>? DecodedTree
    {
        get => _decodedTree ?? TreeSource?.GetTree(Descriptor);
        init => _decodedTree = value;
    }

    /// <summary>
    ///     Lazy decoder for <see cref="DecodedTree" />, attached by the schema-driven parser instead of
    ///     materializing a tree per record: on Fallout 76 the eager trees measured 1,873 MB, 27% of the
    ///     post-load managed heap, for data only the browser and the presentation profiles read.
    ///     <para>
    ///         Deliberately a pair of plain references rather than a <c>Func&lt;&gt;</c>. A closure per
    ///         record would allocate a display class and a delegate for each of ~484k records (~46 MB);
    ///         two reference fields cost 16 bytes each and point at objects that already exist — the
    ///         one shared source, and the descriptor the scan result already holds.
    ///     </para>
    /// </summary>
    internal Parsing.DecodedTreeSource? TreeSource { get; init; }

    /// <summary>
    ///     This record's on-disk header descriptor, carried so <see cref="TreeSource" /> can re-read and
    ///     re-decode it exactly: offset, header size, data size, compression flag, endianness and form
    ///     version. Form version is not optional — <c>SchemaRecordDecoder</c> selects between union arms
    ///     on it, so a re-decode without it would silently choose the wrong layout.
    /// </summary>
    internal DetectedMainRecord? Descriptor { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
