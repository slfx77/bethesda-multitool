using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;

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
    ///     Subrecords dropped from master WRLD anchor clones. OFST is the worldspace's per-file
    ///     cell offset table, holding byte offsets into the file the record was READ from, so
    ///     the master's payload is meaningless here — copied verbatim it makes the engine seek
    ///     THIS file at the master's offsets and every loaded exterior cell fails with
    ///     "CELLS: Failed to load temporary data".
    ///     <para>
    ///         Dropping it is only half the job: the record is then re-emitted with a table rebuilt
    ///         for this file by <see cref="WorldOfstTableBuilder" />. Shipping no OFST at all is a
    ///         third, separately broken state — see that class for why it crashes the cell attach.
    ///     </para>
    /// </summary>
    private static readonly IReadOnlySet<string> WrldAnchorStripSubrecords =
        new HashSet<string>(StringComparer.Ordinal) { "OFST" };

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
            wrldAnchorBytes = ReconstructRecordBytes(wrldRecord, WrldAnchorStripSubrecords);
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

        // WRLD anchor record, carrying a zero-filled OFST sized to this worldspace's grid.
        // Entries are WRLD-relative, so they are filled in below once the cells are placed —
        // no post-assembly fixup needed. A worldspace whose bounds we can't read keeps the
        // pre-existing no-OFST behaviour rather than getting a guessed table.
        var wrldPos = stream.Position;
        var grid = WorldOfstTableBuilder.TryReadGrid(wrldAnchorBytes);
        var ofstPayloadPos = -1L;
        if (grid is not null)
        {
            wrldAnchorBytes = WorldOfstTableBuilder.AppendEmptyOfst(
                wrldAnchorBytes, grid.Count, out var payloadOffsetInRecord);
            ofstPayloadPos = wrldPos + payloadOffsetInRecord;
        }

        stream.Write(wrldAnchorBytes);

        // World children GRUP (type 1, label = EMITTED WRLD FormID — matches the anchor bytes).
        var wrldLabel = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(wrldLabel, emittedWrldFormId);
        var childrenPos = WriteGrupHeader(stream, wrldLabel, 1);

        // Persistent CELL containers go directly under world children, no block wrapping.
        // They are deliberately NOT indexed into OFST: the table addresses exterior grid
        // slots, and a persistent cell reached through one is exactly the crash this fixes.
        foreach (var bundle in bundlesInWrld.Where(b => b.Context.IsPersistentCellContainer))
        {
            WriteCellAndChildren(stream, bundle);
        }

        // Remaining (block-bound) cells get the exterior block/subblock hierarchy.
        var blockBound = bundlesInWrld.Where(b => !b.Context.IsPersistentCellContainer).ToList();
        var exteriorCellPositions = new List<(long Position, int GridX, int GridY)>();
        if (blockBound.Count > 0)
        {
            EmitBlocksAndSubblocks(stream, blockBound, 4, 5, (bundle, position) =>
            {
                if (WorldOfstTableBuilder.TryReadCellGrid(bundle.CellRecordBytes, out var x, out var y))
                {
                    exteriorCellPositions.Add((position, x, y));
                }
            });
        }

        RecordHeaderProcessor.FinalizeGrupSize(stream, childrenPos);

        if (grid is not null)
        {
            WorldOfstTableBuilder.PatchTable(
                stream, ofstPayloadPos, wrldPos, grid, exteriorCellPositions);
        }

        return true;
    }

    /// <summary>
    ///     Group bundles by their block label, then by their subblock label, and emit the
    ///     proper nested GRUPs. Used by both interior (block=2, subblock=3) and exterior
    ///     (block=4, subblock=5) paths.
    /// </summary>
    /// <param name="onCellWritten">
    ///     Called with each cell bundle and the stream position it is about to be written at,
    ///     so the exterior path can index cells into the worldspace's OFST table.
    /// </param>
    private static void EmitBlocksAndSubblocks(
        Stream stream,
        IReadOnlyList<CellOverrideBundle> bundles,
        int blockGroupType,
        int subblockGroupType,
        Action<CellOverrideBundle, long>? onCellWritten = null)
    {
        // INTERIOR cells (block group type 2) are filed at the engine's FormID-derived
        // block/sub-block position — block = (fid & 0xFFFFFF) % 10, sub-block =
        // ((fid & 0xFFFFFF) % 100) / 10 — NOT the master's copied labels. Decompile- +
        // runtime-verified (block_formula.txt FUN_005441b0/FUN_00544210; CellAttachTrace):
        // the base master stores ~all its interior cells at a flat block 0 / sub 0 and finds
        // them only via its cached fast-seek offset. A dependent plugin's override, once it
        // clobbers that shared offset, is located by the engine's SCAN fallback, whose descend
        // predicate (FUN_00543df0) accepts a block/sub GRUP only when its label equals the
        // formula. Copying the master's flat 0/0 labels makes the scan skip our GRUPs and never
        // find the cell → temp children never stream → crash / empty interior. Every shipped
        // FNV DLC files its interior overrides at the formula position for exactly this reason
        // (e.g. OWB's 000E6A6E at block 0 / sub 5 = the formula). Exteriors (type 4) keep their
        // master block/sub labels — that path is FormID-agnostic and their coords are canonical.
        var useInteriorFormula = blockGroupType == 2;

        byte[] BlockLabelFor(CellOverrideBundle b)
        {
            return useInteriorFormula ? LabelBytes((b.CellFormId & 0xFFFFFFu) % 10) : b.Context.BlockLabel!;
        }

        byte[] SubblockLabelFor(CellOverrideBundle b)
        {
            return useInteriorFormula ? LabelBytes((b.CellFormId & 0xFFFFFFu) % 100 / 10) : b.Context.SubblockLabel!;
        }

        // Group by block label, then by subblock label.
        var byBlock = bundles
            .Where(b => useInteriorFormula
                        || (b.Context.BlockLabel is { Length: 4 } && b.Context.SubblockLabel is { Length: 4 }))
            .GroupBy(b => BinaryPrimitives.ReadUInt32LittleEndian(BlockLabelFor(b)))
            .OrderBy(g => g.Key);

        foreach (var blockGroup in byBlock)
        {
            var blockLabel = BlockLabelFor(blockGroup.First());
            var blockPos = WriteGrupHeader(stream, blockLabel, blockGroupType);

            var bySubblock = blockGroup
                .GroupBy(b => BinaryPrimitives.ReadUInt32LittleEndian(SubblockLabelFor(b)))
                .OrderBy(g => g.Key);

            foreach (var subblockGroup in bySubblock)
            {
                var subblockLabel = SubblockLabelFor(subblockGroup.First());
                var subblockPos = WriteGrupHeader(stream, subblockLabel, subblockGroupType);

                foreach (var bundle in subblockGroup.OrderBy(b => b.CellFormId))
                {
                    onCellWritten?.Invoke(bundle, stream.Position);
                    WriteCellAndChildren(stream, bundle);
                }

                RecordHeaderProcessor.FinalizeGrupSize(stream, subblockPos);
            }

            RecordHeaderProcessor.FinalizeGrupSize(stream, blockPos);
        }
    }

    private static byte[] LabelBytes(uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return b;
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
        return ReconstructRecordBytes(parsed, null);
    }

    /// <summary>
    ///     Reconstructs a parsed main record's raw bytes, optionally omitting subrecords whose
    ///     signature is in <paramref name="stripSubrecordSignatures" />. Used by
    ///     <see cref="CellStructuralReferencePreserver" /> to drop emittance links (XEMI) from
    ///     preserved master refs: when the plugin is ESM-flagged the engine eager-resolves these
    ///     during master-init and dereferences the still-unlinked REGN FormID as a pointer
    ///     (the Doc Mitchell light-rays AV). Emittance is a cosmetic runtime grid the engine rebuilds.
    /// </summary>
    public static byte[] ReconstructRecordBytes(
        ParsedMainRecord parsed, IReadOnlySet<string>? stripSubrecordSignatures)
    {
        using var subStream = new MemoryStream();
        using (var subWriter = new BinaryWriter(subStream, Encoding.Latin1, true))
        {
            foreach (var sub in parsed.Subrecords)
            {
                if (stripSubrecordSignatures is not null
                    && stripSubrecordSignatures.Contains(sub.Signature))
                {
                    continue;
                }

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
