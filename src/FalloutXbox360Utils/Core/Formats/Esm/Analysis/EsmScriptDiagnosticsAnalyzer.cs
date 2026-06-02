using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;
using FalloutXbox360Utils.Core.Formats.Esm.Script;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils.Core.Formats.Esm.Analysis;

public sealed record EsmScriptDiagnosticsResult(
    string SourcePath,
    IReadOnlyList<string> Targets,
    IReadOnlyList<EsmScriptDiagnosticTargetMatchRow> TargetMatches,
    IReadOnlyList<EsmScriptDiagnosticRecordRow> Records,
    IReadOnlyList<EsmScriptDiagnosticDialogueRow> Dialogue,
    IReadOnlyList<EsmScriptDialogueAuditRow> DialogueAudit,
    IReadOnlyList<EsmScriptConditionAuditRow> Conditions,
    IReadOnlyList<EsmScriptDiagnosticBlockRow> ScriptBlocks,
    IReadOnlyList<EsmScriptDiagnosticReferenceRow> ScriptReferences);

public sealed record EsmScriptDiagnosticTargetMatchRow(
    string Target,
    string RecordType,
    uint FormId,
    string EditorId,
    string FullName,
    string MatchReason);

public sealed record EsmScriptDiagnosticRecordRow(
    string Target,
    string Relation,
    string RecordType,
    uint FormId,
    string EditorId,
    string FullName,
    string InterestingSubrecords);

public sealed record EsmScriptDiagnosticDialogueRow(
    string Target,
    uint InfoFormId,
    string InfoEditorId,
    uint TopicFormId,
    string TopicLabel,
    uint QuestFormId,
    uint SpeakerFormId,
    uint PreviousInfo,
    string LinkToTopics,
    string LinkFromTopics,
    string AddTopics,
    string FollowUpInfos,
    string InfoFlags,
    int ResponseCount,
    bool HasResultScript,
    string ResponsePreview);

public sealed record EsmScriptDialogueAuditRow(
    string Target,
    uint InfoFormId,
    uint TopicFormId,
    string TopicLabel,
    uint QuestFormId,
    uint SpeakerFormId,
    string RootClassification,
    bool HasIncomingTopicEdge,
    bool HasExplicitRootLink,
    bool IsTerminalReturnCandidate,
    bool HasGoodbyeForSpeakerQuest,
    string RawTcltBytes,
    string LinkToTopics,
    string FollowUpInfos,
    string ResponsePreview);

public sealed record EsmScriptConditionAuditRow(
    string Target,
    string Relation,
    string RecordType,
    uint FormId,
    string EditorId,
    int ConditionIndex,
    string FunctionName,
    ushort FunctionIndex,
    byte Type,
    float ComparisonValue,
    uint Parameter1,
    string Parameter1Label,
    uint Parameter2,
    string Parameter2Label,
    uint RunOn,
    uint Reference,
    string ReferenceLabel,
    string RawBytes);

public sealed record EsmScriptDiagnosticBlockRow(
    string Target,
    string Relation,
    string RecordType,
    uint FormId,
    string EditorId,
    int BlockIndex,
    string SubrecordOrder,
    string OrderStatus,
    int ScdaLength,
    uint? SchrCompiledSize,
    uint? SchrReferenceCount,
    int ActualReferenceSlots,
    bool CompiledSizeMatches,
    bool RefCountMatches,
    bool WalkedToEnd,
    bool HasDiagnostics,
    string Diagnostics,
    string SourceTextPreview);

public sealed record EsmScriptDiagnosticReferenceRow(
    string Target,
    string ParentRecordType,
    uint ParentFormId,
    int BlockIndex,
    int SlotIndex,
    string ReferenceKind,
    uint RawValue,
    uint ResolvedFormId,
    string Status,
    string ResolvedRecordType,
    string ResolvedEditorId,
    string ResolvedFullName);

public static class EsmScriptDiagnosticsAnalyzer
{
    private static readonly HashSet<string> ActorRecordTypes = new(StringComparer.Ordinal)
    {
        "NPC_", "CREA"
    };

    private static readonly HashSet<string> PlacedActorRecordTypes = new(StringComparer.Ordinal)
    {
        "ACHR", "ACRE"
    };

    public static EsmScriptDiagnosticsResult AnalyzeFile(
        string path,
        IReadOnlyList<string> targets,
        IReadOnlySet<uint>? explicitRecordFormIds = null)
    {
        var data = File.ReadAllBytes(path);
        var records = EsmParser.EnumerateRecordsWithGrups(data).Records;
        return AnalyzeRecords(path, records, targets, explicitRecordFormIds);
    }

    public static EsmScriptDiagnosticsResult AnalyzeRecords(
        string sourcePath,
        IReadOnlyList<ParsedMainRecord> records,
        IReadOnlyList<string> targets,
        IReadOnlySet<uint>? explicitRecordFormIds = null)
    {
        var normalizedTargets = targets
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var index = BuildFormIdIndex(records);
        var validFormIds = records.Select(r => r.Header.FormId).ToHashSet();
        var byFormId = records
            .GroupBy(r => r.Header.FormId)
            .ToDictionary(g => g.Key, g => g.First());

        var targetMatches = new List<EsmScriptDiagnosticTargetMatchRow>();
        var recordRelations = new Dictionary<(string Target, string RecordType, uint FormId), HashSet<string>>();
        var actorIdsByTarget = new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in normalizedTargets)
        {
            var matches = FindTargetRecords(target, records, index);
            foreach (var match in matches)
            {
                targetMatches.Add(new EsmScriptDiagnosticTargetMatchRow(
                    target,
                    match.Info.RecordType,
                    match.Info.FormId,
                    match.Info.EditorId,
                    match.Info.FullName,
                    match.Reason));
                AddRelation(recordRelations, target, match.Info.RecordType, match.Info.FormId, match.Reason);
            }

            actorIdsByTarget[target] = matches
                .Where(m => ActorRecordTypes.Contains(m.Info.RecordType))
                .Select(m => m.Info.FormId)
                .ToHashSet();
        }

        if (explicitRecordFormIds is { Count: > 0 })
        {
            foreach (var target in normalizedTargets.Count == 0 ? ["explicit"] : normalizedTargets)
            {
                foreach (var formId in explicitRecordFormIds)
                {
                    if (byFormId.TryGetValue(formId, out var record))
                    {
                        AddRelation(recordRelations, target, record.Header.Signature, formId, "explicit-record");
                    }
                }
            }
        }

        foreach (var target in normalizedTargets)
        {
            var actorIds = actorIdsByTarget[target];
            var packageIds = new HashSet<uint>();
            var scriptIds = new HashSet<uint>();
            var topicIds = new HashSet<uint>();
            var expandedTopicIds = new HashSet<uint>();

            foreach (var actorId in actorIds)
            {
                if (!byFormId.TryGetValue(actorId, out var actorRecord))
                {
                    continue;
                }

                foreach (var packageId in ReadFormIdSubrecords(actorRecord, "PKID"))
                {
                    packageIds.Add(packageId);
                }

                foreach (var scriptId in ReadFormIdSubrecords(actorRecord, "SCRI"))
                {
                    scriptIds.Add(scriptId);
                }
            }

            foreach (var placedActor in records.Where(r =>
                         PlacedActorRecordTypes.Contains(r.Header.Signature)
                         && actorIds.Contains(ReadFirstFormIdSubrecord(r, "NAME"))))
            {
                AddRelation(recordRelations, target, placedActor.Header.Signature, placedActor.Header.FormId,
                    "placed-actor");
                foreach (var packageId in ReadFormIdSubrecords(placedActor, "PKID"))
                {
                    packageIds.Add(packageId);
                }
            }

            foreach (var info in records.Where(r => r.Header.Signature == "INFO" && IsInfoRelatedToActor(r, actorIds)))
            {
                AddRelation(recordRelations, target, "INFO", info.Header.FormId, "actor-dialogue");
                foreach (var topicId in ReadFormIdSubrecords(info, "TPIC"))
                {
                    topicIds.Add(topicId);
                }
            }

            foreach (var dial in records.Where(r =>
                         r.Header.Signature == "DIAL" &&
                         (actorIds.Contains(ReadFirstFormIdSubrecord(r, "TNAM")) ||
                          LabelMatches(index.GetValueOrDefault(r.Header.FormId), target))))
            {
                topicIds.Add(dial.Header.FormId);
                expandedTopicIds.Add(dial.Header.FormId);
                AddRelation(recordRelations, target, "DIAL", dial.Header.FormId,
                    actorIds.Contains(ReadFirstFormIdSubrecord(dial, "TNAM")) ? "actor-topic" : "target-topic");
            }

            foreach (var topicId in topicIds)
            {
                if (byFormId.TryGetValue(topicId, out var topicRecord) && topicRecord.Header.Signature == "DIAL")
                {
                    AddRelation(recordRelations, target, "DIAL", topicRecord.Header.FormId, "dialogue-topic");
                }

                if (!expandedTopicIds.Contains(topicId))
                {
                    continue;
                }

                foreach (var info in records.Where(r =>
                             r.Header.Signature == "INFO" && ReadFirstFormIdSubrecord(r, "TPIC") == topicId))
                {
                    AddRelation(recordRelations, target, "INFO", info.Header.FormId, "topic-info");
                }
            }

            foreach (var packageId in packageIds)
            {
                if (byFormId.TryGetValue(packageId, out var packageRecord) &&
                    packageRecord.Header.Signature == "PACK")
                {
                    AddRelation(recordRelations, target, "PACK", packageRecord.Header.FormId, "actor-package");
                }
            }

            foreach (var scriptId in scriptIds)
            {
                if (byFormId.TryGetValue(scriptId, out var scriptRecord) &&
                    scriptRecord.Header.Signature == "SCPT")
                {
                    AddRelation(recordRelations, target, "SCPT", scriptRecord.Header.FormId, "actor-script");
                }
            }

            foreach (var pack in records.Where(r =>
                         r.Header.Signature == "PACK" &&
                         (LabelMatches(index.GetValueOrDefault(r.Header.FormId), target) ||
                          ContainsAnyFormReference(r, actorIds))))
            {
                AddRelation(recordRelations, target, "PACK", pack.Header.FormId, "target-ref-pack");
            }

            foreach (var script in records.Where(r =>
                         r.Header.Signature == "SCPT" &&
                         (LabelMatches(index.GetValueOrDefault(r.Header.FormId), target) ||
                          ContainsAnyFormReference(r, actorIds))))
            {
                AddRelation(recordRelations, target, "SCPT", script.Header.FormId, "target-ref-script");
            }
        }

        var recordRows = BuildRecordRows(recordRelations, byFormId, index);
        var dialogueRows = BuildDialogueRows(recordRelations, byFormId, index);
        var dialogueAuditRows = BuildDialogueAuditRows(dialogueRows, byFormId);
        var conditionRows = BuildConditionRows(recordRows, byFormId, index);
        var scriptBlocks = new List<EsmScriptDiagnosticBlockRow>();
        var scriptRefs = new List<EsmScriptDiagnosticReferenceRow>();

        foreach (var recordRow in recordRows)
        {
            if (!byFormId.TryGetValue(recordRow.FormId, out var record) || !ContainsScriptPayload(record))
            {
                continue;
            }

            ExtractScriptBlocks(recordRow, record, index, validFormIds, scriptBlocks, scriptRefs);
        }

        return new EsmScriptDiagnosticsResult(
            sourcePath,
            normalizedTargets,
            targetMatches
                .OrderBy(r => r.Target, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.RecordType, StringComparer.Ordinal)
                .ThenBy(r => r.FormId)
                .ToList(),
            recordRows,
            dialogueRows,
            dialogueAuditRows,
            conditionRows,
            scriptBlocks
                .OrderBy(r => r.Target, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.RecordType, StringComparer.Ordinal)
                .ThenBy(r => r.FormId)
                .ThenBy(r => r.BlockIndex)
                .ToList(),
            scriptRefs
                .OrderBy(r => r.Target, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.ParentRecordType, StringComparer.Ordinal)
                .ThenBy(r => r.ParentFormId)
                .ThenBy(r => r.BlockIndex)
                .ThenBy(r => r.SlotIndex)
                .ToList());
    }

    public static void WriteReport(EsmScriptDiagnosticsResult result, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "target_matches.csv"), BuildTargetMatchesCsv(result),
            Encoding.UTF8);
        File.WriteAllText(Path.Combine(outputDirectory, "target_records.csv"), BuildRecordsCsv(result),
            Encoding.UTF8);
        File.WriteAllText(Path.Combine(outputDirectory, "target_dialogue.csv"), BuildDialogueCsv(result),
            Encoding.UTF8);
        File.WriteAllText(Path.Combine(outputDirectory, "target_dialogue_audit.csv"),
            BuildDialogueAuditCsv(result), Encoding.UTF8);
        File.WriteAllText(Path.Combine(outputDirectory, "target_conditions.csv"),
            BuildConditionsCsv(result), Encoding.UTF8);
        File.WriteAllText(Path.Combine(outputDirectory, "target_result_scripts.csv"), BuildScriptBlocksCsv(result),
            Encoding.UTF8);
        File.WriteAllText(Path.Combine(outputDirectory, "target_scro_refs.csv"), BuildScriptReferencesCsv(result),
            Encoding.UTF8);
        File.WriteAllText(Path.Combine(outputDirectory, "summary.md"), BuildSummary(result), Encoding.UTF8);
    }

    private static Dictionary<uint, FormIdInfo> BuildFormIdIndex(IReadOnlyList<ParsedMainRecord> records)
    {
        return records
            .GroupBy(r => r.Header.FormId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var record = g.First();
                    return new FormIdInfo(
                        record.Header.FormId,
                        record.Header.Signature,
                        ReadFirstStringSubrecord(record, "EDID") ?? string.Empty,
                        ReadFirstStringSubrecord(record, "FULL") ?? string.Empty);
                });
    }

    private static List<TargetRecordMatch> FindTargetRecords(
        string target,
        IReadOnlyList<ParsedMainRecord> records,
        IReadOnlyDictionary<uint, FormIdInfo> index)
    {
        if (TryParseFormId(target, out var targetFormId) &&
            index.TryGetValue(targetFormId, out var directInfo))
        {
            return [new TargetRecordMatch(directInfo, "direct-formid")];
        }

        var matches = records
            .Where(r => ActorRecordTypes.Contains(r.Header.Signature))
            .Select(r => index[r.Header.FormId])
            .Where(info => LabelMatches(info, target))
            .Select(info => new TargetRecordMatch(info, "actor-label"))
            .ToList();

        if (matches.Count > 0)
        {
            return matches;
        }

        return records
            .Select(r => index[r.Header.FormId])
            .Where(info => LabelMatches(info, target))
            .Select(info => new TargetRecordMatch(info, "label"))
            .ToList();
    }

    private static List<EsmScriptDiagnosticRecordRow> BuildRecordRows(
        Dictionary<(string Target, string RecordType, uint FormId), HashSet<string>> relations,
        Dictionary<uint, ParsedMainRecord> byFormId,
        IReadOnlyDictionary<uint, FormIdInfo> index)
    {
        return relations
            .Select(kvp =>
            {
                byFormId.TryGetValue(kvp.Key.FormId, out var record);
                index.TryGetValue(kvp.Key.FormId, out var info);
                return new EsmScriptDiagnosticRecordRow(
                    kvp.Key.Target,
                    string.Join('|', kvp.Value.Order(StringComparer.Ordinal)),
                    kvp.Key.RecordType,
                    kvp.Key.FormId,
                    info?.EditorId ?? string.Empty,
                    info?.FullName ?? string.Empty,
                    record is null ? string.Empty : BuildInterestingSubrecordSummary(record, index));
            })
            .OrderBy(r => r.Target, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.RecordType, StringComparer.Ordinal)
            .ThenBy(r => r.FormId)
            .ToList();
    }

    private static List<EsmScriptDiagnosticDialogueRow> BuildDialogueRows(
        Dictionary<(string Target, string RecordType, uint FormId), HashSet<string>> relations,
        Dictionary<uint, ParsedMainRecord> byFormId,
        IReadOnlyDictionary<uint, FormIdInfo> index)
    {
        var rows = new List<EsmScriptDiagnosticDialogueRow>();
        foreach (var ((target, recordType, formId), _) in relations)
        {
            if (recordType != "INFO" || !byFormId.TryGetValue(formId, out var info))
            {
                continue;
            }

            var topicId = ReadFirstFormIdSubrecord(info, "TPIC");
            var speakerId = ReadFirstFormIdSubrecord(info, "ANAM");
            if (speakerId == 0)
            {
                speakerId = ReadSpeakerFromPositiveGetIsIdCondition(info);
            }

            var responses = info.Subrecords
                .Where(s => s.Signature == "NAM1")
                .Select(s => s.DataAsString)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            rows.Add(new EsmScriptDiagnosticDialogueRow(
                target,
                info.Header.FormId,
                index.GetValueOrDefault(info.Header.FormId)?.EditorId ?? string.Empty,
                topicId,
                ResolveLabel(index, topicId),
                ReadFirstFormIdSubrecord(info, "QSTI"),
                speakerId,
                ReadFirstFormIdSubrecord(info, "PNAM"),
                FormatFormIds(ReadFormIdSubrecords(info, "TCLT")),
                FormatFormIds(ReadFormIdSubrecords(info, "TCLF")),
                FormatFormIds(ReadFormIdSubrecords(info, "NAME")),
                FormatFormIds(ReadFormIdSubrecords(info, "TCFU")),
                FormatInfoFlags(info),
                responses.Count,
                info.Subrecords.Any(s => s.Signature == "SCHR"),
                responses.Count == 0 ? string.Empty : Truncate(responses[0], 140)));
        }

        return rows
            .OrderBy(r => r.Target, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.TopicFormId)
            .ThenBy(r => r.InfoFormId)
            .ToList();
    }

    private static List<EsmScriptDialogueAuditRow> BuildDialogueAuditRows(
        IReadOnlyList<EsmScriptDiagnosticDialogueRow> dialogueRows,
        Dictionary<uint, ParsedMainRecord> byFormId)
    {
        var incomingByTargetQuestSpeaker = new Dictionary<(string Target, uint Quest, uint Speaker), HashSet<uint>>();
        var goodbyePairs = new HashSet<(string Target, uint Quest, uint Speaker)>();
        var rowByInfo = dialogueRows.ToDictionary(r => r.InfoFormId);

        foreach (var row in dialogueRows)
        {
            var key = (row.Target, row.QuestFormId, row.SpeakerFormId);
            if (row.TopicFormId == 0x000000D4)
            {
                goodbyePairs.Add(key);
            }

            if (!incomingByTargetQuestSpeaker.TryGetValue(key, out var incoming))
            {
                incoming = [];
                incomingByTargetQuestSpeaker[key] = incoming;
            }

            foreach (var topic in ParseFormIds(row.LinkToTopics))
            {
                incoming.Add(topic);
            }

            foreach (var infoId in ParseFormIds(row.FollowUpInfos))
            {
                if (rowByInfo.TryGetValue(infoId, out var followUp))
                {
                    incoming.Add(followUp.TopicFormId);
                }
            }

            if (row.LinkFromTopics.Length > 0 && row.TopicFormId != 0)
            {
                incoming.Add(row.TopicFormId);
            }
        }

        var results = new List<EsmScriptDialogueAuditRow>();
        foreach (var row in dialogueRows)
        {
            byFormId.TryGetValue(row.InfoFormId, out var record);
            var key = (row.Target, row.QuestFormId, row.SpeakerFormId);
            incomingByTargetQuestSpeaker.TryGetValue(key, out var incoming);
            var hasIncoming = incoming?.Contains(row.TopicFormId) == true;
            var hasExplicitRootLink = row.TopicFormId == 0x000000C8 && row.LinkToTopics.Length > 0;
            var isGoodbye = row.TopicFormId == 0x000000D4;
            var isTerminalReturnCandidate =
                row.TopicFormId is not 0 and not 0x000000C8 and not 0x000000D4
                && row.ResponseCount > 0
                && row.LinkToTopics.Length > 0
                && row.FollowUpInfos.Length == 0
                && row.AddTopics.Length == 0;

            string classification;
            if (hasExplicitRootLink)
            {
                classification = "ExplicitGreetingRoot";
            }
            else if (isGoodbye)
            {
                classification = "Goodbye";
            }
            else if (isTerminalReturnCandidate)
            {
                classification = "TerminalReturnCandidate";
            }
            else if (hasIncoming)
            {
                classification = "InternalLinkedTopic";
            }
            else
            {
                classification = "AmbiguousVisibleRoot";
            }

            results.Add(new EsmScriptDialogueAuditRow(
                row.Target,
                row.InfoFormId,
                row.TopicFormId,
                row.TopicLabel,
                row.QuestFormId,
                row.SpeakerFormId,
                classification,
                hasIncoming,
                hasExplicitRootLink,
                isTerminalReturnCandidate,
                goodbyePairs.Contains(key),
                record is null ? string.Empty : FormatRawSubrecordBytes(record, "TCLT"),
                row.LinkToTopics,
                row.FollowUpInfos,
                row.ResponsePreview));
        }

        return results
            .OrderBy(r => r.Target, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.TopicFormId)
            .ThenBy(r => r.InfoFormId)
            .ToList();
    }

    private static List<EsmScriptConditionAuditRow> BuildConditionRows(
        IReadOnlyList<EsmScriptDiagnosticRecordRow> recordRows,
        Dictionary<uint, ParsedMainRecord> byFormId,
        IReadOnlyDictionary<uint, FormIdInfo> index)
    {
        var results = new List<EsmScriptConditionAuditRow>();
        foreach (var recordRow in recordRows)
        {
            if (!byFormId.TryGetValue(recordRow.FormId, out var record))
            {
                continue;
            }

            var conditionIndex = 0;
            foreach (var sub in record.Subrecords.Where(s => s.Signature == "CTDA" && s.Data.Length >= 28))
            {
                conditionIndex++;
                var condition = CtdaParser.Decode(sub.Data, sub.BigEndian);
                results.Add(new EsmScriptConditionAuditRow(
                    recordRow.Target,
                    recordRow.Relation,
                    recordRow.RecordType,
                    recordRow.FormId,
                    recordRow.EditorId,
                    conditionIndex,
                    ResolveConditionFunctionName(condition.FunctionIndex),
                    condition.FunctionIndex,
                    condition.Type,
                    condition.ComparisonValue,
                    condition.Parameter1,
                    ResolveParameterLabel(index, condition.FunctionIndex, 0, condition.Parameter1),
                    condition.Parameter2,
                    ResolveParameterLabel(index, condition.FunctionIndex, 1, condition.Parameter2),
                    condition.RunOn,
                    condition.Reference,
                    ResolveLabel(index, condition.Reference),
                    Convert.ToHexString(sub.Data)));
            }
        }

        return results
            .OrderBy(r => r.Target, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.RecordType, StringComparer.Ordinal)
            .ThenBy(r => r.FormId)
            .ThenBy(r => r.ConditionIndex)
            .ToList();
    }

    private static void ExtractScriptBlocks(
        EsmScriptDiagnosticRecordRow recordRow,
        ParsedMainRecord record,
        IReadOnlyDictionary<uint, FormIdInfo> index,
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

            var end = FindScriptBlockEnd(subs, i + 1);
            var scdaIndex = FindFirstSubrecord(subs, "SCDA", i + 1, end);
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
            var end = FindScriptBlockEnd(subs, indexInRecord + 1);
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
        IReadOnlyDictionary<uint, FormIdInfo> index,
        IReadOnlySet<uint> validFormIds,
        List<EsmScriptDiagnosticBlockRow> scriptBlocks,
        List<EsmScriptDiagnosticReferenceRow> scriptReferences)
    {
        var subs = record.Subrecords;
        var blockStart = schrIndex >= 0 ? schrIndex + 1 : scdaIndex + 1;
        var header = schrIndex >= 0 ? TryReadScriptHeader(subs[schrIndex].Data) : default;
        var variables = ReadScriptVariables(subs, blockStart, blockEnd);
        var refs = ReadScriptReferences(subs, blockStart, blockEnd);
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
            Truncate(ReadFirstStringSubrecord(subs, "SCTX", blockStart, blockEnd), 180)));

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

    private static bool IsInfoRelatedToActor(ParsedMainRecord record, HashSet<uint> actorIds)
    {
        if (actorIds.Count == 0)
        {
            return false;
        }

        foreach (var sub in record.Subrecords)
        {
            if (sub.Data.Length < 4)
            {
                continue;
            }

            if (sub.Signature is "ANAM" or "SNAM" && actorIds.Contains(sub.DataAsFormId))
            {
                return true;
            }

            if (sub.Signature == "CTDA" && sub.Data.Length >= 28)
            {
                var condition = CtdaParser.Decode(sub.Data, sub.BigEndian);
                if (condition.FunctionIndex == 0x48 && actorIds.Contains(condition.Parameter1))
                {
                    return true;
                }

                if (actorIds.Contains(condition.Parameter1) || actorIds.Contains(condition.Reference))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ContainsAnyFormReference(ParsedMainRecord record, HashSet<uint> formIds)
    {
        if (formIds.Count == 0)
        {
            return false;
        }

        foreach (var sub in record.Subrecords)
        {
            if (sub.Data.Length == 4 && formIds.Contains(sub.DataAsFormId))
            {
                return true;
            }

            if (sub.Signature == "CTDA" && sub.Data.Length >= 28)
            {
                var condition = CtdaParser.Decode(sub.Data, sub.BigEndian);
                if (formIds.Contains(condition.Parameter1) || formIds.Contains(condition.Reference))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ContainsScriptPayload(ParsedMainRecord record)
    {
        return record.Subrecords.Any(s => s.Signature is "SCHR" or "SCDA");
    }

    private static void AddRelation(
        Dictionary<(string Target, string RecordType, uint FormId), HashSet<string>> relations,
        string target,
        string recordType,
        uint formId,
        string relation)
    {
        var key = (target, recordType, formId);
        if (!relations.TryGetValue(key, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            relations[key] = set;
        }

        set.Add(relation);
    }

    private static uint ReadSpeakerFromPositiveGetIsIdCondition(ParsedMainRecord info)
    {
        foreach (var sub in info.Subrecords)
        {
            if (sub.Signature != "CTDA" || sub.Data.Length < 28)
            {
                continue;
            }

            var condition = CtdaParser.Decode(sub.Data, sub.BigEndian);
            if (condition.FunctionIndex == 0x48)
            {
                return condition.Parameter1;
            }
        }

        return 0;
    }

    private static List<uint> ReadFormIdSubrecords(ParsedMainRecord record, string signature)
    {
        return record.Subrecords
            .Where(s => s.Signature == signature && s.Data.Length >= 4)
            .Select(s => s.DataAsFormId)
            .Where(id => id != 0)
            .ToList();
    }

    private static uint ReadFirstFormIdSubrecord(ParsedMainRecord record, string signature)
    {
        return record.Subrecords.FirstOrDefault(s => s.Signature == signature && s.Data.Length >= 4)?.DataAsFormId ??
               0;
    }

    private static string? ReadFirstStringSubrecord(ParsedMainRecord record, string signature)
    {
        return record.Subrecords.FirstOrDefault(s => s.Signature == signature)?.DataAsString;
    }

    private static string ReadFirstStringSubrecord(
        List<ParsedSubrecord> subrecords,
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

    private static string BuildInterestingSubrecordSummary(
        ParsedMainRecord record,
        IReadOnlyDictionary<uint, FormIdInfo> index)
    {
        var parts = new List<string>();
        foreach (var sub in record.Subrecords)
        {
            if (sub.Data.Length < 4)
            {
                if (sub.Signature is "SCHR" or "NEXT" or "PKED" or "PUID" or "PKAM")
                {
                    parts.Add(sub.Signature);
                }

                continue;
            }

            if (sub.Signature is "NAME" or "ANAM" or "SNAM" or "TPIC" or "QSTI" or "PNAM" or "TCLT" or
                "TCLF" or "TCFU" or "PKID" or "SCRI" or "SCRO" or "SCRV" or "TNAM" or "PLDT" or "PTDT")
            {
                var value = sub.Signature == "CTDA" && sub.Data.Length >= 28
                    ? 0
                    : sub.DataAsFormId;
                parts.Add(value == 0
                    ? sub.Signature
                    : $"{sub.Signature}=0x{value:X8}{ResolveLabelSuffix(index, value)}");
            }
            else if (sub.Signature == "CTDA" && sub.Data.Length >= 28)
            {
                var condition = CtdaParser.Decode(sub.Data, sub.BigEndian);
                parts.Add(
                    $"CTDA(fn=0x{condition.FunctionIndex:X},p1=0x{condition.Parameter1:X8}{ResolveLabelSuffix(index, condition.Parameter1)},ref=0x{condition.Reference:X8}{ResolveLabelSuffix(index, condition.Reference)})");
            }
        }

        return string.Join("; ", parts.Take(24));
    }

    private static List<ScriptVariableInfo> ReadScriptVariables(
        List<ParsedSubrecord> subrecords,
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
                variables.Add(new ScriptVariableInfo(
                    pendingIndex.Value,
                    sub.DataAsString,
                    pendingType));
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
        List<ParsedSubrecord> subrecords,
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

    private static int FindScriptBlockEnd(List<ParsedSubrecord> subrecords, int start)
    {
        for (var i = start; i < subrecords.Count; i++)
        {
            if (subrecords[i].Signature is "NEXT" or "SCHR" or "POBA" or "POEA" or "POCA")
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

        var sctxIndex = FindFirstSubrecord(subrecords, "SCTX", schrIndex + 1, blockEnd);
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

    private static string ResolveReferenceStatus(ScriptReferenceSlot reference, IReadOnlySet<uint> validFormIds)
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

    private static string FormatInfoFlags(ParsedMainRecord info)
    {
        var data = info.Subrecords.FirstOrDefault(s => s.Signature == "DATA" && s.Data.Length >= 4)?.Data;
        return data is null ? string.Empty : $"0x{data[2]:X2}/0x{data[3]:X2}";
    }

    private static string ResolveLabel(IReadOnlyDictionary<uint, FormIdInfo> index, uint formId)
    {
        if (formId == 0 || !index.TryGetValue(formId, out var info))
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(info.EditorId))
        {
            return info.EditorId;
        }

        return !string.IsNullOrWhiteSpace(info.FullName) ? info.FullName : info.RecordType;
    }

    private static string ResolveLabelSuffix(IReadOnlyDictionary<uint, FormIdInfo> index, uint formId)
    {
        var label = ResolveLabel(index, formId);
        return string.IsNullOrWhiteSpace(label) ? string.Empty : $" ({label})";
    }

    private static bool LabelMatches(FormIdInfo? info, string target)
    {
        if (info is null)
        {
            return false;
        }

        if (ContainsIgnoreCase(info.EditorId, target) || ContainsIgnoreCase(info.FullName, target))
        {
            return true;
        }

        var normalizedTarget = NormalizeSearchText(target);
        return normalizedTarget.Length > 0 &&
               (NormalizeSearchText(info.EditorId).Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
                NormalizeSearchText(info.FullName).Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsIgnoreCase(string? value, string target)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Contains(target, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSearchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        var index = 0;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer[index++] = char.ToLowerInvariant(ch);
            }
        }

        return new string(buffer[..index]);
    }

    private static bool TryParseFormId(string value, out uint formId)
    {
        var text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out formId);
    }

    private static string FormatFormIds(IEnumerable<uint> formIds)
    {
        return string.Join(' ', formIds.Where(id => id != 0).Select(id => $"0x{id:X8}"));
    }

    private static List<uint> ParseFormIds(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var result = new List<uint>();
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var value = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? token[2..]
                : token;
            if (uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var formId))
            {
                result.Add(formId);
            }
        }

        return result;
    }

    private static string FormatRawSubrecordBytes(ParsedMainRecord record, string signature)
    {
        return string.Join(' ', record.Subrecords
            .Where(s => s.Signature == signature)
            .Select(s => Convert.ToHexString(s.Data)));
    }

    private static string ResolveConditionFunctionName(ushort conditionFunctionIndex)
    {
        var name = PerkConditionParameterResolver.ResolveScriptFunctionName(conditionFunctionIndex);
        return string.IsNullOrWhiteSpace(name)
            ? $"Func0x{conditionFunctionIndex:X4}"
            : name;
    }

    private static string ResolveParameterLabel(
        IReadOnlyDictionary<uint, FormIdInfo> index,
        ushort functionIndex,
        int parameterIndex,
        uint rawValue)
    {
        var resolved = PerkConditionParameterResolver.ResolveParameter(functionIndex, parameterIndex, rawValue);
        if (!string.IsNullOrWhiteSpace(resolved.Display))
        {
            return resolved.Display;
        }

        if (resolved.FormId.HasValue)
        {
            return ResolveLabel(index, resolved.FormId.Value);
        }

        return ResolveLabel(index, rawValue);
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value ?? string.Empty;
        }

        return value[..Math.Max(0, maxLength - 3)] + "...";
    }

    private static string BuildTargetMatchesCsv(EsmScriptDiagnosticsResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("target,record_type,form_id,editor_id,full_name,match_reason");
        foreach (var row in result.TargetMatches)
        {
            sb.AppendLine(string.Join(',',
                Csv(row.Target),
                Csv(row.RecordType),
                Csv($"0x{row.FormId:X8}"),
                Csv(row.EditorId),
                Csv(row.FullName),
                Csv(row.MatchReason)));
        }

        return sb.ToString();
    }

    private static string BuildRecordsCsv(EsmScriptDiagnosticsResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("target,relation,record_type,form_id,editor_id,full_name,interesting_subrecords");
        foreach (var row in result.Records)
        {
            sb.AppendLine(string.Join(',',
                Csv(row.Target),
                Csv(row.Relation),
                Csv(row.RecordType),
                Csv($"0x{row.FormId:X8}"),
                Csv(row.EditorId),
                Csv(row.FullName),
                Csv(row.InterestingSubrecords)));
        }

        return sb.ToString();
    }

    private static string BuildDialogueCsv(EsmScriptDiagnosticsResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "target,info_form_id,info_editor_id,topic_form_id,topic_label,quest_form_id,speaker_form_id,previous_info,link_to_topics,link_from_topics,add_topics,follow_up_infos,info_flags,response_count,has_result_script,response_preview");
        foreach (var row in result.Dialogue)
        {
            sb.AppendLine(string.Join(',',
                Csv(row.Target),
                Csv($"0x{row.InfoFormId:X8}"),
                Csv(row.InfoEditorId),
                Csv(row.TopicFormId == 0 ? string.Empty : $"0x{row.TopicFormId:X8}"),
                Csv(row.TopicLabel),
                Csv(row.QuestFormId == 0 ? string.Empty : $"0x{row.QuestFormId:X8}"),
                Csv(row.SpeakerFormId == 0 ? string.Empty : $"0x{row.SpeakerFormId:X8}"),
                Csv(row.PreviousInfo == 0 ? string.Empty : $"0x{row.PreviousInfo:X8}"),
                Csv(row.LinkToTopics),
                Csv(row.LinkFromTopics),
                Csv(row.AddTopics),
                Csv(row.FollowUpInfos),
                Csv(row.InfoFlags),
                row.ResponseCount,
                row.HasResultScript,
                Csv(row.ResponsePreview)));
        }

        return sb.ToString();
    }

    private static string BuildDialogueAuditCsv(EsmScriptDiagnosticsResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "target,info_form_id,topic_form_id,topic_label,quest_form_id,speaker_form_id,root_classification,has_incoming_topic_edge,has_explicit_root_link,is_terminal_return_candidate,has_goodbye_for_speaker_quest,raw_tclt_bytes,link_to_topics,follow_up_infos,response_preview");
        foreach (var row in result.DialogueAudit)
        {
            sb.AppendLine(string.Join(',',
                Csv(row.Target),
                Csv($"0x{row.InfoFormId:X8}"),
                Csv(row.TopicFormId == 0 ? string.Empty : $"0x{row.TopicFormId:X8}"),
                Csv(row.TopicLabel),
                Csv(row.QuestFormId == 0 ? string.Empty : $"0x{row.QuestFormId:X8}"),
                Csv(row.SpeakerFormId == 0 ? string.Empty : $"0x{row.SpeakerFormId:X8}"),
                Csv(row.RootClassification),
                row.HasIncomingTopicEdge,
                row.HasExplicitRootLink,
                row.IsTerminalReturnCandidate,
                row.HasGoodbyeForSpeakerQuest,
                Csv(row.RawTcltBytes),
                Csv(row.LinkToTopics),
                Csv(row.FollowUpInfos),
                Csv(row.ResponsePreview)));
        }

        return sb.ToString();
    }

    private static string BuildConditionsCsv(EsmScriptDiagnosticsResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "target,relation,record_type,form_id,editor_id,condition_index,function_name,function_index,type,comparison_value,parameter1,parameter1_label,parameter2,parameter2_label,run_on,reference,reference_label,raw_bytes");
        foreach (var row in result.Conditions)
        {
            sb.AppendLine(string.Join(',',
                Csv(row.Target),
                Csv(row.Relation),
                Csv(row.RecordType),
                Csv($"0x{row.FormId:X8}"),
                Csv(row.EditorId),
                row.ConditionIndex,
                Csv(row.FunctionName),
                Csv($"0x{row.FunctionIndex:X4}"),
                row.Type,
                row.ComparisonValue.ToString(CultureInfo.InvariantCulture),
                Csv($"0x{row.Parameter1:X8}"),
                Csv(row.Parameter1Label),
                Csv($"0x{row.Parameter2:X8}"),
                Csv(row.Parameter2Label),
                row.RunOn,
                Csv(row.Reference == 0 ? string.Empty : $"0x{row.Reference:X8}"),
                Csv(row.ReferenceLabel),
                Csv(row.RawBytes)));
        }

        return sb.ToString();
    }

    private static string BuildScriptBlocksCsv(EsmScriptDiagnosticsResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "target,relation,record_type,form_id,editor_id,block_index,subrecord_order,order_status,scda_length,schr_compiled_size,schr_reference_count,actual_reference_slots,compiled_size_matches,ref_count_matches,walked_to_end,has_diagnostics,diagnostics,source_text_preview");
        foreach (var row in result.ScriptBlocks)
        {
            sb.AppendLine(string.Join(',',
                Csv(row.Target),
                Csv(row.Relation),
                Csv(row.RecordType),
                Csv($"0x{row.FormId:X8}"),
                Csv(row.EditorId),
                row.BlockIndex,
                Csv(row.SubrecordOrder),
                Csv(row.OrderStatus),
                row.ScdaLength,
                row.SchrCompiledSize?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                row.SchrReferenceCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                row.ActualReferenceSlots,
                row.CompiledSizeMatches,
                row.RefCountMatches,
                row.WalkedToEnd,
                row.HasDiagnostics,
                Csv(row.Diagnostics),
                Csv(row.SourceTextPreview)));
        }

        return sb.ToString();
    }

    private static string BuildScriptReferencesCsv(EsmScriptDiagnosticsResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "target,parent_record_type,parent_form_id,block_index,slot_index,reference_kind,raw_value,resolved_form_id,status,resolved_record_type,resolved_editor_id,resolved_full_name");
        foreach (var row in result.ScriptReferences)
        {
            sb.AppendLine(string.Join(',',
                Csv(row.Target),
                Csv(row.ParentRecordType),
                Csv($"0x{row.ParentFormId:X8}"),
                row.BlockIndex,
                row.SlotIndex,
                Csv(row.ReferenceKind),
                Csv($"0x{row.RawValue:X8}"),
                Csv(row.ResolvedFormId == 0 ? string.Empty : $"0x{row.ResolvedFormId:X8}"),
                Csv(row.Status),
                Csv(row.ResolvedRecordType),
                Csv(row.ResolvedEditorId),
                Csv(row.ResolvedFullName)));
        }

        return sb.ToString();
    }

    private static string BuildSummary(EsmScriptDiagnosticsResult result)
    {
        var missingRefs = result.ScriptReferences.Count(r => r.Status is "Null" or "Missing");
        var structuralFailures = result.ScriptBlocks.Count(r =>
            !r.CompiledSizeMatches || !r.RefCountMatches || !r.WalkedToEnd || r.HasDiagnostics);
        var nonCanonical = result.ScriptBlocks.Count(r => r.OrderStatus != "canonical");

        var sb = new StringBuilder();
        sb.AppendLine($"# Script Diagnostics: {Path.GetFileName(result.SourcePath)}");
        sb.AppendLine();
        sb.AppendLine($"- Targets: {string.Join(", ", result.Targets)}");
        sb.AppendLine($"- Target matches: {result.TargetMatches.Count:N0}");
        sb.AppendLine($"- Related records: {result.Records.Count:N0}");
        sb.AppendLine($"- Related INFO rows: {result.Dialogue.Count:N0}");
        sb.AppendLine($"- Script blocks: {result.ScriptBlocks.Count:N0}");
        sb.AppendLine($"- Script structural failures: {structuralFailures:N0}");
        sb.AppendLine($"- Non-canonical script block order: {nonCanonical:N0}");
        sb.AppendLine($"- Null/missing SCRO refs: {missingRefs:N0}");

        if (result.ScriptBlocks.Count > 0 && structuralFailures == 0)
        {
            sb.AppendLine();
            sb.AppendLine(
                "Target SCDA bytecode walks cleanly; prioritize SCRO remap/content, package state, and result-script attachment/order.");
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

    private sealed record FormIdInfo(
        uint FormId,
        string RecordType,
        string EditorId,
        string FullName);

    private sealed record TargetRecordMatch(FormIdInfo Info, string Reason);

    private sealed record ScriptReferenceSlot(string Kind, uint RawValue);
}
