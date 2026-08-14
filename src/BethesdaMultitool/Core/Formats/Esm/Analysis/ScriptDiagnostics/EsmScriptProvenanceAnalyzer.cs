using System.Globalization;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using static BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics.EsmScriptProvenanceClassifier;
using ScriptReferenceSlot = BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics.EsmScriptBlockReader.ScriptReferenceSlot;

namespace BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics;

/// <summary>Traces how a generated ESP's scripts, dialogue, and references originate from source/master records.</summary>
public static class EsmScriptProvenanceAnalyzer
{
    /// <summary>Parses the generated ESP at the given path and builds its script-provenance report.</summary>
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

    /// <summary>Builds the script-provenance report from already-parsed generated records and the source/master collections.</summary>
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

    /// <summary>Writes the provenance report as a set of CSV files into the given output directory.</summary>
    public static void WriteReport(EsmScriptProvenanceReport report, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(
            Path.Combine(outputDirectory, "script_source_vs_emitted_refs.csv"),
            EsmScriptProvenanceCsvWriter.BuildSourceVsEmittedRefsCsv(report),
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(outputDirectory, "result_script_provenance.csv"),
            EsmScriptProvenanceCsvWriter.BuildResultScriptProvenanceCsv(report),
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(outputDirectory, "bytecode_endian_probe.csv"),
            EsmScriptProvenanceCsvWriter.BuildBytecodeEndianProbeCsv(report),
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(outputDirectory, "target_state_trace.csv"),
            EsmScriptProvenanceCsvWriter.BuildStateTraceCsv(report),
            Encoding.UTF8);
    }

    private static List<BlockSnapshot> BuildEmittedSnapshots(
        Dictionary<uint, ParsedMainRecord> generatedByFormId,
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
        List<ParsedSubrecord> subrecords,
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

            var end = EsmScriptBlockReader.FindScriptBlockEnd(subrecords, i + 1);
            var scdaIndex = EsmScriptBlockReader.FindFirstSubrecord(subrecords, "SCDA", i + 1, end);
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
            var end = EsmScriptBlockReader.FindScriptBlockEnd(subrecords, index + 1);
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
        List<ParsedSubrecord> subrecords,
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
            EsmScriptBlockReader.ReadFirstStringSubrecord(subrecords, "SCTX", blockStart, blockEnd),
            EsmScriptBlockReader.ReadScriptReferences(subrecords, blockStart, blockEnd),
            EsmScriptBlockReader.ReadScriptVariables(subrecords, blockStart, blockEnd),
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

        var labels = BuildSimpleLabelIndex(records);

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
                ? [
                    new BlockSnapshot(
                        string.Empty,
                        string.Empty,
                        "INFO",
                        dialogue.FormId,
                        dialogue.EditorId ?? string.Empty,
                        0,
                        [],
                        string.Empty,
                        [],
                        [],
                        dialogue.IsBigEndian,
                        origin,
                        "dialogue-empty")
                ]
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
            lookup.AddDialogue(dialogue, snapshots, labels);
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
                source = match.Blocks.Count > 0 ? match.Blocks[0] : null;
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
        Dictionary<uint, ParsedMainRecord> generatedByFormId,
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
                if (sub.Signature == "CTDA")
                {
                    var layoutStatus = CtdaParser.GetLayoutStatus(diagnostics.Game, sub.Data.Length);
                    if (layoutStatus != "valid" ||
                        !CtdaParser.TryDecode(sub.Data, sub.BigEndian, out var condition, out var physical))
                    {
                        rows.Add(new EsmTargetStateTraceRow(
                            recordRow.Target,
                            "condition-invalid",
                            recordRow.Relation,
                            recordRow.RecordType,
                            recordRow.FormId,
                            recordRow.EditorId,
                            0,
                            string.Empty,
                            $"CTDA length={sub.Data.Length} status={layoutStatus} " +
                            $"raw={Convert.ToHexString(sub.Data)}"));
                        continue;
                    }

                    var referenceSlotIsSemantic = physical.ReferenceStorage.HasValue &&
                                                  DialogueConditionReferencePolicy.IsSemanticReferenceSlot(
                                                      condition, diagnostics.Game);
                    var hasSemanticReference = referenceSlotIsSemantic && condition.Reference != 0;
                    var reference = condition.Reference;
                    string referenceDetail;
                    if (referenceSlotIsSemantic)
                    {
                        referenceDetail = $"ref=0x{reference:X8}";
                    }
                    else if (physical.ReferenceStorage is { } referenceStorage)
                    {
                        referenceDetail = $"reference_storage=0x{referenceStorage:X8}";
                    }
                    else
                    {
                        referenceDetail = "reference_storage=absent";
                    }
                    var runOnDetail = physical.RunOn is { } runOn ? runOn.ToString() : "absent";
                    var parameter3Detail = physical.Parameter3 is { } parameter3
                        ? parameter3.ToString()
                        : "absent";
                    var conditionDetail =
                        $"CTDA fn=0x{condition.FunctionIndex:X} p1=0x{condition.Parameter1:X8} " +
                        $"p2=0x{condition.Parameter2:X8} runOn={runOnDetail} {referenceDetail} " +
                        $"p3={parameter3Detail}";
                    rows.Add(new EsmTargetStateTraceRow(
                        recordRow.Target,
                        "condition-raw",
                        recordRow.Relation,
                        recordRow.RecordType,
                        recordRow.FormId,
                        recordRow.EditorId,
                        0,
                        string.Empty,
                        conditionDetail));

                    if (condition.Parameter1 != 0 &&
                        EsmScriptDiagnosticsResolvers.IsFormIdConditionParameter(
                            diagnostics.Game,
                            condition.FunctionIndex,
                            0,
                            condition.Type,
                            condition.RunOn,
                            condition.Parameter1))
                    {
                        AddLinkedTrace(
                            rows,
                            recordRow,
                            "condition-parameter",
                            condition.Parameter1,
                            labels,
                            $"{conditionDetail} linked_slot=p1");
                    }

                    if (condition.Parameter2 != 0 &&
                        EsmScriptDiagnosticsResolvers.IsFormIdConditionParameter(
                            diagnostics.Game,
                            condition.FunctionIndex,
                            1,
                            condition.Type,
                            condition.RunOn,
                            condition.Parameter1))
                    {
                        AddLinkedTrace(
                            rows,
                            recordRow,
                            "condition-parameter",
                            condition.Parameter2,
                            labels,
                            $"{conditionDetail} linked_slot=p2");
                    }

                    if (hasSemanticReference)
                    {
                        AddLinkedTrace(rows, recordRow, "condition-reference", reference, labels,
                            $"CTDA fn=0x{condition.FunctionIndex:X} reference");
                    }

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
            if (dialogueRow is not null && responseKey.Length > 0)
            {
                var compositeKey = new InfoCompositeKey(
                    dialogueRow.QuestFormId,
                    dialogueRow.TopicFormId,
                    dialogueRow.SpeakerFormId,
                    responseKey);
                if (sourceLookup.DialogueByComposite.TryGetValue(compositeKey, out var byComposite))
                {
                    return new SourceMatch("info-quest-topic-speaker-response", byComposite);
                }

                var topicSpeakerKey = compositeKey with { QuestFormId = 0 };
                if (sourceLookup.DialogueByComposite.TryGetValue(topicSpeakerKey, out var byTopicSpeaker))
                {
                    return new SourceMatch("info-topic-speaker-response", byTopicSpeaker);
                }

                var questSpeakerKey = compositeKey with { TopicFormId = 0 };
                if (sourceLookup.DialogueByComposite.TryGetValue(questSpeakerKey, out var byQuestSpeaker))
                {
                    return new SourceMatch("info-quest-speaker-response", byQuestSpeaker);
                }
            }

            if (responseKey.Length > 0 &&
                sourceLookup.DialogueByResponse.TryGetValue(responseKey, out var byResponse))
            {
                return new SourceMatch("info-response-text", byResponse);
            }

            if (dialogueRow is not null)
            {
                var topicKey = new InfoTopicKey(
                    dialogueRow.QuestFormId,
                    dialogueRow.TopicFormId,
                    dialogueRow.SpeakerFormId);
                if (sourceLookup.DialogueByTopic.TryGetValue(topicKey, out var byTopic))
                {
                    return new SourceMatch("info-quest-topic-speaker", byTopic);
                }

                var topicSpeakerKey = topicKey with { QuestFormId = 0 };
                if (sourceLookup.DialogueByTopic.TryGetValue(topicSpeakerKey, out var byTopicSpeaker))
                {
                    return new SourceMatch("info-topic-speaker", byTopicSpeaker);
                }

                var topicLabelKey = NormalizeText(dialogueRow.TopicLabel);
                if (topicLabelKey.Length > 0 &&
                    sourceLookup.DialogueByTopicLabel.TryGetValue(topicLabelKey, out var byTopicLabel))
                {
                    return new SourceMatch("info-topic-label", byTopicLabel);
                }
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

    private static Dictionary<uint, string> BuildSimpleLabelIndex(RecordCollection records)
    {
        var labels = new Dictionary<uint, string>();
        foreach (var (formId, fullName) in records.FormIdToDisplayName)
        {
            labels[formId] = fullName;
        }

        foreach (var (formId, editorId) in records.FormIdToEditorId)
        {
            labels[formId] = editorId;
        }

        return labels;
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

    private static List<ScriptReferenceSlot> ToReferenceSlots(IEnumerable<uint> referencedObjects)
    {
        return referencedObjects
            .Select(r => (r & 0x80000000) != 0
                ? new ScriptReferenceSlot("SCRV", r & 0x7FFFFFFF)
                : new ScriptReferenceSlot("SCRO", r))
            .ToList();
    }

    private static string ReadFirstStringSubrecord(ParsedMainRecord record, string signature)
    {
        return record.Subrecords.FirstOrDefault(s => s.Signature == signature)?.DataAsString ?? string.Empty;
    }

    private sealed class SourceLookup
    {
        public Dictionary<uint, BlockSnapshot> ScriptsByFormId { get; } = [];
        public Dictionary<string, BlockSnapshot> ScriptsByEditorId { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<uint, List<BlockSnapshot>> DialogueByFormId { get; } = [];
        public Dictionary<string, List<BlockSnapshot>> DialogueByResponse { get; } = new(StringComparer.Ordinal);
        public Dictionary<InfoCompositeKey, List<BlockSnapshot>> DialogueByComposite { get; } = [];
        public Dictionary<InfoTopicKey, List<BlockSnapshot>> DialogueByTopic { get; } = [];
        public Dictionary<string, List<BlockSnapshot>> DialogueByTopicLabel { get; } = new(StringComparer.Ordinal);
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

        public void AddDialogue(
            DialogueRecord dialogue,
            List<BlockSnapshot> snapshots,
            Dictionary<uint, string> labels)
        {
            DialogueByFormId.TryAdd(dialogue.FormId, snapshots);
            AddTopic(dialogue.QuestFormId ?? 0, dialogue.TopicFormId ?? 0, dialogue.SpeakerFormId ?? 0,
                snapshots);
            AddTopic(0, dialogue.TopicFormId ?? 0, dialogue.SpeakerFormId ?? 0, snapshots);
            if (dialogue.TopicFormId.HasValue &&
                labels.TryGetValue(dialogue.TopicFormId.Value, out var topicLabel))
            {
                var topicLabelKey = NormalizeText(topicLabel);
                if (topicLabelKey.Length > 0)
                {
                    DialogueByTopicLabel.TryAdd(topicLabelKey, snapshots);
                }
            }

            var firstResponse = dialogue.Responses.FirstOrDefault()?.Text;
            var responseKey = NormalizeText(firstResponse);
            if (responseKey.Length > 0)
            {
                DialogueByResponse.TryAdd(responseKey, snapshots);
                AddComposite(dialogue.QuestFormId ?? 0, dialogue.TopicFormId ?? 0, dialogue.SpeakerFormId ?? 0,
                    responseKey, snapshots);
                AddComposite(0, dialogue.TopicFormId ?? 0, dialogue.SpeakerFormId ?? 0, responseKey, snapshots);
                AddComposite(dialogue.QuestFormId ?? 0, 0, dialogue.SpeakerFormId ?? 0, responseKey, snapshots);
            }
        }

        private void AddComposite(
            uint questFormId,
            uint topicFormId,
            uint speakerFormId,
            string responseKey,
            List<BlockSnapshot> snapshots)
        {
            if ((topicFormId == 0 && questFormId == 0) || speakerFormId == 0 || responseKey.Length == 0)
            {
                return;
            }

            DialogueByComposite.TryAdd(
                new InfoCompositeKey(questFormId, topicFormId, speakerFormId, responseKey),
                snapshots);
        }

        private void AddTopic(
            uint questFormId,
            uint topicFormId,
            uint speakerFormId,
            List<BlockSnapshot> snapshots)
        {
            if (topicFormId == 0 || speakerFormId == 0)
            {
                return;
            }

            DialogueByTopic.TryAdd(new InfoTopicKey(questFormId, topicFormId, speakerFormId), snapshots);
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

    private readonly record struct InfoCompositeKey(
        uint QuestFormId,
        uint TopicFormId,
        uint SpeakerFormId,
        string ResponseKey);

    private readonly record struct InfoTopicKey(
        uint QuestFormId,
        uint TopicFormId,
        uint SpeakerFormId);
}

