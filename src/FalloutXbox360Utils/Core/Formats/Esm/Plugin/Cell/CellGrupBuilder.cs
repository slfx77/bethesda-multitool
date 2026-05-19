using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Cell;

/// <summary>
///     A single cell's worth of override emission state — the master CELL bytes that anchor
///     the children, the master GRUP context for proper nesting, and the lists of persistent
///     and temporary placed-ref overrides that go inside the child GRUP.
/// </summary>
public sealed record CellOverrideBundle
{
    /// <summary>FormID of the cell being overridden — used as the child GRUP label.</summary>
    public required uint CellFormId { get; init; }

    /// <summary>
    ///     Master ESM nesting context for this cell — drives interior vs exterior placement
    ///     and reproduces the master's exact block/subblock labels.
    /// </summary>
    public required PcEsmCellContext Context { get; init; }

    /// <summary>The raw CELL record bytes (header + subrecords) to emit as an Identical-To-Master anchor.</summary>
    public required byte[] CellRecordBytes { get; init; }

    /// <summary>Override records to emit in the persistent children GRUP (type 8).</summary>
    public required IReadOnlyList<byte[]> PersistentChildRecords { get; init; }

    /// <summary>Override records to emit in the visible-when-distant children GRUP (type 10).</summary>
    public IReadOnlyList<byte[]> VwdChildRecords { get; init; } = [];

    /// <summary>Override records to emit in the temporary children GRUP (type 9).</summary>
    public required IReadOnlyList<byte[]> TemporaryChildRecords { get; init; }
}

/// <summary>
///     Builds the GRUP nesting hierarchy for cell-children overrides — proper interior and
///     exterior layouts that reproduce the master's block/subblock labels.
/// </summary>
public static class CellGrupBuilder
{
    /// <summary>
    ///     Build the full cell section of the plugin body — top-level CELL GRUP for interior
    ///     bundles plus a single top-level WRLD GRUP wrapping every affected worldspace.
    ///     Returns null when there are no bundles to emit.
    /// </summary>
    /// <param name="bundles">
    ///     Bundles in any order; this method groups them by interior vs
    ///     exterior worldspace.
    /// </param>
    /// <param name="pcRecordsByFormId">PC ESM record lookup, used to fetch WRLD anchor bytes.</param>
    /// <param name="newWorldspacesByDmpFormId">
    ///     Optional fallback: when an exterior bundle's
    ///     parent worldspace isn't in master, look here for the pre-encoded new-WRLD record.
    ///     Keys are the ORIGINAL DMP FormID (matches <c>CellOverrideBundle.Context.WorldspaceFormId</c>).
    /// </param>
    public static byte[]? BuildCellSection(
        IReadOnlyList<CellOverrideBundle> bundles,
        IReadOnlyDictionary<uint, ParsedMainRecord> pcRecordsByFormId,
        IReadOnlyDictionary<uint, NewWorldspaceEntry>? newWorldspacesByDmpFormId = null)
    {
        if (bundles.Count == 0)
        {
            return null;
        }

        using var stream = new MemoryStream();

        var interior = bundles.Where(b => b.Context.IsInterior).ToList();
        if (interior.Count > 0)
        {
            stream.Write(BuildInteriorCellGrup(interior));
        }

        var exteriorByWrld = bundles
            .Where(b => !b.Context.IsInterior && b.Context.WorldspaceFormId.HasValue)
            .GroupBy(b => b.Context.WorldspaceFormId!.Value)
            .OrderBy(g => g.Key)
            .ToList();

        if (exteriorByWrld.Count > 0)
        {
            // Single top-level WRLD GRUP wrapping all worldspace anchors + their World
            // Children GRUPs. Emitting a top-level GRUP per worldspace produces an ESP
            // that FNVEdit auto-merges with "duplicated top level group" warnings and
            // that some tools refuse to load.
            var anyEmitted = false;
            var topLabel = "WRLD"u8.ToArray();
            var topPos = WriteGrupHeader(stream, topLabel, 0);

            foreach (var group in exteriorByWrld)
            {
                anyEmitted |= EmitWrldRecordAndChildren(
                    stream, group.Key, group.ToList(), pcRecordsByFormId, newWorldspacesByDmpFormId);
            }

            if (anyEmitted)
            {
                RecordHeaderProcessor.FinalizeGrupSize(stream, topPos);
            }
            else
            {
                // No worldspace resolved — roll back the empty top-level GRUP header.
                stream.SetLength(topPos);
            }
        }

        return stream.Length > 0 ? stream.ToArray() : null;
    }

    /// <summary>
    ///     Emit the top-level CELL GRUP wrapping all interior cell-override bundles, with each
    ///     cell nested under its master's actual block/subblock labels.
    /// </summary>
    public static byte[] BuildInteriorCellGrup(IReadOnlyList<CellOverrideBundle> interiorBundles)
    {
        if (interiorBundles.Count == 0)
        {
            return [];
        }

        using var stream = new MemoryStream();
        var topLabel = "CELL"u8.ToArray();
        var topPos = WriteGrupHeader(stream, topLabel, 0);

        EmitBlocksAndSubblocks(stream, interiorBundles, 2, 3);

        RecordHeaderProcessor.FinalizeGrupSize(stream, topPos);
        return stream.ToArray();
    }

    /// <summary>
    ///     Emit one worldspace's anchor record and its world-children GRUP into
    ///     <paramref name="stream" />. Layout:
    ///     <code>
    ///         WRLD record (master anchor OR pre-encoded new WRLD)
    ///         GRUP type=1 label=wrldFormId       (world children)
    ///           [persistent CELL records — no block/subblock wrapper]
    ///           [exterior block/subblock GRUPs with their CELL records]
    ///     </code>
    ///     The caller is responsible for wrapping all worldspace emissions in a single
    ///     top-level WRLD GRUP (see <see cref="BuildCellSection" />).
    ///     For a new (non-master) WRLD, anchor bytes come from <paramref name="newWorldspacesByDmpFormId" />
    ///     and the World Children GRUP label uses the EMITTED FormID (matches the FormID encoded
    ///     inside the anchor record bytes). Returns false (and writes nothing) if neither source has the WRLD.
    /// </summary>
    private static bool EmitWrldRecordAndChildren(
        Stream stream,
        uint wrldFormId,
        IReadOnlyList<CellOverrideBundle> bundlesInWrld,
        IReadOnlyDictionary<uint, ParsedMainRecord> pcRecordsByFormId,
        IReadOnlyDictionary<uint, NewWorldspaceEntry>? newWorldspacesByDmpFormId)
    {
        byte[] wrldAnchorBytes;
        uint emittedWrldFormId;

        if (pcRecordsByFormId.TryGetValue(wrldFormId, out var wrldRecord)
            && wrldRecord.Header.Signature == "WRLD")
        {
            wrldAnchorBytes = ReconstructRecordBytes(wrldRecord);
            emittedWrldFormId = wrldFormId;
        }
        else if (newWorldspacesByDmpFormId is not null
                 && newWorldspacesByDmpFormId.TryGetValue(wrldFormId, out var newEntry))
        {
            wrldAnchorBytes = newEntry.RecordBytes;
            emittedWrldFormId = newEntry.EmittedFormId;
        }
        else
        {
            return false;
        }

        // WRLD anchor record.
        stream.Write(wrldAnchorBytes);

        // World children GRUP (type 1, label = EMITTED WRLD FormID — matches the anchor bytes).
        var wrldLabel = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(wrldLabel, emittedWrldFormId);
        var childrenPos = WriteGrupHeader(stream, wrldLabel, 1);

        // Persistent CELL containers go directly under world children, no block wrapping.
        foreach (var bundle in bundlesInWrld.Where(b => b.Context.IsPersistentCellContainer))
        {
            WriteCellAndChildren(stream, bundle);
        }

        // Remaining (block-bound) cells get the exterior block/subblock hierarchy.
        var blockBound = bundlesInWrld.Where(b => !b.Context.IsPersistentCellContainer).ToList();
        if (blockBound.Count > 0)
        {
            EmitBlocksAndSubblocks(stream, blockBound, 4, 5);
        }

        RecordHeaderProcessor.FinalizeGrupSize(stream, childrenPos);
        return true;
    }

    /// <summary>
    ///     Group bundles by their block label, then by their subblock label, and emit the
    ///     proper nested GRUPs. Used by both interior (block=2, subblock=3) and exterior
    ///     (block=4, subblock=5) paths.
    /// </summary>
    private static void EmitBlocksAndSubblocks(
        Stream stream,
        IReadOnlyList<CellOverrideBundle> bundles,
        int blockGroupType,
        int subblockGroupType)
    {
        // Group by block label, then by subblock label. Keys are the raw 4-byte arrays
        // converted to uint32 for stable hashing.
        var byBlock = bundles
            .Where(b => b.Context.BlockLabel is { Length: 4 } && b.Context.SubblockLabel is { Length: 4 })
            .GroupBy(b => BinaryPrimitives.ReadUInt32LittleEndian(b.Context.BlockLabel!))
            .OrderBy(g => g.Key);

        foreach (var blockGroup in byBlock)
        {
            var representative = blockGroup.First();
            var blockLabel = representative.Context.BlockLabel!;

            var blockPos = WriteGrupHeader(stream, blockLabel, blockGroupType);

            var bySubblock = blockGroup
                .GroupBy(b => BinaryPrimitives.ReadUInt32LittleEndian(b.Context.SubblockLabel!))
                .OrderBy(g => g.Key);

            foreach (var subblockGroup in bySubblock)
            {
                var subblockRep = subblockGroup.First();
                var subblockLabel = subblockRep.Context.SubblockLabel!;

                var subblockPos = WriteGrupHeader(stream, subblockLabel, subblockGroupType);

                foreach (var bundle in subblockGroup.OrderBy(b => b.CellFormId))
                {
                    WriteCellAndChildren(stream, bundle);
                }

                RecordHeaderProcessor.FinalizeGrupSize(stream, subblockPos);
            }

            RecordHeaderProcessor.FinalizeGrupSize(stream, blockPos);
        }
    }

    /// <summary>
    ///     Emit a single cell's anchor record + child GRUP (containing the persistent and
    ///     temporary children GRUPs and their override records).
    /// </summary>
    private static void WriteCellAndChildren(Stream stream, CellOverrideBundle bundle)
    {
        // 1. Cell record bytes (verbatim from PC ESM — Identical-To-Master).
        stream.Write(bundle.CellRecordBytes);

        // Skip the children GRUP entirely if there's nothing to override.
        if (bundle.PersistentChildRecords.Count == 0
            && bundle.VwdChildRecords.Count == 0
            && bundle.TemporaryChildRecords.Count == 0)
        {
            return;
        }

        // 2. Child GRUP (type 6) labeled with cell FormID.
        var cellLabel = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(cellLabel, bundle.CellFormId);
        var childPos = WriteGrupHeader(stream, cellLabel, 6);

        // Canonical sub-GRUP order per fopdoc: persistent (8) → VWD (10) → temporary (9).
        if (bundle.PersistentChildRecords.Count > 0)
        {
            var persistentPos = WriteGrupHeader(stream, cellLabel, 8);
            foreach (var record in bundle.PersistentChildRecords)
            {
                stream.Write(record);
            }

            RecordHeaderProcessor.FinalizeGrupSize(stream, persistentPos);
        }

        if (bundle.VwdChildRecords.Count > 0)
        {
            var vwdPos = WriteGrupHeader(stream, cellLabel, 10);
            foreach (var record in bundle.VwdChildRecords)
            {
                stream.Write(record);
            }

            RecordHeaderProcessor.FinalizeGrupSize(stream, vwdPos);
        }

        if (bundle.TemporaryChildRecords.Count > 0)
        {
            var temporaryPos = WriteGrupHeader(stream, cellLabel, 9);
            foreach (var record in bundle.TemporaryChildRecords)
            {
                stream.Write(record);
            }

            RecordHeaderProcessor.FinalizeGrupSize(stream, temporaryPos);
        }

        RecordHeaderProcessor.FinalizeGrupSize(stream, childPos);
    }

    private static long WriteGrupHeader(Stream stream, byte[] label, int groupType)
    {
        var header = new GroupHeader
        {
            GroupSize = 0,
            Label = label,
            GroupType = groupType,
            Stamp = 0,
            Unknown = 0
        };
        return RecordHeaderProcessor.WriteGrupHeader(stream, header);
    }

    /// <summary>
    ///     Reconstructs the raw bytes of a parsed main record (header + subrecord stream),
    ///     suitable for emission as an Identical-To-Master anchor record.
    /// </summary>
    /// <remarks>
    ///     Compressed records have already been decompressed during parsing (via
    ///     <see cref="EsmParser" />), so the reconstructed stream is uncompressed and the
    ///     compressed flag is cleared on output.
    /// </remarks>
    public static byte[] ReconstructRecordBytes(ParsedMainRecord parsed)
    {
        using var subStream = new MemoryStream();
        using (var subWriter = new BinaryWriter(subStream, Encoding.Latin1, true))
        {
            foreach (var sub in parsed.Subrecords)
            {
                SubrecordEncoder.WriteSubrecord(subWriter, sub.Signature, sub.Data);
            }
        }

        var subBytes = subStream.ToArray();

        using var stream = new MemoryStream();
        var header = parsed.Header with
        {
            DataSize = (uint)subBytes.Length,
            Flags = parsed.Header.Flags & ~0x00040000u // clear compressed flag
        };
        RecordHeaderProcessor.WriteRecordHeader(stream, header);
        stream.Write(subBytes);
        return stream.ToArray();
    }
}
