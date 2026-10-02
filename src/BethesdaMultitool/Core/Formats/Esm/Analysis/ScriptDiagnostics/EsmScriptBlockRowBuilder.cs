using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.Games;
using static BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics.EsmScriptDiagnosticsResolvers;

namespace BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics;

/// <summary>
///     Builds the per-block (SCDA) diagnostic rows and reference-slot rows for a script-bearing record, walking each
///     SCHR/SCDA block and validating its declared sizes/order against the actual subrecords.
/// </summary>
internal static class EsmScriptBlockRowBuilder
{
    /// <param name="game">
    ///     Selects the command table SCDA is walked with, both by the byte-order selector and by
    ///     the block row's own walk of the order it chose
    ///     (<see cref="ScriptBytecodeByteOrderSelector.ResolveFunctionSet" />): Oblivion, FO3 and FNV
    ///     use their own tables, anything else (Unknown included) the FNV/FO3 table. Only Oblivion's
    ///     table differs from the default, so an Oblivion plugin analyzed without its game can
    ///     carry a false "Big-endian SCDA" row — which is why the parameter has no default.
    /// </param>
    public static void ExtractScriptBlocks(
        EsmScriptDiagnosticRecordRow recordRow,
        ParsedMainRecord record,
        IReadOnlyDictionary<uint, EsmScriptFormIdInfo> index,
        IReadOnlySet<uint> validFormIds,
        List<EsmScriptDiagnosticBlockRow> scriptBlocks,
        List<EsmScriptDiagnosticReferenceRow> scriptReferences,
        BethesdaGame game)
    {
        var subs = record.Subrecords;

        // A TERM block belongs to the menu item whose ITXT precedes it, so the block row can name
        // the item it runs for (target_terminal_items.csv joins back on the same block index).
        IReadOnlyList<EsmScriptTerminalItemRowBuilder.MenuItemSpan> menuItems =
            record.Header.Signature == "TERM"
                ? EsmScriptTerminalItemRowBuilder.LocateMenuItems(subs)
                : [];

        foreach (var block in EsmScriptBlockReader.LocateScriptBlocks(subs))
        {
            var owner = EsmScriptTerminalItemRowBuilder.FindOwner(menuItems, block.StartIndex);
            AddScriptBlockRows(recordRow, record, block.BlockIndex, block.SchrIndex, block.End, block.ScdaIndex,
                owner, index, validFormIds, scriptBlocks, scriptReferences, game);
        }
    }

    private static void AddScriptBlockRows(
        EsmScriptDiagnosticRecordRow recordRow,
        ParsedMainRecord record,
        int blockIndex,
        int schrIndex,
        int blockEnd,
        int scdaIndex,
        EsmScriptTerminalItemRowBuilder.MenuItemSpan? owner,
        IReadOnlyDictionary<uint, EsmScriptFormIdInfo> index,
        IReadOnlySet<uint> validFormIds,
        List<EsmScriptDiagnosticBlockRow> scriptBlocks,
        List<EsmScriptDiagnosticReferenceRow> scriptReferences,
        BethesdaGame game)
    {
        var subs = record.Subrecords;
        var blockStart = schrIndex >= 0 ? schrIndex + 1 : scdaIndex + 1;
        var header = schrIndex >= 0
            ? TryReadScriptHeader(subs[schrIndex].Data, subs[schrIndex].BigEndian)
            : default;
        var variables = EsmScriptBlockReader.ReadScriptVariables(subs, blockStart, blockEnd);
        var refs = EsmScriptBlockReader.ReadScriptReferences(subs, blockStart, blockEnd);
        var scda = scdaIndex >= 0 ? subs[scdaIndex].Data : [];
        var analysis = scda.Length > 0
            ? AnalyzeBlock(scda, variables, refs, game)
            : new ScriptBytecodeAnalysis(0, false, true, 0, 0, false, string.Empty);

        var compiledSizeMatches = !header.CompiledSize.HasValue || header.CompiledSize.Value == scda.Length;
        var refCountMatches = !header.RefObjectCount.HasValue || header.RefObjectCount.Value == refs.Count;
        var sourceText = EsmScriptBlockReader.ReadFirstStringSubrecord(subs, "SCTX", blockStart, blockEnd);

        scriptBlocks.Add(new EsmScriptDiagnosticBlockRow(
            recordRow.Target,
            recordRow.Relation,
            record.Header.Signature,
            record.Header.FormId,
            recordRow.EditorId,
            blockIndex,
            BuildSubrecordOrder(subs, schrIndex, blockEnd),
            ValidateScriptBlockOrder(subs, schrIndex, blockEnd, scdaIndex),
            scda.Length,
            header.CompiledSize,
            header.RefObjectCount,
            refs.Count,
            compiledSizeMatches,
            refCountMatches,
            analysis.WalkedToEnd,
            analysis.HasDiagnostics,
            analysis.Diagnostics,
            Truncate(sourceText, 180),
            sourceText,
            owner?.Index,
            owner?.Text ?? string.Empty));

        var slotIndex = 0;
        foreach (var reference in refs)
        {
            slotIndex++;
            var resolvedFormId = reference.Kind == "SCRV" ? 0 : reference.RawValue;
            var status = ResolveReferenceStatus(reference, validFormIds);
            index.TryGetValue(resolvedFormId, out var resolved);
            scriptReferences.Add(new EsmScriptDiagnosticReferenceRow(
                recordRow.Target,
                record.Header.Signature,
                record.Header.FormId,
                blockIndex,
                slotIndex,
                reference.Kind,
                reference.RawValue,
                resolvedFormId,
                status,
                resolved?.RecordType ?? string.Empty,
                resolved?.EditorId ?? string.Empty,
                resolved?.FullName ?? string.Empty));
        }
    }

    /// <summary>
    ///     Walks the block's SCDA in the byte order its own payload selects. Serialized SCDA is
    ///     little-endian inside an Xbox 360 record too, so a payload the walks cannot decide is
    ///     read little-endian — diagnostics only ever read on-disk plugins.
    ///     <para>
    ///         A payload that only reads big-endian is walked that way, so its row shows what the
    ///         bytes say, but it stays a diagnostic: the engine reads serialized SCDA little-endian
    ///         on every platform, so big-endian bytecode in a plugin (typically an unswapped runtime
    ///         capture) is a defect even when it walks cleanly.
    ///     </para>
    ///     <para>
    ///         The row walks the chosen order with the same command table the selector decided
    ///         with, so the decision and the reported walk never come from two different tables.
    ///     </para>
    /// </summary>
    internal static ScriptBytecodeAnalysis AnalyzeBlock(
        byte[] scda,
        List<ScriptVariableInfo> variables,
        List<EsmScriptBlockReader.ScriptReferenceSlot> refs,
        BethesdaGame game)
    {
        var referencedObjects = refs
            .Select(r => r.Kind == "SCRV" ? 0x80000000u | r.RawValue : r.RawValue)
            .ToList();
        var bytecodeOrder = ScriptBytecodeByteOrderSelector.Select(
            scda,
            variables,
            referencedObjects,
            false,
            ScriptBytecodeByteOrderEvidence.AmbiguousSerializedDefault,
            game);
        var analysis = ScriptBytecodeAnalyzer.Analyze(
            scda,
            bytecodeOrder.IsBigEndian,
            variables,
            referencedObjects,
            functions: ScriptBytecodeByteOrderSelector.ResolveFunctionSet(game));
        if (!bytecodeOrder.IsBigEndian)
        {
            return analysis;
        }

        var note = $"; Big-endian SCDA in a serialized record ({bytecodeOrder.Evidence}); "
                   + "the engine reads serialized SCDA little-endian";
        return analysis with
        {
            HasDiagnostics = true,
            Diagnostics = analysis.HasDiagnostics ? $"{note} | {analysis.Diagnostics}" : note
        };
    }

    /// <summary>
    ///     Reads the SCHR counts in the subrecord's container order. Serialized SCHR (20 bytes) is
    ///     4 unused bytes, then RefCount at +4, CompiledSize at +8 and VariableCount at +12, then
    ///     the Type/Flags bytes at +16..+19 (byte flags, never swapped). Offset 0 is not a count.
    /// </summary>
    internal static (uint? VariableCount, uint? RefObjectCount, uint? CompiledSize) TryReadScriptHeader(
        byte[]? data,
        bool bigEndian)
    {
        if (data is not { Length: >= 20 })
        {
            return (null, null, null);
        }

        return (
            ReadUInt32(data.AsSpan(12, 4), bigEndian),
            ReadUInt32(data.AsSpan(4, 4), bigEndian),
            ReadUInt32(data.AsSpan(8, 4), bigEndian));
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data)
            : BinaryPrimitives.ReadUInt32LittleEndian(data);
    }

    private static string BuildSubrecordOrder(List<ParsedSubrecord> subrecords, int schrIndex, int end)
    {
        var start = schrIndex >= 0 ? schrIndex : Math.Max(0, end - 1);
        var signatures = subrecords
            .Skip(start)
            .Take(Math.Max(0, end - start))
            .Select(s => s.Signature)
            .ToList();
        if (end < subrecords.Count && subrecords[end].Signature == "NEXT")
        {
            signatures.Add("NEXT");
        }

        return string.Join('>', signatures);
    }

    private static string ValidateScriptBlockOrder(
        IReadOnlyList<ParsedSubrecord> subrecords,
        int schrIndex,
        int blockEnd,
        int scdaIndex)
    {
        if (schrIndex < 0)
        {
            return "implicit-scda-without-schr";
        }

        var sctxIndex = EsmScriptBlockReader.FindFirstSubrecord(subrecords, "SCTX", schrIndex + 1, blockEnd);
        var firstRefIndex = FindFirstReferenceSubrecord(subrecords, schrIndex + 1, blockEnd);

        if (scdaIndex >= 0 && sctxIndex >= 0 && sctxIndex < scdaIndex)
        {
            return "sctx-before-scda";
        }

        if (firstRefIndex >= 0 && scdaIndex >= 0 && firstRefIndex < scdaIndex)
        {
            return "reference-before-scda";
        }

        if (firstRefIndex >= 0 && sctxIndex >= 0 && firstRefIndex < sctxIndex)
        {
            return "reference-before-sctx";
        }

        return "canonical";
    }

    private static int FindFirstReferenceSubrecord(IReadOnlyList<ParsedSubrecord> subrecords, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (subrecords[i].Signature is "SCRO" or "SCRV")
            {
                return i;
            }
        }

        return -1;
    }

    private static string ResolveReferenceStatus(
        EsmScriptBlockReader.ScriptReferenceSlot reference,
        IReadOnlySet<uint> validFormIds)
    {
        if (reference.Kind == "SCRV")
        {
            return "Variable";
        }

        if (reference.RawValue == 0)
        {
            return "Null";
        }

        return validFormIds.Contains(reference.RawValue) ? "Resolved" : "Missing";
    }
}
