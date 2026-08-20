using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;

namespace BethesdaMultitool.Core.Formats.Esm.Merge;

/// <summary>
///     Bundle of deletion-flag override records, partitioned by which sub-GRUP they belong in.
/// </summary>
public sealed record DeletedRefBundle
{
    public required List<byte[]> Persistent { get; init; }
    public required List<byte[]> Temporary { get; init; }
}

/// <summary>
///     For cells in <see cref="CellMergeMode.LoadedReplacement" /> mode, computes the set
///     difference between the master ESM's refs in a cell and the DMP's refs in the same cell,
///     and removes each ref that's in the master but not in the DMP and isn't kept by the
///     preservation filter — via the community-standard "UNDELETE AND DISABLE" pattern: a
///     full master-clone override with the Initially Disabled record flag (0x800) instead of
///     a deletion-flag (0x20) stub. Deleted references are FNV's most notorious crash source
///     (any surviving master content that still references a deleted form faults); a disabled
///     override achieves the same in-game removal (invisible, no collision) while every
///     FormID keeps resolving. Gomorrah01 concentrated all 790 of the plugin's deletion
///     stubs and was the one interior that crashed on attach — the bisect that motivated
///     this switch.
/// </summary>
public static class DeletedRefSynthesizer
{
    private const uint InitiallyDisabledFlag = 0x00000800;
    private const uint CompressedFlag = 0x00040000;
    private const uint PersistentFlag = 0x00000400;

    private const uint DeletedRecordFlag = 0x00000020;

    private static readonly IReadOnlySet<string> DisabledOverrideStripSubrecords =
        new HashSet<string>(StringComparer.Ordinal) { "XEMI" };

    /// <summary>
    ///     Build a <see cref="DeletedRefBundle" /> for the given cell.
    /// </summary>
    /// <param name="masterRefsInCell">All master ESM REFR/ACHR/ACRE records belonging to this cell.</param>
    /// <param name="dmpFormIdsInCell">Set of FormIDs the DMP has for refs in this cell.</param>
    /// <param name="preserveMissingRef">Optional predicate for missing master refs that must not be deleted.</param>
    /// <param name="useHardDeletion">
    ///     Optional predicate selecting refs that get a true deleted-flag stub instead of the
    ///     disabled-override default. Render-culling markers (room bounds / portals /
    ///     occlusion planes) REQUIRE hard deletion: the engine's room-portal culling graph
    ///     honors initially-disabled markers (in-game verified — disabled markers re-broke
    ///     the Gomorrah occlusion the v89 tombstones had fixed), and nothing references
    ///     culling markers, so deleting them is safe from the referenced-form crash class.
    /// </param>
    public static DeletedRefBundle Synthesize(
        IEnumerable<ParsedMainRecord> masterRefsInCell,
        ISet<uint> dmpFormIdsInCell,
        Func<ParsedMainRecord, bool>? preserveMissingRef = null,
        Func<ParsedMainRecord, bool>? useHardDeletion = null)
    {
        var persistent = new List<byte[]>();
        var temporary = new List<byte[]>();

        foreach (var masterRef in masterRefsInCell)
        {
            if (dmpFormIdsInCell.Contains(masterRef.Header.FormId))
            {
                continue;
            }

            if (preserveMissingRef?.Invoke(masterRef) == true)
            {
                continue;
            }

            var bytes = useHardDeletion?.Invoke(masterRef) == true
                ? BuildHardDeletedStub(masterRef)
                : BuildDeletedOverride(masterRef);
            if ((masterRef.Header.Flags & PersistentFlag) != 0)
            {
                persistent.Add(bytes);
            }
            else
            {
                temporary.Add(bytes);
            }
        }

        return new DeletedRefBundle
        {
            Persistent = persistent,
            Temporary = temporary
        };
    }

    /// <summary>
    ///     True deleted-flag stub (header flag 0x20, minimal EDID-only payload) — reserved
    ///     for render-culling markers, which must not exist AT ALL for the engine to skip
    ///     them when building the room-portal culling graph.
    /// </summary>
    private static byte[] BuildHardDeletedStub(ParsedMainRecord masterRef)
    {
        using var subStream = new MemoryStream();
        var edid = masterRef.Subrecords.FirstOrDefault(s => s.Signature == "EDID");
        if (edid is not null)
        {
            using var subWriter = new BinaryWriter(subStream, Encoding.Latin1, true);
            SubrecordEncoder.WriteSubrecord(subWriter, "EDID", edid.Data);
        }

        var subBytes = subStream.ToArray();
        var header = masterRef.Header with
        {
            DataSize = (uint)subBytes.Length,
            Flags = (masterRef.Header.Flags & ~CompressedFlag) | DeletedRecordFlag
        };

        using var stream = new MemoryStream();
        RecordHeaderProcessor.WriteRecordHeader(stream, header);
        stream.Write(subBytes);
        return stream.ToArray();
    }

    /// <summary>
    ///     Build a single "undeleted + initially disabled" override from its master source:
    ///     the full master record clone (XEMI stripped — the emittance link is eager-resolved
    ///     under ESM load and was an AV source) with the Initially Disabled flag added. The
    ///     ref exists and resolves at runtime but never renders, collides, or processes.
    /// </summary>
    private static byte[] BuildDeletedOverride(ParsedMainRecord masterRef)
    {
        var bytes = CellGrupBuilder.ReconstructRecordBytes(
            masterRef, DisabledOverrideStripSubrecords);

        // ReconstructRecordBytes already cleared the compressed flag; add Initially Disabled.
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(8, 4), (flags & ~CompressedFlag) | InitiallyDisabledFlag);
        return bytes;
    }
}
