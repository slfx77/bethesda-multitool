namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin;

/// <summary>
///     The GRUP nesting context of a single CELL record in the master ESM. We capture this so
///     plugin overrides can reproduce the master's exact block/subblock placement instead of
///     guessing via a (potentially-wrong) hash formula.
/// </summary>
public sealed record PcEsmCellContext
{
    public required uint CellFormId { get; init; }

    /// <summary>True for cells inside the top-level CELL GRUP; false for cells inside WRLD.</summary>
    public required bool IsInterior { get; init; }

    /// <summary>FormID of the parent worldspace (null for interior cells).</summary>
    public uint? WorldspaceFormId { get; init; }

    /// <summary>
    ///     Raw 4-byte label of the enclosing block GRUP. Null when the CELL is the worldspace's
    ///     persistent ref container (which sits directly under the world children GRUP without
    ///     a block/subblock wrapper).
    /// </summary>
    public byte[]? BlockLabel { get; init; }

    /// <summary>Raw 4-byte label of the enclosing subblock GRUP. Null for persistent cells.</summary>
    public byte[]? SubblockLabel { get; init; }

    /// <summary>2 for interior, 4 for exterior, 0 for persistent cells.</summary>
    public int BlockGroupType { get; init; }

    /// <summary>3 for interior, 5 for exterior, 0 for persistent cells.</summary>
    public int SubblockGroupType { get; init; }

    /// <summary>True when this is the worldspace's persistent ref container (no block/subblock).</summary>
    public bool IsPersistentCellContainer => !IsInterior && BlockLabel is null;
}

/// <summary>
///     Builds a map of master CELL FormID → <see cref="PcEsmCellContext" /> by walking the PC
///     ESM's GRUP and record streams in offset order while tracking the active GRUP stack.
/// </summary>
public static class PcEsmCellContextIndex
{
    public static Dictionary<uint, PcEsmCellContext> Build(byte[] pcEsmBytes)
    {
        var (records, grupHeaders) = EsmParser.EnumerateRecordsWithGrups(pcEsmBytes);
        return Build(records, grupHeaders);
    }

    /// <summary>
    ///     Test-friendly overload that takes pre-parsed records and GRUP headers.
    /// </summary>
    public static Dictionary<uint, PcEsmCellContext> Build(
        IEnumerable<ParsedMainRecord> records,
        IEnumerable<GrupHeaderInfo> grupHeaders)
    {
        // Merge records and GRUP headers into a single offset-sorted event stream.
        var events = new List<(long Offset, GrupHeaderInfo? Grup, ParsedMainRecord? Record)>();
        foreach (var g in grupHeaders)
        {
            events.Add((g.Offset, g, null));
        }

        foreach (var r in records)
        {
            events.Add((r.Offset, null, r));
        }

        events.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        var stack = new Stack<GrupHeaderInfo>();
        var contexts = new Dictionary<uint, PcEsmCellContext>();

        foreach (var (offset, grup, record) in events)
        {
            // Pop any GRUPs whose region we've left.
            while (stack.TryPeek(out var top) && top.Offset + top.GroupSize <= offset)
            {
                stack.Pop();
            }

            if (grup is not null)
            {
                stack.Push(grup);
                continue;
            }

            if (record is null || record.Header.Signature != "CELL")
            {
                continue;
            }

            contexts[record.Header.FormId] = ComputeCellContext(record, stack);
        }

        return contexts;
    }

    /// <summary>
    ///     Inspect the GRUP stack to determine the cell's location in the hierarchy. We walk
    ///     from innermost outward, looking for the block/subblock and top-level GRUPs.
    /// </summary>
    private static PcEsmCellContext ComputeCellContext(ParsedMainRecord cell, Stack<GrupHeaderInfo> stack)
    {
        GrupHeaderInfo? blockGrup = null;
        GrupHeaderInfo? subblockGrup = null;
        GrupHeaderInfo? worldChildrenGrup = null;
        GrupHeaderInfo? topLevelGrup = null;

        foreach (var g in stack) // Stack iterates innermost-to-outermost.
        {
            switch (g.GroupType)
            {
                case 3 when subblockGrup is null:    // interior subblock
                case 5 when subblockGrup is null:    // exterior subblock
                    subblockGrup = g;
                    break;
                case 2 when blockGrup is null:       // interior block
                case 4 when blockGrup is null:       // exterior block
                    blockGrup = g;
                    break;
                case 1 when worldChildrenGrup is null:   // world children
                    worldChildrenGrup = g;
                    break;
                case 0 when topLevelGrup is null:    // top-level CELL or WRLD
                    topLevelGrup = g;
                    break;
            }
        }

        var isInterior = topLevelGrup?.LabelAsSignature == "CELL";

        uint? worldspaceFormId = null;
        if (worldChildrenGrup is not null && worldChildrenGrup.Label.Length >= 4)
        {
            worldspaceFormId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(worldChildrenGrup.Label);
        }

        return new PcEsmCellContext
        {
            CellFormId = cell.Header.FormId,
            IsInterior = isInterior,
            WorldspaceFormId = worldspaceFormId,
            BlockLabel = blockGrup?.Label,
            SubblockLabel = subblockGrup?.Label,
            BlockGroupType = blockGrup?.GroupType ?? 0,
            SubblockGroupType = subblockGrup?.GroupType ?? 0
        };
    }
}
