using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using static BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics.EsmScriptDiagnosticsResolvers;

namespace BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics;

/// <summary>
///     Builds the per-block (SCDA) diagnostic rows and reference-slot rows for a script-bearing record, walking each
///     SCHR/SCDA block and validating its declared sizes/order against the actual subrecords.
/// </summary>
internal static class EsmScriptBlockRowBuilder
{
    public static void ExtractScriptBlocks(
        EsmScriptDiagnosticRecordRow recordRow,
        ParsedMainRecord record,
        IReadOnlyDictionary<uint, EsmScriptFormIdInfo> index,
        IReadOnlySet<uint> validFormIds,
        List<EsmScriptDiagnosticBlockRow> scriptBlocks,
        List<EsmScriptDiagnosticReferenceRow> scriptReferences)
    {
        var blockIndex = 0;
        var scdaSeen = new HashSet<int>();
        var subs = record.Subrecords;

        for (var i = 0; i < subs.Count; i++)
        {
            if (subs[i].Signature != "SCHR")
            {
                continue;
            }

            var end = EsmScriptBlockReader.FindScriptBlockEnd(subs, i + 1);
            var scdaIndex = EsmScriptBlockReader.FindFirstSubrecord(subs, "SCDA", i + 1, end);
            blockIndex++;
            if (scdaIndex >= 0)
            {
                scdaSeen.Add(scdaIndex);
            }

            AddScriptBlockRows(recordRow, record, blockIndex, i, end, scdaIndex, index, validFormIds,
                scriptBlocks, scriptReferences);
        }

        foreach (var (sub, indexInRecord) in subs.Select((sub, indexInRecord) => (sub, indexInRecord)))
        {
            if (sub.Signature != "SCDA" || scdaSeen.Contains(indexInRecord))
            {
                continue;
            }

            blockIndex++;
            var end = EsmScriptBlockReader.FindScriptBlockEnd(subs, indexInRecord + 1);
            AddScriptBlockRows(recordRow, record, blockIndex, -1, end, indexInRecord, index, validFormIds,
                scriptBlocks, scriptReferences);
        }
    }

    private static void AddScriptBlockRows(
        EsmScriptDiagnosticRecordRow recordRow,
        ParsedMainRecord record,
        int blockIndex,
        int schrIndex,
        int blockEnd,
        int scdaIndex,
        IReadOnlyDictionary<uint, EsmScriptFormIdInfo> index,
        IReadOnlySet<uint> validFormIds,
        List<EsmScriptDiagnosticBlockRow> scriptBlocks,
        List<EsmScriptDiagnosticReferenceRow> scriptReferences)
    {
        var subs = record.Subrecords;
        var blockStart = schrIndex >= 0 ? schrIndex + 1 : scdaIndex + 1;
        var header = schrIndex >= 0 ? TryReadScriptHeader(subs[schrIndex].Data) : default;
        var variables = EsmScriptBlockReader.ReadScriptVariables(subs, blockStart, blockEnd);
        var refs = EsmScriptBlockReader.ReadScriptReferences(subs, blockStart, blockEnd);
        var scda = scdaIndex >= 0 ? subs[scdaIndex].Data : [];
        var analysis = scda.Length > 0
            ? ScriptBytecodeAnalyzer.Analyze(scda, false, variables,
                refs.Select(r => r.Kind == "SCRV" ? 0x80000000u | r.RawValue : r.RawValue).ToList())
            : new ScriptBytecodeAnalysis(0, false, true, 0, 0, false, string.Empty);

        var compiledSizeMatches = !header.CompiledSize.HasValue || header.CompiledSize.Value == scda.Length;
        var refCountMatches = !header.RefObjectCount.HasValue || header.RefObjectCount.Value == refs.Count;

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
            Truncate(EsmScriptBlockReader.ReadFirstStringSubrecord(subs, "SCTX", blockStart, blockEnd), 180)));

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

    private static (uint? VariableCount, uint? RefObjectCount, uint? CompiledSize) TryReadScriptHeader(byte[]? data)
    {
        if (data is not { Length: >= 20 })
        {
            return (null, null, null);
        }

        return (
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4)));
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

