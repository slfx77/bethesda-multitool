using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.CLI.Formatters;

/// <summary>
///     Data types used by the semantic diff command. <see cref="SemdiffResult" /> is the complete
///     comparison outcome (records, verdicts, header deltas, warnings) and carries no display state,
///     so the table renderer (<see cref="SemdiffFieldFormatter" />) and the JSON writer
///     (<see cref="SemdiffJsonWriter" />) both consume it.
/// </summary>
internal static class SemdiffTypes
{
    /// <summary>
    ///     How a record's header differs between the two files.
    ///     <list type="bullet">
    ///         <item><see cref="HeaderDeltaClass.Semantic" />: a record-header flag bit other than 18 (Persistent, Initially Disabled, ...).</item>
    ///         <item><see cref="HeaderDeltaClass.Storage" />: bit 18 (Compressed) — how the payload is stored, not what it says.</item>
    ///         <item>
    ///             <see cref="HeaderDeltaClass.Format" />: the form version (u16 at header offset 20). Not counted on
    ///             its own: every Xbox 360 build stamps form version 15 on every record while PC keeps older values
    ///             on records that were never re-saved (4,288 records between the X360 final master and PC 1.0), so a
    ///             pair that differs only here is <see cref="DiffType.FormVersionOnly" />, listed under <c>--all</c>.
    ///         </item>
    ///         <item><see cref="HeaderDeltaClass.Bookkeeping" />: the version-control words (header offsets 16 and 22); never counted.</item>
    ///     </list>
    /// </summary>
    internal enum HeaderDeltaClass
    {
        Semantic,
        Storage,
        Format,
        Bookkeeping
    }

    /// <summary>How the command pairs records of file A with records of file B.</summary>
    internal enum MatchMode
    {
        /// <summary>By numeric FormID; repeated FormIDs are paired by occurrence in file order.</summary>
        FormId,

        /// <summary>By (signature, EditorID), falling back to (signature, FormID) for records without one.</summary>
        EditorId,

        /// <summary>Only the FormID pairs given with <c>--map A=B</c>.</summary>
        ExplicitMap
    }

    /// <summary>Which rule paired (or failed to pair) a record.</summary>
    internal enum MatchedBy
    {
        FormId,
        EditorId,
        FormIdFallback,
        ExplicitMap
    }

    internal enum SemdiffSide
    {
        A,
        B
    }

    internal enum DiffType
    {
        OnlyInA,
        OnlyInB,

        /// <summary>
        ///     Subrecords differ, the same subrecords appear in a different order
        ///     (<see cref="RecordDiff.SubrecordOrderNote" />), or a header flag bit differs (including the
        ///     compressed bit).
        /// </summary>
        Different,

        /// <summary>
        ///     The header fields and the ordered subrecord sequence are equal: byte for byte when both files
        ///     share a byte order, else after the big-endian side is swapped to PC order by the converter's
        ///     own schema (<see cref="SemdiffRecordParser.NormalizeBigEndianSubrecord" />).
        /// </summary>
        Identical,

        /// <summary>Only the version-control bookkeeping words differ. Listed under <c>--all</c> only.</summary>
        NonSemanticHeaderOnly,

        /// <summary>The paired records have different signatures; typed field comparison is refused.</summary>
        SignatureMismatch,

        /// <summary>An EditorID key occurs more than once in a file, so no pair is formed.</summary>
        Ambiguous,

        /// <summary>
        ///     Subrecords, their order and the header flags are equal; the form version differs (the
        ///     version-control words may differ too). Listed under <c>--all</c> only and not counted as a
        ///     difference.
        /// </summary>
        FormVersionOnly
    }

    internal sealed record ParsedRecord(
        string Type,
        uint FormId,
        uint Flags,
        int Offset,
        List<ParsedSubrecord> Subrecords)
    {
        /// <summary>The header's declared payload size (compressed size for a compressed record).</summary>
        public uint DataSize { get; init; }

        /// <summary>Version Control Info 1, the u32 at header offset 16.</summary>
        public uint VersionControl1 { get; init; }

        /// <summary>Form version, the u16 at header offset 20.</summary>
        public ushort FormVersion { get; init; }

        /// <summary>Version Control Info 2, the u16 at header offset 22.</summary>
        public ushort VersionControl2 { get; init; }

        public int HeaderSize { get; init; } = 24;

        /// <summary>
        ///     The first EDID subrecord read to its first NUL, or null when the record has none (or an
        ///     empty one).
        /// </summary>
        public string? EditorId => SemdiffRecordParser.ReadEditorId(Subrecords);
    }

    internal sealed record ParsedSubrecord(string Signature, byte[] Data, int Offset);

    /// <summary>One header flag bit that is set on one side only.</summary>
    internal sealed record FlagBitDelta(int Bit, uint Mask, string? Name, HeaderDeltaClass Class);

    /// <summary>A non-flag header field whose value differs; values are display strings.</summary>
    internal sealed record HeaderFieldDelta(string Field, string ValueA, string ValueB, HeaderDeltaClass Class);

    /// <summary>
    ///     The header comparison of a same-signature pair. <see cref="Added" /> holds bits set in B
    ///     only, <see cref="Removed" /> bits set in A only, each lowest bit first.
    /// </summary>
    internal sealed record HeaderComparison(
        uint FlagsA,
        uint FlagsB,
        IReadOnlyList<FlagBitDelta> Added,
        IReadOnlyList<FlagBitDelta> Removed,
        IReadOnlyList<HeaderFieldDelta> Fields)
    {
        /// <summary>
        ///     Any flag bit changed (Semantic or Storage). The form version (<see cref="HeaderDeltaClass.Format" />)
        ///     and the version-control words (<see cref="HeaderDeltaClass.Bookkeeping" />) are not counted.
        /// </summary>
        public bool HasCountedDelta =>
            Added.Count > 0 || Removed.Count > 0 ||
            Fields.Any(f => f.Class is not (HeaderDeltaClass.Format or HeaderDeltaClass.Bookkeeping));

        public bool HasFormVersionDelta => Fields.Any(f => f.Class == HeaderDeltaClass.Format);

        public bool HasBookkeepingDelta => Fields.Any(f => f.Class == HeaderDeltaClass.Bookkeeping);
    }

    /// <summary>
    ///     Where two records' ordered subrecord sequences first disagree although every signature holds the
    ///     same subrecords in the same per-signature order. A signature is null when that side has no
    ///     subrecord at <see cref="Index" />.
    /// </summary>
    internal sealed record SubrecordOrderDivergence(int Index, string? SignatureA, string? SignatureB);

    /// <summary>
    ///     The subrecord comparison of a same-signature pair: the per-signature differences and, only when
    ///     there are none, where the ordered sequences first diverge (null when they do not).
    /// </summary>
    internal sealed record SubrecordComparison(List<FieldDiff> FieldDiffs, SubrecordOrderDivergence? OrderDivergence);

    /// <summary>One signature's share of a record's subrecords: how many, and each one's size.</summary>
    internal sealed record SubrecordInventoryEntry(string Signature, int Count, IReadOnlyList<int> Sizes);

    /// <summary>One <c>--map A=B</c> pair.</summary>
    internal readonly record struct FormIdMapping(uint FormIdA, uint FormIdB);

    internal sealed record RecordDiff(
        uint FormId,
        string RecordType,
        DiffType DiffType,
        ParsedRecord? RecordA,
        ParsedRecord? RecordB,
        List<FieldDiff>? FieldDiffs = null)
    {
        /// <summary>File B's FormID; differs from <see cref="FormId" /> only when EditorID or --map paired the records.</summary>
        public uint FormIdB { get; init; } = FormId;

        public MatchedBy MatchedBy { get; init; } = MatchedBy.FormId;

        /// <summary>Null for a pair the comparer never compares (one side missing, or a signature mismatch).</summary>
        public HeaderComparison? Header { get; init; }

        public string? EditorIdA { get; init; }
        public string? EditorIdB { get; init; }

        /// <summary>0-based occurrence of this record among the records sharing its key in file A.</summary>
        public int OccurrenceA { get; init; }

        /// <summary>0-based occurrence of this record among the records sharing its key in file B.</summary>
        public int OccurrenceB { get; init; }

        /// <summary>Record A's set header flags named with A's signature (empty when A is absent).</summary>
        public IReadOnlyList<string> FlagNamesA { get; init; } = [];

        /// <summary>Record B's set header flags named with B's signature (empty when B is absent).</summary>
        public IReadOnlyList<string> FlagNamesB { get; init; } = [];

        /// <summary>Per-signature subrecord inventory of A; set on a signature mismatch.</summary>
        public IReadOnlyList<SubrecordInventoryEntry>? InventoryA { get; init; }

        /// <summary>Per-signature subrecord inventory of B; set on a signature mismatch.</summary>
        public IReadOnlyList<SubrecordInventoryEntry>? InventoryB { get; init; }

        /// <summary>Where a record's EditorID exists in the other file, when the command looked it up.</summary>
        public string? EditorIdHint { get; init; }

        /// <summary>
        ///     Set when every signature holds the same subrecords but the ordered sequence differs (a
        ///     subrecord moved, e.g. a condition from one quest stage to another): names the first diverging
        ///     index. The verdict is then <see cref="DiffType.Different" />.
        /// </summary>
        public string? SubrecordOrderNote { get; init; }

        /// <summary>For <see cref="DiffType.Ambiguous" />: every file-A FormID that carries the key.</summary>
        public IReadOnlyList<uint> AmbiguousFormIdsA { get; init; } = [];

        /// <summary>For <see cref="DiffType.Ambiguous" />: every file-B FormID that carries the key.</summary>
        public IReadOnlyList<uint> AmbiguousFormIdsB { get; init; } = [];
    }

    /// <summary>
    ///     One subrecord difference. <see cref="DataA" /> and <see cref="DataB" /> are the raw payloads, each in
    ///     its own file's byte order (<see cref="BigEndianA" />, <see cref="BigEndianB" />).
    /// </summary>
    internal sealed record FieldDiff(
        string Signature,
        byte[]? DataA,
        byte[]? DataB,
        string? Message,
        bool BigEndianA,
        bool BigEndianB,
        string RecordType)
    {
        /// <summary>
        ///     The files' byte orders differ and no conversion schema says how to swap this subrecord, so
        ///     whether the two payloads hold the same values is unknown. Never treated as equal.
        /// </summary>
        public bool ByteOrderUnresolved { get; init; }

        /// <summary>
        ///     For a subrecord present on both sides whose compared payloads differ: the first byte offset at
        ///     which they do, counted in the bytes that were compared (PC byte order when the files' byte
        ///     orders differ, else the files' own). Null for a one-sided or unresolved subrecord.
        /// </summary>
        public int? FirstDifferingOffset { get; init; }
    }

    /// <summary>
    ///     One display row of a subrecord difference. A null value means the side has no such
    ///     field; <see cref="Message" /> is set for a subrecord present on one side only.
    /// </summary>
    internal sealed record FieldRow(
        string Signature,
        string Field,
        string? ValueA,
        string? ValueB,
        bool Equal,
        string? Message);

    internal sealed record SemdiffCompareOptions
    {
        public MatchMode Match { get; init; } = MatchMode.FormId;

        /// <summary>The <c>--map</c> pairs; used only when <see cref="Match" /> is <see cref="MatchMode.ExplicitMap" />.</summary>
        public IReadOnlyList<FormIdMapping> ExplicitPairs { get; init; } = [];

        /// <summary>List every compared record, not only the ones that differ.</summary>
        public bool ShowAll { get; init; }

        public BethesdaGame GameA { get; init; } = BethesdaGame.Unknown;
        public BethesdaGame GameB { get; init; } = BethesdaGame.Unknown;
        public bool BigEndianA { get; init; }
        public bool BigEndianB { get; init; }
        public string LabelA { get; init; } = "File A";
        public string LabelB { get; init; } = "File B";
    }

    /// <summary>
    ///     Verdict counts over every record the comparison saw. <see cref="WithDifferences" /> counts
    ///     only the verdicts the default listing shows (Different, SignatureMismatch, OnlyInA, OnlyInB,
    ///     Ambiguous); <see cref="Listed" /> is the number of records in <see cref="SemdiffResult.Records" />.
    /// </summary>
    internal sealed record SemdiffSummary
    {
        /// <summary>
        ///     Pairs formed: Different + SignatureMismatch + FormVersionOnly + NonSemanticHeaderOnly + Identical.
        /// </summary>
        public int Compared { get; init; }

        public int WithDifferences { get; init; }
        public int Different { get; init; }
        public int SignatureMismatch { get; init; }
        public int OnlyInA { get; init; }
        public int OnlyInB { get; init; }
        public int Ambiguous { get; init; }
        public int FormVersionOnly { get; init; }
        public int NonSemanticHeaderOnly { get; init; }
        public int Identical { get; init; }
        public int Listed { get; init; }
    }

    /// <summary>
    ///     A condition the reader must know about to trust the result. Codes: <c>duplicate-formid</c>,
    ///     <c>editorid-differs</c>, <c>map-formid-not-found</c>, <c>compressed-skipped</c>,
    ///     <c>game-mismatch</c>, <c>master-list-mismatch</c>.
    /// </summary>
    internal sealed record SemdiffWarning(string Code, SemdiffSide? Side, uint? FormId, string Message);

    /// <summary>
    ///     A warning as the structured output and its stderr lines report it. Every warning is one entry
    ///     with <see cref="Count" /> 1 and no <see cref="Examples" />, except <c>duplicate-formid</c>, which is
    ///     collapsed to one entry per file (an Xbox 360 master repeats about 14,000 INFO FormIDs):
    ///     <see cref="Count" /> is then the number of repeated FormIDs, <see cref="FormId" /> is set only when
    ///     that number is 1, and <see cref="Examples" /> holds the first few of the underlying warnings.
    /// </summary>
    internal sealed record SemdiffWarningEntry(
        string Code,
        SemdiffSide? Side,
        uint? FormId,
        int Count,
        string Message,
        IReadOnlyList<SemdiffWarning> Examples);

    /// <summary>One compared file as the structured output describes it.</summary>
    /// <param name="Label">The display label (<c>File A</c>, or the caller's label).</param>
    /// <param name="Path">The full path of the file.</param>
    /// <param name="SizeBytes">The file's length in bytes.</param>
    /// <param name="BigEndian">True for an Xbox 360 (big-endian) plugin.</param>
    /// <param name="Game">The detected game; header flag bits are named with it.</param>
    /// <param name="Masters">The TES4 master list, in file order.</param>
    internal sealed record SemdiffFileInfo(
        string Label,
        string Path,
        long SizeBytes,
        bool BigEndian,
        BethesdaGame Game,
        IReadOnlyList<string> Masters)
    {
        public string FileName => global::System.IO.Path.GetFileName(Path);
    }

    /// <summary>
    ///     The validated options of one run, as the structured output echoes them. <see cref="Limit" />
    ///     caps the records written (the summary counts every record); <see cref="Maps" /> is empty
    ///     unless <see cref="Match" /> is <see cref="MatchMode.ExplicitMap" />.
    /// </summary>
    internal sealed record SemdiffQuery
    {
        public uint? FormId { get; init; }
        public string? RecordType { get; init; }
        public MatchMode Match { get; init; } = MatchMode.FormId;
        public IReadOnlyList<FormIdMapping> Maps { get; init; } = [];
        public int Limit { get; init; } = 10;
        public bool ShowAll { get; init; }
    }

    internal sealed record SemdiffResult
    {
        /// <summary>The listed records, ordered by FormID then occurrence. Not truncated by <c>--limit</c>.</summary>
        public List<RecordDiff> Records { get; init; } = [];

        public SemdiffSummary Summary { get; init; } = new();
        public List<SemdiffWarning> Warnings { get; init; } = [];
    }
}
