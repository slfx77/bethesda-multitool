using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics;

internal static class EsmQuestScriptReader
{
    internal static IReadOnlyList<EsmQuestScriptFragment> Read(
        IReadOnlyList<ParsedMainRecord> records, IReadOnlySet<uint> selectedIds,
        BethesdaGame game, int? recordHeaderSize)
    {
        if (selectedIds.Count == 0) return [];
        var symbols = BuildSymbols(records);
        var names = records.GroupBy(record => record.Header.FormId)
            .Select(group => (Id: group.Key, Names: group.Select(record => record.EditorId)
                .Distinct(StringComparer.Ordinal).ToArray()))
            .Where(group => group.Names.Length == 1 && !string.IsNullOrWhiteSpace(group.Names[0]))
            .ToDictionary(group => group.Id, group => group.Names[0]);
        string? ResolveName(uint id) => id == 0x14 ? "PlayerRef" : names.GetValueOrDefault(id);
        var results = new List<EsmQuestScriptFragment>();
        var occurrence = 0;
        foreach (var record in records.Where(record => record.Header.Signature == "QUST"
                                                       && selectedIds.Contains(record.Header.FormId)))
        {
            occurrence++;
            var subs = record.Subrecords;
            foreach (var span in ReadSpans(subs))
            {
                var bundle = ReadBundle(subs, span.StartIndex, span.End);
                var diagnostics = new List<string>(bundle.Diagnostics);
                var (stageOrdinal, stageIndex, entryOrdinal, ownerMalformed) = FindOwner(subs, span.StartIndex);
                if (stageIndex is null || entryOrdinal is null) diagnostics.Add("stage-entry-unavailable");
                if (ownerMalformed) diagnostics.Add("stage-entry-malformed");
                var scda = span.ScdaIndex >= 0 ? subs[span.ScdaIndex].Data.ToArray() : null;
                var sourceIndex = EsmScriptBlockReader.FindFirstSubrecord(subs, "SCTX", span.StartIndex, span.End);
                var source = sourceIndex >= 0 ? subs[sourceIndex].Data.ToArray() : null;
                var refs = bundle.References.Select(reference => reference.DecoderValue).ToList();
                var bindings = new List<ScriptExternalVariableBinding>();
                ScriptBytecodeByteOrderDecision? order = null;
                string? reconstruction = null;
                var bytecodeState = scda switch
                {
                    null => "absent",
                    { Length: 0 } => "empty",
                    _ => "recovered"
                };
                if (scda is { Length: > 0 })
                {
                    order = ScriptBytecodeByteOrderSelector.Select(scda, bundle.Variables, refs, false,
                        ScriptBytecodeByteOrderEvidence.AmbiguousSerializedDefault, game);
                    var functions = ScriptFunctionTables.For(game);
                    var reader = new BytecodeReader(scda, order.Value.IsBigEndian);
                    var decompiler = new ScriptDecompiler(bundle.Variables, refs, ResolveName,
                        order.Value.IsBigEndian, scriptName: null, symbols.Track(bindings), functions);
                    var text = decompiler.Decompile(scda, reader);
                    var analysis = ScriptBytecodeAnalyzer.Analyze(scda, order.Value.IsBigEndian,
                        bundle.Variables, refs, functions: functions);
                    if (reader.Position != scda.Length || reader.HasStructuralUncertainty
                        || analysis.HasDiagnostics || analysis.UnknownOpcodeCount > 0)
                    {
                        bytecodeState = "partial";
                        diagnostics.Add($"bytecode-partial:position={reader.Position}:length={scda.Length}");
                        if (analysis.HasDiagnostics) diagnostics.Add(analysis.Diagnostics);
                    }
                    reconstruction = CapturedScriptEmissionContract.BuildDecompiledSource(text,
                        bundle.Variables, null,
                        ["; Reconstruction", $"; QUST {record.Header.FormId:X8}, offset 0x{record.Offset:X}, block {span.BlockIndex}",
                            "; Evidence: manifest.json"], refs);
                    if (bindings.Any(binding => binding.Status != "resolved"))
                        diagnostics.Add("external-variable-unresolved");
                }
                foreach (var reference in bundle.References.Where(reference => reference.Kind == "SCRV"))
                    if (reference.RawValue is { } index && !bundle.Variables.Any(variable => variable.Index == index))
                        diagnostics.Add($"scrv-local-unavailable:{index}");
                var physical = recordHeaderSize.HasValue && !record.Header.IsCompressed
                    && subs.Sum(sub => 6L + sub.Data.Length) == record.Header.DataSize
                    && subs.All(sub => sub.Signature != "XXXX");
                long? offset = physical && span.ScdaIndex >= 0 ? record.Offset + recordHeaderSize!.Value
                    + subs.Take(span.ScdaIndex).Sum(sub => 6L + sub.Data.Length) + 6 : null;
                var sourceState = source switch
                {
                    null => "absent",
                    { Length: 0 } or [0] => "empty",
                    _ => "recovered"
                };
                var status = "Unavailable";
                if (diagnostics.Count > 0) status = "Partial";
                else if (scda is { Length: > 0 }) status = "Reconstruction";
                else if (sourceState == "recovered") status = "Stored";
                else if (scda is not null || source is not null) status = "Empty";
                string? byteOrder = null;
                if (order is { } selected) byteOrder = selected.IsBigEndian ? "big" : "little";
                results.Add(new(record.Header.FormId, record.EditorId, record.Offset, occurrence,
                    record.Header.Flags, span.BlockIndex, stageOrdinal, stageIndex, entryOrdinal,
                    span.StartIndex, span.ScdaIndex < 0 ? null : span.ScdaIndex, offset,
                    offset.HasValue ? "physical-payload" : "record-and-subrecord-index-only",
                    bundle.HeaderState, bundle.CompiledSize, bundle.ReferenceCount, bundle.VariableCount,
                    bytecodeState, sourceState, status,
                    byteOrder,
                    order?.Evidence.ToString(), order?.Detail, scda, source, reconstruction,
                    bundle.Variables, bundle.References, bindings, bundle.Metadata, diagnostics));
            }
        }
        return results;
    }

    private static List<FragmentSpan> ReadSpans(List<ParsedSubrecord> subs)
    {
        var spans = EsmScriptBlockReader.LocateScriptBlocks(subs, questBoundaries: true)
            .Select(span => new FragmentSpan(span.BlockIndex, span.StartIndex, span.ScdaIndex, span.End)).ToList();
        var index = spans.Count;
        for (var i = 0; i < subs.Count; i++)
        {
            if (subs[i].Signature != "SCTX" || spans.Any(span => i >= span.StartIndex && i < span.End)) continue;
            var end = EsmScriptBlockReader.FindScriptBlockEnd(subs, i + 1, questBoundaries: true);
            var nextBundle = spans.Where(span => span.StartIndex > i).Select(span => span.StartIndex)
                .DefaultIfEmpty(end).Min();
            var nextSource = EsmScriptBlockReader.FindFirstSubrecord(subs, "SCTX", i + 1, end);
            end = Math.Min(end, nextBundle);
            if (nextSource >= 0) end = Math.Min(end, nextSource);
            spans.Add(new(++index, i, -1, end));
        }
        return spans.OrderBy(span => span.StartIndex).ToList();
    }

    private static (int? StageOrdinal, ushort? StageIndex, int? EntryOrdinal, bool Malformed) FindOwner(
        List<ParsedSubrecord> subs, int start)
    {
        var stageCount = 0;
        int? stageOrdinal = null;
        ushort? stage = null;
        int? entry = null;
        var malformed = false;
        for (var i = 0; i < start; i++)
        {
            var sub = subs[i];
            switch (sub.Signature)
            {
                case "INDX":
                    stageOrdinal = ++stageCount;
                    // xEdit QUST INDX: little-endian on both PC and Xbox.
                    stage = sub.Data.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(sub.Data) : null;
                    entry = null;
                    malformed = stage is null;
                    break;
                case "QSDT":
                    entry = stageOrdinal.HasValue ? (entry ?? 0) + 1 : null;
                    malformed = stage is null || sub.Data.Length == 0;
                    break;
                case "QOBJ" or "QSTA":
                    stageOrdinal = null;
                    stage = null;
                    entry = null;
                    break;
            }
        }
        return (stageOrdinal, stage, entry, malformed);
    }

    private static ExternalScriptVariableResolver BuildSymbols(IReadOnlyList<ParsedMainRecord> records)
    {
        var scripts = new List<ScriptRecord>();
        var links = new List<ScriptOwnerLink>();
        foreach (var record in records)
        {
            if (record.Header.Signature == "SCPT")
            {
                var bundle = ReadBundle(record.Subrecords, 0, record.Subrecords.Count);
                scripts.Add(new ScriptRecord
                {
                    FormId = record.Header.FormId, Variables = bundle.Variables,
                    HasMalformedSerializedTable = bundle.Diagnostics.Count > 0,
                    IsIncompleteExecutableBundle = bundle.HeaderState != "recovered"
                });
                continue;
            }
            var linkSignature = record.Header.Signature switch
            {
                "REFR" or "ACHR" or "ACRE" => "NAME",
                "NPC_" or "CREA" or "ACTI" or "CONT" or "TERM" or "DOOR"
                    or "LIGH" or "FURN" or "QUST" or "AMMO" or "WEAP" or "ARMO" or "BOOK" or "MISC"
                    or "ALCH" or "KEYM" or "CARD" or "CHAL" or "LVLC" or "LVLN" or "FACT" => "SCRI",
                _ => null
            };
            if (linkSignature is null) continue;
            var found = false;
            foreach (var sub in record.Subrecords.Where(sub => sub.Signature == linkSignature))
            {
                links.Add(new(record.Header.FormId, sub.Data.Length == 4 ? sub.DataAsFormId : 0));
                found = true;
            }
            // Missing links in one physical copy must conflict with a link in another copy.
            if (!found) links.Add(new(record.Header.FormId, 0));
        }
        return new(scripts, links);
    }

    private static Bundle ReadBundle(List<ParsedSubrecord> subs, int start, int end)
    {
        var variables = new List<ScriptVariableInfo>();
        var localParser = new SerializedScriptLocalTableParser(variables);
        var references = new List<EsmQuestScriptReference>();
        var metadata = new List<EsmQuestScriptMetadata>();
        var diagnostics = new List<string>();
        var headers = 0;
        var scdas = 0;
        var sources = 0;
        var compiledLength = 0;
        uint? compiledSize = null, referenceCount = null, variableCount = null;
        var headerState = "absent";
        for (var i = start; i < end; i++)
        {
            var sub = subs[i];
            localParser.ObserveSubrecord(sub.Signature, sub.Data, sub.BigEndian);
            if (sub.Signature is "SCHR" or "SLSD" or "SCVR" or "SCRO" or "SCRV" or "SCTX")
                metadata.Add(new(sub.Signature, i, sub.Data.ToArray()));
            switch (sub.Signature)
            {
                case "SCHR":
                    headers++;
                    headerState = sub.Data.Length == 20 && headers == 1 ? "recovered" : "malformed";
                    if (sub.Data.Length >= 20 && headers == 1)
                    {
                        referenceCount = ReadUInt32(sub, 4);
                        compiledSize = ReadUInt32(sub, 8);
                        variableCount = ReadUInt32(sub, 12);
                    }
                    break;
                case "SCDA":
                    scdas++;
                    compiledLength = sub.Data.Length;
                    break;
                case "SCTX":
                    sources++;
                    break;
                case "SCRO" or "SCRV":
                    var value = sub.Data.Length >= 4 ? ReadUInt32(sub, 0) : (uint?)null;
                    references.Add(new(references.Count + 1, sub.Signature, value, i));
                    if (sub.Data.Length != 4 || value >= 0x80000000)
                        diagnostics.Add($"reference-slot-malformed:{references.Count}");
                    break;
            }
        }
        localParser.Complete();
        if (localParser.IsMalformed)
        {
            diagnostics.Add("local-table-malformed");
            // A duplicate slot can disagree with the earlier accepted declaration. Keep the raw
            // table in metadata; do not let its first name label a numeric operand or declaration.
            variables.Clear();
        }
        if (headerState != "recovered") diagnostics.Add($"header-{headerState}");
        if (compiledSize is { } size && size != compiledLength) diagnostics.Add("compiled-size-mismatch");
        if (referenceCount is { } count && count != references.Count) diagnostics.Add("reference-count-mismatch");
        if (scdas > 1 || sources > 1) diagnostics.Add("duplicate-script-payload");
        return new(headerState, compiledSize, referenceCount, variableCount, variables, references, metadata, diagnostics);
    }

    private static uint ReadUInt32(ParsedSubrecord sub, int offset) => sub.BigEndian
        ? BinaryPrimitives.ReadUInt32BigEndian(sub.Data.AsSpan(offset, 4))
        : BinaryPrimitives.ReadUInt32LittleEndian(sub.Data.AsSpan(offset, 4));

    private sealed record Bundle(string HeaderState, uint? CompiledSize, uint? ReferenceCount,
        uint? VariableCount, List<ScriptVariableInfo> Variables, List<EsmQuestScriptReference> References,
        List<EsmQuestScriptMetadata> Metadata, List<string> Diagnostics);

    private sealed record FragmentSpan(int BlockIndex, int StartIndex, int ScdaIndex, int End);
}
