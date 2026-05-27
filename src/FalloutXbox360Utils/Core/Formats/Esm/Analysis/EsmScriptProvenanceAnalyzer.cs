using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;
using FalloutXbox360Utils.Core.Formats.Esm.Script;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils.Core.Formats.Esm.Analysis;

public sealed record EsmScriptProvenanceReport(
    IReadOnlyList<EsmScriptSourceVsEmittedRefRow> SourceVsEmittedRefs,
    IReadOnlyList<EsmResultScriptProvenanceRow> ResultScripts,
    IReadOnlyList<EsmBytecodeEndianProbeRow> BytecodeEndianProbes,
    IReadOnlyList<EsmTargetStateTraceRow> StateTrace);

public sealed record EsmScriptSourceVsEmittedRefRow(
    string Target,
    string RecordType,
    uint EmittedFormId,
    string EmittedEditorId,
    int BlockIndex,
    int SlotIndex,
    string MatchStrategy,
    string SourceOrigin,
    uint SourceFormId,
    string SourceKind,
    uint SourceRawValue,
    string SourceLabel,
    string EmittedKind,
    uint EmittedRawValue,
    string EmittedLabel,
    string Classification);

public sealed record EsmResultScriptProvenanceRow(
    string Target,
    uint EmittedInfoFormId,
    uint SourceInfoFormId,
    string MatchStrategy,
    int SourceBlockCount,
    int EmittedBlockCount,
    string SourceScdaHashes,
    string EmittedScdaHashes,
    string SourceSctxPreview,
    string EmittedSctxPreview,
    string SourceReferenceCounts,
    string EmittedReferenceCounts,
    string Classification);

public sealed record EsmBytecodeEndianProbeRow(
    string Target,
    string Origin,
    string RecordType,
    uint FormId,
    string EditorId,
    int BlockIndex,
    int ByteLength,
    string FirstBytes,
    string LittleEndianOpcode,
    string BigEndianOpcode,
    bool LittleEndianWalkedToEnd,
    bool LittleEndianHasDiagnostics,
    string LittleEndianDiagnostics,
    bool BigEndianWalkedToEnd,
    bool BigEndianHasDiagnostics,
    string BigEndianDiagnostics,
    string Classification);

public sealed record EsmTargetStateTraceRow(
    string Target,
    string Category,
    string Relation,
    string RecordType,
    uint FormId,
    string EditorId,
    uint LinkedFormId,
    string LinkedLabel,
    string Detail);

public static class EsmScriptProvenanceAnalyzer
{
    private static readonly HashSet<string> ScriptBoundarySubrecords = new(StringComparer.Ordinal)
    {
        "NEXT", "SCHR", "POBA", "POEA", "POCA"
    };

    public static EsmScriptProvenanceReport AnalyzeFile(
        string generatedPath,
        EsmScriptDiagnosticsResult diagnostics,
        RecordCollection? sourceRecords,
        RecordCollection? masterRecords)
    {
        var data = File.ReadAllBytes(generatedPath);
        var generatedRecords = EsmParser.EnumerateRecordsWithGrups(data).Records;
        return AnalyzeRecords(generatedRecords, diagnostics, sourceRecords, masterRecords);
    }

    public static EsmScriptProvenanceReport AnalyzeRecords(
        IReadOnlyList<ParsedMainRecord> generatedRecords,
        EsmScriptDiagnosticsResult diagnostics,
        RecordCollection? sourceRecords,
        RecordCollection? masterRecords)
    {
        var generatedByFormId = generatedRecords
            .GroupBy(r => r.Header.FormId)
            .ToDictionary(g => g.Key, g => g.First());
        var generatedLabels = BuildGeneratedLabelIndex(generatedRecords);
        var sourceLabels = BuildCollectionLabelIndex(sourceRecords, masterRecords);
        var emittedLabelIndex = MergeLabels(generatedLabels, BuildCollectionLabelIndex(masterRecords, null));
        var sourceLookup = BuildSourceLookup(sourceRecords, masterRecords);

        var emittedBlocks = BuildEmittedSnapshots(generatedByFormId, diagnostics.Records);
        var sourceRefRows = BuildSourceReferenceRows(
            emittedBlocks,
            diagnostics,
            sourceLookup,
            sourceLabels,
            emittedLabelIndex);
        var resultRows = BuildResultScriptRows(
            emittedBlocks,
            diagnostics,
            sourceLookup);
        var endianRows = BuildEndianProbeRows(
            emittedBlocks,
            sourceLookup,
            diagnostics);
        var stateRows = BuildStateTraceRows(
            diagnostics,
            generatedByFormId,
            emittedLabelIndex);

        return new EsmScriptProvenanceReport(sourceRefRows, resultRows, endianRows, stateRows);
    }

    public static void WriteReport(EsmScriptProvenanceReport report, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(
            Path.Combine(outputDirectory, "script_source_vs_emitted_refs.csv"),
            BuildSourceVsEmittedRefsCsv(report),
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(outputDirectory, "result_script_provenance.csv"),
            BuildResultScriptProvenanceCsv(report),
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(outputDirectory, "bytecode_endian_probe.csv"),
            BuildBytecodeEndianProbeCsv(report),
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(outputDirectory, "target_state_trace.csv"),
            BuildStateTraceCsv(report),
            Encoding.UTF8);
    }

    private static List<BlockSnapshot> BuildEmittedSnapshots(
        IReadOnlyDictionary<uint, ParsedMainRecord> generatedByFormId,
        IReadOnlyList<EsmScriptDiagnosticRecordRow> recordRows)
    {
        var snapshots = new List<BlockSnapshot>();
        foreach (var row in recordRows)
        {
            if (!generatedByFormId.TryGetValue(row.FormId, out var record) ||
                !record.Subrecords.Any(s => s.Signature is "SCHR" or "SCDA"))
            {
                continue;
            }

            snapshots.AddRange(ExtractRawBlockSnapshots(
                row.Target,
                row.Relation,
                record.Header.Signature,
                record.Header.FormId,
                row.EditorId,
                record.Subrecords,
                false,
                "emitted",
                "emitted-formid"));
        }

        return snapshots
            .OrderBy(s => s.Target, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.RecordType, StringComparer.Ordinal)
            .ThenBy(s => s.FormId)
            .ThenBy(s => s.BlockIndex)
            .ToList();
    }

    private static List<BlockSnapshot> ExtractRawBlockSnapshots(
        string target,
        string relation,
        string recordType,
        uint formId,
        string editorId,
        IReadOnlyList<ParsedSubrecord> subrecords,
        bool isBigEndianBytecode,
        string origin,
        string matchStrategy)
    {
        var results = new List<BlockSnapshot>();
        var blockIndex = 0;
        var scdaSeen = new HashSet<int>();

        for (var i = 0; i < subrecords.Count; i++)
        {
            if (subrecords[i].Signature != "SCHR")
            {
                continue;
            }

            var end = FindScriptBlockEnd(subrecords, i + 1);
            var scdaIndex = FindFirstSubrecord(subrecords, "SCDA", i + 1, end);
            blockIndex++;
            if (scdaIndex >= 0)
            {
                scdaSeen.Add(scdaIndex);
            }

            results.Add(CreateRawSnapshot(
                target,
                relation,
                recordType,
                formId,
                editorId,
                blockIndex,
                i + 1,
                end,
                scdaIndex,
                subrecords,
                isBigEndianBytecode,
                origin,
                matchStrategy));
        }

        foreach (var (sub, index) in subrecords.Select((sub, index) => (sub, index)))
        {
            if (sub.Signature != "SCDA" || scdaSeen.Contains(index))
            {
                continue;
            }

            blockIndex++;
            var end = FindScriptBlockEnd(subrecords, index + 1);
            results.Add(CreateRawSnapshot(
                target,
                relation,
                recordType,
                formId,
                editorId,
                blockIndex,
                index + 1,
                end,
                index,
                subrecords,
                isBigEndianBytecode,
                origin,
                matchStrategy));
        }

        return results;
    }

    private static BlockSnapshot CreateRawSnapshot(
        string target,
        string relation,
        string recordType,
        uint formId,
        string editorId,
        int blockIndex,
        int blockStart,
        int blockEnd,
        int scdaIndex,
        IReadOnlyList<ParsedSubrecord> subrecords,
        bool isBigEndianBytecode,
        string origin,
        string matchStrategy)
    {
        return new BlockSnapshot(
            target,
            relation,
            recordType,
            formId,
            editorId,
            blockIndex,
            scdaIndex >= 0 ? subrecords[scdaIndex].Data : [],
            ReadFirstStringSubrecord(subrecords, "SCTX", blockStart, blockEnd),
            ReadScriptReferences(subrecords, blockStart, blockEnd),
            ReadScriptVariables(subrecords, blockStart, blockEnd),
            isBigEndianBytecode,
            origin,
            matchStrategy);
    }

    private static SourceLookup BuildSourceLookup(RecordCollection? sourceRecords, RecordCollection? masterRecords)
    {
        var lookup = new SourceLookup();
        AddCollectionToLookup(lookup, sourceRecords, "DMP");
        AddCollectionToLookup(lookup, masterRecords, "Master");
        return lookup;
    }

    private static void AddCollectionToLookup(SourceLookup lookup, RecordCollection? records, string origin)
    {
        if (records is null)
        {
            return;
        }

        foreach (var script in records.Scripts)
        {
            var snapshot = new BlockSnapshot(
                string.Empty,
                string.Empty,
                "SCPT",
                script.FormId,
                script.EditorId ?? string.Empty,
                1,
                script.CompiledData ?? [],
                script.SourceText ?? string.Empty,
                ToReferenceSlots(script.ReferencedObjects),
                script.Variables,
                script.IsBigEndian || script.FromRuntime,
                origin,
                "script");
            lookup.AddScript(script, snapshot);
        }

        foreach (var dialogue in records.Dialogues)
        {
            var snapshots = dialogue.ResultScripts.Count == 0
                ? new List<BlockSnapshot>()
                : dialogue.ResultScripts
                    .Select((script, index) => new BlockSnapshot(
                        string.Empty,
                        string.Empty,
                        "INFO",
                        dialogue.FormId,
                        dialogue.EditorId ?? string.Empty,
                        index + 1,
                        script.CompiledData ?? [],
                        script.SourceText ?? string.Empty,
                        ToReferenceSlots(script.ReferencedObjects),
                        [],
                        script.IsBigEndianBytecode,
                        origin,
                        "dialogue"))
                    .ToList();
            lookup.AddDialogue(dialogue, snapshots, origin);
        }

        foreach (var package in records.Packages)
        {
            var snapshots = new List<BlockSnapshot>();
            AddPackageEventSnapshots(package, package.OnBegin, snapshots, origin);
            AddPackageEventSnapshots(package, package.OnEnd, snapshots, origin);
            AddPackageEventSnapshots(package, package.OnChange, snapshots, origin);
            lookup.AddPackage(package, snapshots);
        }
    }

    private static void AddPackageEventSnapshots(
        PackageRecord package,
        PackageEventAction? action,
        List<BlockSnapshot> snapshots,
        string origin)
    {
        if (action is null)
        {
            return;
        }

        foreach (var script in action.Scripts)
        {
            snapshots.Add(new BlockSnapshot(
                string.Empty,
                string.Empty,
                "PACK",
                package.FormId,
                package.EditorId ?? string.Empty,
                snapshots.Count + 1,
                script.CompiledData ?? [],
                script.SourceText ?? string.Empty,
                ToReferenceSlots(script.ReferencedObjects),
                [],
                script.IsBigEndianBytecode,
                origin,
                action.Kind.ToString()));
        }
    }

    private static List<EsmScriptSourceVsEmittedRefRow> BuildSourceReferenceRows(
        IReadOnlyList<BlockSnapshot> emittedBlocks,
        EsmScriptDiagnosticsResult diagnostics,
        SourceLookup sourceLookup,
        IReadOnlyDictionary<uint, LabelInfo> sourceLabels,
        IReadOnlyDictionary<uint, LabelInfo> emittedLabels)
    {
        var rows = new List<EsmScriptSourceVsEmittedRefRow>();
        foreach (var emitted in emittedBlocks)
        {
            var match = FindSourceBlocks(emitted, diagnostics, sourceLookup);
            var source = match.Blocks.FirstOrDefault(b => b.BlockIndex == emitted.BlockIndex);
            if (source is null && emitted.RecordType != "INFO")
            {
                source = match.Blocks.FirstOrDefault();
            }

            var maxSlots = Math.Max(source?.References.Count ?? 0, emitted.References.Count);
            for (var i = 0; i < maxSlots; i++)
            {
                var sourceRef = source is not null && i < source.References.Count ? source.References[i] : null;
                var emittedRef = i < emitted.References.Count ? emitted.References[i] : null;
                var classification = ClassifyReference(sourceRef, emittedRef);
                rows.Add(new EsmScriptSourceVsEmittedRefRow(
                    emitted.Target,
                    emitted.RecordType,
                    emitted.FormId,
                    emitted.EditorId,
                    emitted.BlockIndex,
                    i + 1,
                    match.Strategy,
                    source?.Origin ?? string.Empty,
                    source?.FormId ?? 0,
                    sourceRef?.Kind ?? string.Empty,
                    sourceRef?.RawValue ?? 0,
                    ResolveReferenceLabel(sourceLabels, sourceRef),
                    emittedRef?.Kind ?? string.Empty,
                    emittedRef?.RawValue ?? 0,
                    ResolveReferenceLabel(emittedLabels, emittedRef),
                    classification));
            }
        }

        return rows;
    }

    private static List<EsmResultScriptProvenanceRow> BuildResultScriptRows(
        IReadOnlyList<BlockSnapshot> emittedBlocks,
        EsmScriptDiagnosticsResult diagnostics,
        SourceLookup sourceLookup)
    {
        var rows = new List<EsmResultScriptProvenanceRow>();
        var emittedByInfo = emittedBlocks
            .Where(b => b.RecordType == "INFO")
            .GroupBy(b => (b.Target, b.FormId))
            .ToList();

        foreach (var group in emittedByInfo)
        {
            var emitted = group.OrderBy(b => b.BlockIndex).ToList();
            var first = emitted[0];
            var match = FindSourceBlocks(first, diagnostics, sourceLookup);
            var source = match.Blocks.OrderBy(b => b.BlockIndex).ToList();
            rows.Add(new EsmResultScriptProvenanceRow(
                first.Target,
                first.FormId,
                source.FirstOrDefault()?.FormId ?? 0,
                match.Strategy,
                source.Count,
                emitted.Count,
                FormatHashes(source),
                FormatHashes(emitted),
                Truncate(string.Join(" | ", source.Select(s => s.SourceText).Where(s => !string.IsNullOrWhiteSpace(s))),
                    180),
                Truncate(string.Join(" | ", emitted.Select(s => s.SourceText).Where(s => !string.IsNullOrWhiteSpace(s))),
                    180),
                string.Join(' ', source.Select(s => s.References.Count.ToString(CultureInfo.InvariantCulture))),
                string.Join(' ', emitted.Select(s => s.References.Count.ToString(CultureInfo.InvariantCulture))),
                ClassifyResultScript(source, emitted)));
        }

        return rows;
    }

    private static List<EsmBytecodeEndianProbeRow> BuildEndianProbeRows(
        IReadOnlyList<BlockSnapshot> emittedBlocks,
        SourceLookup sourceLookup,
        EsmScriptDiagnosticsResult diagnostics)
    {
        var rows = new List<EsmBytecodeEndianProbeRow>();
        foreach (var emitted in emittedBlocks.Where(b => b.Scda.Length > 0))
        {
            rows.Add(BuildEndianProbeRow(emitted with { Origin = "Emitted" }));
            var match = FindSourceBlocks(emitted, diagnostics, sourceLookup);
            foreach (var source in match.Blocks
                         .Where(s => s.Scda.Length > 0 && s.BlockIndex == emitted.BlockIndex)
                         .Take(1))
            {
                rows.Add(BuildEndianProbeRow(source with
                {
                    Target = emitted.Target,
                    Relation = emitted.Relation,
                    Origin = source.Origin
                }));
            }
        }

        return rows;
    }

    private static EsmBytecodeEndianProbeRow BuildEndianProbeRow(BlockSnapshot block)
    {
        var leRefs = block.References.Select(r => r.Kind == "SCRV" ? 0x80000000u | r.RawValue : r.RawValue).ToList();
        var le = ScriptBytecodeAnalyzer.Analyze(block.Scda, false, block.Variables, leRefs, block.EditorId);
        var be = ScriptBytecodeAnalyzer.Analyze(block.Scda, true, block.Variables, leRefs, block.EditorId);
        return new EsmBytecodeEndianProbeRow(
            block.Target,
            block.Origin,
            block.RecordType,
            block.FormId,
            block.EditorId,
            block.BlockIndex,
            block.Scda.Length,
            FormatFirstBytes(block.Scda),
            FormatOpcode(block.Scda, false),
            FormatOpcode(block.Scda, true),
            le.WalkedToEnd,
            le.HasDiagnostics,
            le.Diagnostics,
            be.WalkedToEnd,
            be.HasDiagnostics,
            be.Diagnostics,
            ClassifyEndianProbe(le, be));
    }

    private static List<EsmTargetStateTraceRow> BuildStateTraceRows(
        EsmScriptDiagnosticsResult diagnostics,
        IReadOnlyDictionary<uint, ParsedMainRecord> generatedByFormId,
        IReadOnlyDictionary<uint, LabelInfo> labels)
    {
        var rows = new List<EsmTargetStateTraceRow>();
        foreach (var recordRow in diagnostics.Records)
        {
            if (!generatedByFormId.TryGetValue(recordRow.FormId, out var record))
            {
                continue;
            }

            foreach (var sub in record.Subrecords)
            {
                if (sub.Signature == "CTDA" && sub.Data.Length >= 28)
                {
                    var condition = CtdaParser.Decode(sub.Data, sub.BigEndian);
                    AddLinkedTrace(rows, recordRow, "condition-parameter", condition.Parameter1, labels,
                        $"CTDA fn=0x{condition.FunctionIndex:X} p1=0x{condition.Parameter1:X8} p2=0x{condition.Parameter2:X8} runOn={condition.RunOn} ref=0x{condition.Reference:X8}");
                    AddLinkedTrace(rows, recordRow, "condition-reference", condition.Reference, labels,
                        $"CTDA fn=0x{condition.FunctionIndex:X} reference");
                    continue;
                }

                if (sub.Data.Length < 4)
                {
                    if (sub.Signature is "PKED" or "PUID" or "PKAM" or "NEXT")
                    {
                        rows.Add(new EsmTargetStateTraceRow(
                            recordRow.Target,
                            "marker",
                            recordRow.Relation,
                            recordRow.RecordType,
                            recordRow.FormId,
                            recordRow.EditorId,
                            0,
                            string.Empty,
                            sub.Signature));
                    }

                    continue;
                }

                if (sub.Signature is "PKID" or "SCRI" or "NAME" or "PLDT" or "PTDT" or "PLD2" or "PTD2"
                    or "INAM" or "TNAM" or "CNAM" or "SCRO" or "SCRV" or "QSTI" or "TPIC" or "TCLT"
                    or "TCLF" or "TCFU" or "ANAM")
                {
                    AddLinkedTrace(rows, recordRow, sub.Signature.ToLowerInvariant(), sub.DataAsFormId, labels,
                        $"{sub.Signature}=0x{sub.DataAsFormId:X8}");
                }
            }
        }

        foreach (var block in diagnostics.ScriptBlocks)
        {
            rows.Add(new EsmTargetStateTraceRow(
                block.Target,
                "script-block",
                block.Relation,
                block.RecordType,
                block.FormId,
                block.EditorId,
                0,
                string.Empty,
                $"block={block.BlockIndex} scda={block.ScdaLength} order={block.OrderStatus} walk={block.WalkedToEnd} {block.SourceTextPreview}"));
        }

        return rows
            .OrderBy(r => r.Target, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.RecordType, StringComparer.Ordinal)
            .ThenBy(r => r.FormId)
            .ThenBy(r => r.Category, StringComparer.Ordinal)
            .ToList();
    }

    private static void AddLinkedTrace(
        List<EsmTargetStateTraceRow> rows,
        EsmScriptDiagnosticRecordRow recordRow,
        string category,
        uint linkedFormId,
        IReadOnlyDictionary<uint, LabelInfo> labels,
        string detail)
    {
        if (linkedFormId == 0 && category != "condition-parameter")
        {
            return;
        }

        rows.Add(new EsmTargetStateTraceRow(
            recordRow.Target,
            category,
            recordRow.Relation,
            recordRow.RecordType,
            recordRow.FormId,
            recordRow.EditorId,
            linkedFormId,
            ResolveLabel(labels, linkedFormId),
            detail));
    }

    private static SourceMatch FindSourceBlocks(
        BlockSnapshot emitted,
        EsmScriptDiagnosticsResult diagnostics,
        SourceLookup sourceLookup)
    {
        if (emitted.RecordType == "SCPT")
        {
            if (!string.IsNullOrWhiteSpace(emitted.EditorId) &&
                sourceLookup.ScriptsByEditorId.TryGetValue(emitted.EditorId, out var byEditorId))
            {
                return new SourceMatch("script-editorid", [byEditorId]);
            }

            if (sourceLookup.ScriptsByFormId.TryGetValue(emitted.FormId, out var byFormId))
            {
                return new SourceMatch("script-formid", [byFormId]);
            }
        }

        if (emitted.RecordType == "INFO")
        {
            var dialogueRow = diagnostics.Dialogue.FirstOrDefault(d =>
                d.Target.Equals(emitted.Target, StringComparison.OrdinalIgnoreCase) &&
                d.InfoFormId == emitted.FormId);
            if (sourceLookup.DialogueByFormId.TryGetValue(emitted.FormId, out var exact))
            {
                return new SourceMatch("info-formid", exact);
            }

            var responseKey = NormalizeText(dialogueRow?.ResponsePreview);
            if (responseKey.Length > 0 &&
                sourceLookup.DialogueByResponse.TryGetValue(responseKey, out var byResponse))
            {
                return new SourceMatch("info-response-text", byResponse);
            }
        }

        if (emitted.RecordType == "PACK")
        {
            if (sourceLookup.PackageByFormId.TryGetValue(emitted.FormId, out var exactPackage))
            {
                return new SourceMatch("pack-formid", exactPackage);
            }

            if (!string.IsNullOrWhiteSpace(emitted.EditorId) &&
                sourceLookup.PackageByEditorId.TryGetValue(emitted.EditorId, out var byEditorIdPackage))
            {
                return new SourceMatch("pack-editorid", byEditorIdPackage);
            }
        }

        return new SourceMatch("no-source-match", []);
    }

    private static string ClassifyReference(ScriptReferenceSlot? source, ScriptReferenceSlot? emitted)
    {
        if (source is null)
        {
            return emitted is null ? "Resolved" : "ExtraEmittedSlot";
        }

        if (emitted is null)
        {
            return "MissingSourceSlot";
        }

        if (source.Kind == "SCRV" && emitted.Kind == "SCRO")
        {
            return "SourceScrvEmittedAsScro";
        }

        if (source.Kind == "SCRO" && source.RawValue == 0)
        {
            return "SourceNull";
        }

        if (source.Kind == "SCRO" && source.RawValue != 0 &&
            emitted.Kind == "SCRO" && emitted.RawValue == 0)
        {
            return "ConverterNulledNonZeroSource";
        }

        return "Resolved";
    }

    private static string ClassifyResultScript(
        IReadOnlyList<BlockSnapshot> source,
        IReadOnlyList<BlockSnapshot> emitted)
    {
        if (source.Count == 0)
        {
            return "SourceMissing";
        }

        var sourceWithContent = source.Where(HasScriptContent).ToList();
        var emittedWithContent = emitted.Where(HasScriptContent).ToList();
        if (sourceWithContent.Count == 0)
        {
            return "SourceEmpty";
        }

        if (emittedWithContent.Count == 0)
        {
            return emitted.Count > 0 ? "EmittedPlaceholderOnly" : "SkippedOverrideScript";
        }

        return FormatHashes(sourceWithContent) == FormatHashes(emittedWithContent) &&
               string.Join(' ', sourceWithContent.Select(s => s.References.Count)) ==
               string.Join(' ', emittedWithContent.Select(s => s.References.Count))
            ? "Preserved"
            : "ContentMismatch";
    }

    private static bool HasScriptContent(BlockSnapshot block)
    {
        return block.Scda.Length > 0 ||
               !string.IsNullOrWhiteSpace(block.SourceText) ||
               block.References.Count > 0;
    }

    private static string ClassifyEndianProbe(ScriptBytecodeAnalysis littleEndian, ScriptBytecodeAnalysis bigEndian)
    {
        var leClean = littleEndian.WalkedToEnd && !littleEndian.HasDiagnostics;
        var beClean = bigEndian.WalkedToEnd && !bigEndian.HasDiagnostics;
        if (leClean)
        {
            return "WalksLe";
        }

        if (beClean)
        {
            return "WouldSwapFix";
        }

        return (littleEndian.Diagnostics + " " + bigEndian.Diagnostics)
            .Contains("Unknown opcode", StringComparison.Ordinal)
            ? "WalkerGap"
            : "CorruptOrTruncated";
    }

    private static Dictionary<uint, LabelInfo> BuildGeneratedLabelIndex(IReadOnlyList<ParsedMainRecord> records)
    {
        return records
            .GroupBy(r => r.Header.FormId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var record = g.First();
                    return new LabelInfo(
                        record.Header.Signature,
                        ReadFirstStringSubrecord(record, "EDID"),
                        ReadFirstStringSubrecord(record, "FULL"));
                });
    }

    private static Dictionary<uint, LabelInfo> BuildCollectionLabelIndex(
        RecordCollection? primary,
        RecordCollection? fallback)
    {
        var labels = new Dictionary<uint, LabelInfo>();
        AddCollectionLabels(labels, fallback);
        AddCollectionLabels(labels, primary);
        return labels;
    }

    private static void AddCollectionLabels(Dictionary<uint, LabelInfo> labels, RecordCollection? records)
    {
        if (records is null)
        {
            return;
        }

        foreach (var (formId, editorId) in records.FormIdToEditorId)
        {
            labels[formId] = labels.TryGetValue(formId, out var existing)
                ? existing with { EditorId = editorId }
                : new LabelInfo(string.Empty, editorId, string.Empty);
        }

        foreach (var (formId, fullName) in records.FormIdToDisplayName)
        {
            labels[formId] = labels.TryGetValue(formId, out var existing)
                ? existing with { FullName = fullName }
                : new LabelInfo(string.Empty, string.Empty, fullName);
        }
    }

    private static Dictionary<uint, LabelInfo> MergeLabels(
        IReadOnlyDictionary<uint, LabelInfo> primary,
        IReadOnlyDictionary<uint, LabelInfo> fallback)
    {
        var labels = new Dictionary<uint, LabelInfo>(fallback);
        foreach (var (formId, label) in primary)
        {
            labels[formId] = label;
        }

        return labels;
    }

    private static IReadOnlyList<ScriptReferenceSlot> ToReferenceSlots(IEnumerable<uint> referencedObjects)
    {
        return referencedObjects
            .Select(r => (r & 0x80000000) != 0
                ? new ScriptReferenceSlot("SCRV", r & 0x7FFFFFFF)
                : new ScriptReferenceSlot("SCRO", r))
            .ToList();
    }

    private static int FindScriptBlockEnd(IReadOnlyList<ParsedSubrecord> subrecords, int start)
    {
        for (var i = start; i < subrecords.Count; i++)
        {
            if (ScriptBoundarySubrecords.Contains(subrecords[i].Signature))
            {
                return i;
            }
        }

        return subrecords.Count;
    }

    private static int FindFirstSubrecord(
        IReadOnlyList<ParsedSubrecord> subrecords,
        string signature,
        int start,
        int end)
    {
        for (var i = start; i < end; i++)
        {
            if (subrecords[i].Signature == signature)
            {
                return i;
            }
        }

        return -1;
    }

    private static List<ScriptVariableInfo> ReadScriptVariables(
        IReadOnlyList<ParsedSubrecord> subrecords,
        int start,
        int end)
    {
        var variables = new List<ScriptVariableInfo>();
        uint? pendingIndex = null;
        byte pendingType = 0;
        for (var i = start; i < end; i++)
        {
            var sub = subrecords[i];
            if (sub.Signature == "SLSD" && sub.Data.Length >= 4)
            {
                pendingIndex = BinaryPrimitives.ReadUInt32LittleEndian(sub.Data.AsSpan(0, 4));
                pendingType = sub.Data.Length > 16 && sub.Data[16] != 0 ? (byte)1 : (byte)0;
            }
            else if (sub.Signature == "SCVR" && pendingIndex.HasValue)
            {
                variables.Add(new ScriptVariableInfo(pendingIndex.Value, sub.DataAsString, pendingType));
                pendingIndex = null;
                pendingType = 0;
            }
        }

        if (pendingIndex.HasValue)
        {
            variables.Add(new ScriptVariableInfo(pendingIndex.Value, null, pendingType));
        }

        return variables;
    }

    private static List<ScriptReferenceSlot> ReadScriptReferences(
        IReadOnlyList<ParsedSubrecord> subrecords,
        int start,
        int end)
    {
        var references = new List<ScriptReferenceSlot>();
        for (var i = start; i < end; i++)
        {
            var sub = subrecords[i];
            if (sub.Data.Length < 4)
            {
                continue;
            }

            if (sub.Signature == "SCRO")
            {
                references.Add(new ScriptReferenceSlot("SCRO", BinaryPrimitives.ReadUInt32LittleEndian(sub.Data)));
            }
            else if (sub.Signature == "SCRV")
            {
                references.Add(new ScriptReferenceSlot("SCRV", BinaryPrimitives.ReadUInt32LittleEndian(sub.Data)));
            }
        }

        return references;
    }

    private static string ReadFirstStringSubrecord(ParsedMainRecord record, string signature)
    {
        return record.Subrecords.FirstOrDefault(s => s.Signature == signature)?.DataAsString ?? string.Empty;
    }

    private static string ReadFirstStringSubrecord(
        IReadOnlyList<ParsedSubrecord> subrecords,
        string signature,
        int start,
        int end)
    {
        for (var i = start; i < end; i++)
        {
            if (subrecords[i].Signature == signature)
            {
                return subrecords[i].DataAsString;
            }
        }

        return string.Empty;
    }

    private static string ResolveReferenceLabel(
        IReadOnlyDictionary<uint, LabelInfo> labels,
        ScriptReferenceSlot? reference)
    {
        if (reference is null || reference.Kind == "SCRV")
        {
            return reference is null ? string.Empty : $"SCRV[{reference.RawValue}]";
        }

        return ResolveLabel(labels, reference.RawValue);
    }

    private static string ResolveLabel(IReadOnlyDictionary<uint, LabelInfo> labels, uint formId)
    {
        if (formId == 0 || !labels.TryGetValue(formId, out var label))
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(label.EditorId))
        {
            return label.EditorId;
        }

        if (!string.IsNullOrWhiteSpace(label.FullName))
        {
            return label.FullName;
        }

        return label.RecordType;
    }

    private static string FormatHashes(IReadOnlyList<BlockSnapshot> blocks)
    {
        return string.Join(' ', blocks.Select(b => b.Scda.Length == 0 ? "empty" : ShortSha256(b.Scda)));
    }

    private static string ShortSha256(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    private static string FormatFirstBytes(byte[] bytes)
    {
        return bytes.Length == 0
            ? string.Empty
            : Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 8))).ToLowerInvariant();
    }

    private static string FormatOpcode(byte[] bytes, bool bigEndian)
    {
        if (bytes.Length < 2)
        {
            return string.Empty;
        }

        var opcode = bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(0, 2))
            : BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0, 2));
        return $"0x{opcode:X4}";
    }

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString();
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value ?? string.Empty;
        }

        return value[..Math.Max(0, maxLength - 3)] + "...";
    }

    private static string BuildSourceVsEmittedRefsCsv(EsmScriptProvenanceReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("target,record_type,emitted_form_id,emitted_editor_id,block_index,slot_index,match_strategy,source_origin,source_form_id,source_kind,source_raw_value,source_label,emitted_kind,emitted_raw_value,emitted_label,classification");
        foreach (var row in report.SourceVsEmittedRefs)
        {
            sb.AppendLine(string.Join(',',
                Csv(row.Target),
                Csv(row.RecordType),
                Csv($"0x{row.EmittedFormId:X8}"),
                Csv(row.EmittedEditorId),
                row.BlockIndex,
                row.SlotIndex,
                Csv(row.MatchStrategy),
                Csv(row.SourceOrigin),
                Csv(row.SourceFormId == 0 ? string.Empty : $"0x{row.SourceFormId:X8}"),
                Csv(row.SourceKind),
                Csv(row.SourceRawValue == 0 ? string.Empty : $"0x{row.SourceRawValue:X8}"),
                Csv(row.SourceLabel),
                Csv(row.EmittedKind),
                Csv(row.EmittedRawValue == 0 ? string.Empty : $"0x{row.EmittedRawValue:X8}"),
                Csv(row.EmittedLabel),
                Csv(row.Classification)));
        }

        return sb.ToString();
    }

    private static string BuildResultScriptProvenanceCsv(EsmScriptProvenanceReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("target,emitted_info_form_id,source_info_form_id,match_strategy,source_block_count,emitted_block_count,source_scda_hashes,emitted_scda_hashes,source_sctx_preview,emitted_sctx_preview,source_reference_counts,emitted_reference_counts,classification");
        foreach (var row in report.ResultScripts)
        {
            sb.AppendLine(string.Join(',',
                Csv(row.Target),
                Csv($"0x{row.EmittedInfoFormId:X8}"),
                Csv(row.SourceInfoFormId == 0 ? string.Empty : $"0x{row.SourceInfoFormId:X8}"),
                Csv(row.MatchStrategy),
                row.SourceBlockCount,
                row.EmittedBlockCount,
                Csv(row.SourceScdaHashes),
                Csv(row.EmittedScdaHashes),
                Csv(row.SourceSctxPreview),
                Csv(row.EmittedSctxPreview),
                Csv(row.SourceReferenceCounts),
                Csv(row.EmittedReferenceCounts),
                Csv(row.Classification)));
        }

        return sb.ToString();
    }

    private static string BuildBytecodeEndianProbeCsv(EsmScriptProvenanceReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("target,origin,record_type,form_id,editor_id,block_index,byte_length,first_bytes,little_endian_opcode,big_endian_opcode,little_endian_walked_to_end,little_endian_has_diagnostics,little_endian_diagnostics,big_endian_walked_to_end,big_endian_has_diagnostics,big_endian_diagnostics,classification");
        foreach (var row in report.BytecodeEndianProbes)
        {
            sb.AppendLine(string.Join(',',
                Csv(row.Target),
                Csv(row.Origin),
                Csv(row.RecordType),
                Csv($"0x{row.FormId:X8}"),
                Csv(row.EditorId),
                row.BlockIndex,
                row.ByteLength,
                Csv(row.FirstBytes),
                Csv(row.LittleEndianOpcode),
                Csv(row.BigEndianOpcode),
                row.LittleEndianWalkedToEnd,
                row.LittleEndianHasDiagnostics,
                Csv(row.LittleEndianDiagnostics),
                row.BigEndianWalkedToEnd,
                row.BigEndianHasDiagnostics,
                Csv(row.BigEndianDiagnostics),
                Csv(row.Classification)));
        }

        return sb.ToString();
    }

    private static string BuildStateTraceCsv(EsmScriptProvenanceReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("target,category,relation,record_type,form_id,editor_id,linked_form_id,linked_label,detail");
        foreach (var row in report.StateTrace)
        {
            sb.AppendLine(string.Join(',',
                Csv(row.Target),
                Csv(row.Category),
                Csv(row.Relation),
                Csv(row.RecordType),
                Csv($"0x{row.FormId:X8}"),
                Csv(row.EditorId),
                Csv(row.LinkedFormId == 0 ? string.Empty : $"0x{row.LinkedFormId:X8}"),
                Csv(row.LinkedLabel),
                Csv(row.Detail)));
        }

        return sb.ToString();
    }

    private static string Csv(object? value)
    {
        var text = value?.ToString() ?? string.Empty;
        return text.Contains('"') || text.Contains(',') || text.Contains('\n') || text.Contains('\r')
            ? $"\"{text.Replace("\"", "\"\"")}\""
            : text;
    }

    private sealed class SourceLookup
    {
        public Dictionary<uint, BlockSnapshot> ScriptsByFormId { get; } = [];
        public Dictionary<string, BlockSnapshot> ScriptsByEditorId { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<uint, List<BlockSnapshot>> DialogueByFormId { get; } = [];
        public Dictionary<string, List<BlockSnapshot>> DialogueByResponse { get; } = new(StringComparer.Ordinal);
        public Dictionary<uint, List<BlockSnapshot>> PackageByFormId { get; } = [];
        public Dictionary<string, List<BlockSnapshot>> PackageByEditorId { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void AddScript(ScriptRecord script, BlockSnapshot snapshot)
        {
            ScriptsByFormId.TryAdd(script.FormId, snapshot);
            if (!string.IsNullOrWhiteSpace(script.EditorId))
            {
                ScriptsByEditorId.TryAdd(script.EditorId, snapshot);
            }
        }

        public void AddDialogue(DialogueRecord dialogue, List<BlockSnapshot> snapshots, string origin)
        {
            DialogueByFormId.TryAdd(dialogue.FormId, snapshots);
            var firstResponse = dialogue.Responses.FirstOrDefault()?.Text;
            var responseKey = NormalizeText(firstResponse);
            if (responseKey.Length > 0)
            {
                DialogueByResponse.TryAdd(responseKey, snapshots);
            }
        }

        public void AddPackage(PackageRecord package, List<BlockSnapshot> snapshots)
        {
            PackageByFormId.TryAdd(package.FormId, snapshots);
            if (!string.IsNullOrWhiteSpace(package.EditorId))
            {
                PackageByEditorId.TryAdd(package.EditorId, snapshots);
            }
        }
    }

    private sealed record SourceMatch(string Strategy, IReadOnlyList<BlockSnapshot> Blocks);

    private sealed record BlockSnapshot(
        string Target,
        string Relation,
        string RecordType,
        uint FormId,
        string EditorId,
        int BlockIndex,
        byte[] Scda,
        string SourceText,
        IReadOnlyList<ScriptReferenceSlot> References,
        IReadOnlyList<ScriptVariableInfo> Variables,
        bool IsBigEndianBytecode,
        string Origin,
        string MatchStrategy);

    private sealed record ScriptReferenceSlot(string Kind, uint RawValue);

    private sealed record LabelInfo(string RecordType, string EditorId, string FullName);
}
