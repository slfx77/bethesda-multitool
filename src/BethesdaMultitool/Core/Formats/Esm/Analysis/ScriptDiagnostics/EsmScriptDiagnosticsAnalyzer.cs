using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Plugin;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.Games;
using static BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics.EsmScriptDiagnosticsResolvers;

namespace BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics;

/// <summary>Gathers script, dialogue, condition, and reference diagnostics for a set of target records in an ESM/ESP.</summary>
public static class EsmScriptDiagnosticsAnalyzer
{
    /// <summary>
    ///     Target label for an explicit record that no requested target relates to (and for every
    ///     explicit record in an explicit-only run).
    /// </summary>
    public const string ExplicitTargetLabel = "explicit";

    private static readonly HashSet<string> ActorRecordTypes = new(StringComparer.Ordinal)
    {
        "NPC_", "CREA"
    };

    private static readonly HashSet<string> PlacedActorRecordTypes = new(StringComparer.Ordinal)
    {
        "ACHR", "ACRE"
    };

    /// <summary>Parses the file at the given path and runs script diagnostics for the requested targets.</summary>
    public static EsmScriptDiagnosticsResult AnalyzeFile(
        string path,
        IReadOnlyList<string> targets,
        IReadOnlySet<uint>? explicitRecordFormIds = null)
    {
        var data = File.ReadAllBytes(path);
        var game = GameDetector.DetectFromBytes(data, Path.GetFileName(path)).Game;
        var records = EsmParser.EnumerateRecordsWithGrups(data).Records;
        return AnalyzeRecordsCore(path, records, targets, game, explicitRecordFormIds,
            Convert.ToHexStringLower(SHA256.HashData(data)), data.LongLength,
            PluginFormat.Detect(data).RecordHeaderSize);
    }

    /// <summary>Runs script diagnostics for the requested targets against an already-parsed record set.</summary>
    [SuppressMessage(
        "Major Code Smell",
        "S1133:Deprecated code should be removed",
        Justification =
            "This overload remains as a source-compatibility bridge for callers that cannot yet provide game context.")]
    [Obsolete("Pass an explicit BethesdaGame so CTDA layouts and parameter semantics can be decoded safely.")]
    public static EsmScriptDiagnosticsResult AnalyzeRecords(
        string sourcePath,
        IReadOnlyList<ParsedMainRecord> records,
        IReadOnlyList<string> targets,
        IReadOnlySet<uint>? explicitRecordFormIds = null)
    {
        return AnalyzeRecords(sourcePath, records, targets, BethesdaGame.Unknown, explicitRecordFormIds);
    }

    /// <summary>
    ///     Runs game-aware script diagnostics against an already-parsed record set.
    ///     <para>
    ///         Explicit records are attached after discovery: to every target whose discovered
    ///         relations already include the record (relation <c>...|explicit-record</c>), otherwise
    ///         once under <see cref="ExplicitTargetLabel" />. They are never copied under a target
    ///         that does not reach them, so one INFO cannot yield a row per unrelated target.
    ///     </para>
    /// </summary>
    public static EsmScriptDiagnosticsResult AnalyzeRecords(
        string sourcePath,
        IReadOnlyList<ParsedMainRecord> records,
        IReadOnlyList<string> targets,
        BethesdaGame game,
        IReadOnlySet<uint>? explicitRecordFormIds = null)
        => AnalyzeRecordsCore(sourcePath, records, targets, game, explicitRecordFormIds, null, null, null);

    private static EsmScriptDiagnosticsResult AnalyzeRecordsCore(
        string sourcePath,
        IReadOnlyList<ParsedMainRecord> records,
        IReadOnlyList<string> targets,
        BethesdaGame game,
        IReadOnlySet<uint>? explicitRecordFormIds,
        string? sourceSha256,
        long? sourceLength,
        int? recordHeaderSize)
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

            foreach (var info in records.Where(r =>
                         r.Header.Signature == "INFO" && IsInfoRelatedToActor(r, actorIds, game)))
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
                          ContainsAnyFormReference(r, actorIds, game))))
            {
                AddRelation(recordRelations, target, "PACK", pack.Header.FormId, "target-ref-pack");
            }

            foreach (var script in records.Where(r =>
                         r.Header.Signature == "SCPT" &&
                         (LabelMatches(index.GetValueOrDefault(r.Header.FormId), target) ||
                          ContainsAnyFormReference(r, actorIds, game))))
            {
                AddRelation(recordRelations, target, "SCPT", script.Header.FormId, "target-ref-script");
            }
        }

        var (explicitFormIds, missingExplicitFormIds) =
            AttachExplicitRecords(recordRelations, byFormId, explicitRecordFormIds);

        var recordRows = BuildRecordRows(recordRelations, byFormId, index, game);
        var dialogueRows = BuildDialogueRows(recordRelations, byFormId, index, game);
        var dialogueAuditRows = BuildDialogueAuditRows(dialogueRows, byFormId);
        var conditionRows = BuildConditionRows(recordRows, byFormId, index, game);
        var scriptBlocks = new List<EsmScriptDiagnosticBlockRow>();
        var scriptRefs = new List<EsmScriptDiagnosticReferenceRow>();

        foreach (var recordRow in recordRows)
        {
            if (!byFormId.TryGetValue(recordRow.FormId, out var record) || !ContainsScriptPayload(record))
            {
                continue;
            }

            var firstNewRow = scriptBlocks.Count;
            EsmScriptBlockRowBuilder.ExtractScriptBlocks(recordRow, record, index, validFormIds, scriptBlocks,
                scriptRefs, game);
            AttachSourceTextBytes(record, scriptBlocks, firstNewRow);
        }

        var terminalItems = EsmScriptTerminalItemRowBuilder.BuildRows(recordRows, byFormId, index, game);

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
                .ToList())
        {
            Game = game,
            ExplicitRecordFormIds = explicitFormIds,
            MissingExplicitRecordFormIds = missingExplicitFormIds,
            TerminalItems = terminalItems,
            SourceSha256 = sourceSha256,
            SourceLength = sourceLength,
            QuestScripts = EsmQuestScriptReader.Read(records,
                recordRows.Where(row => row.RecordType == "QUST").Select(row => row.FormId).ToHashSet(),
                game, recordHeaderSize)
        };
    }

    /// <summary>
    ///     Writes the diagnostics result as a set of CSV reports into the given output directory.
    ///     <c>target_terminal_items.csv</c> is always written (header only when no TERM record is related);
    ///     <c>target_conditions.csv</c> keeps its columns, and the TERM join lives in the new file instead.
    ///     <para>
    ///         Every script block with a non-empty SCTX also gets its source as a file of its own,
    ///         <c>scripts/{RecordType}_{FormId:X8}_block{NN:D2}{ext}</c>
    ///         (<see cref="EsmScriptDiagnosticsCsvWriter.SourceTextFileName" />): the SCTX subrecord's bytes as
    ///         stored minus one trailing NUL, with no header, no BOM, no transcoding (Windows-1252 stays
    ///         Windows-1252) and CRLF kept. A block reached by several targets is written once, and the
    ///         <c>source_text_file</c> column of <c>target_result_scripts.csv</c> names the file on each of its
    ///         rows. <c>scripts/</c> is created only when there is at least one such block. Existing files of
    ///         the same name are overwritten, like the CSVs; files an earlier run wrote that this run does not
    ///         produce are left in place.
    ///     </para>
    ///     <para>
    ///         Selected QUST records also export every physical stage-script occurrence under
    ///         <c>quest-scripts/</c>, with stored source, reconstruction, original SCDA and a provenance manifest.
    ///     </para>
    /// </summary>
    public static void WriteReport(EsmScriptDiagnosticsResult result, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        WriteSourceTextFiles(result, outputDirectory);
        EsmQuestScriptExportWriter.Write(result, outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "target_matches.csv"),
            EsmScriptDiagnosticsCsvWriter.BuildTargetMatchesCsv(result), Encoding.UTF8);
        File.WriteAllText(Path.Combine(outputDirectory, "target_records.csv"),
            EsmScriptDiagnosticsCsvWriter.BuildRecordsCsv(result), Encoding.UTF8);
        File.WriteAllText(Path.Combine(outputDirectory, "target_dialogue.csv"),
            EsmScriptDiagnosticsCsvWriter.BuildDialogueCsv(result), Encoding.UTF8);
        File.WriteAllText(Path.Combine(outputDirectory, "target_dialogue_audit.csv"),
            EsmScriptDiagnosticsCsvWriter.BuildDialogueAuditCsv(result), Encoding.UTF8);
        File.WriteAllText(Path.Combine(outputDirectory, "target_conditions.csv"),
            EsmScriptDiagnosticsCsvWriter.BuildConditionsCsv(result), Encoding.UTF8);
        File.WriteAllText(Path.Combine(outputDirectory, "target_result_scripts.csv"),
            EsmScriptDiagnosticsCsvWriter.BuildScriptBlocksCsv(result), Encoding.UTF8);
        File.WriteAllText(Path.Combine(outputDirectory, "target_scro_refs.csv"),
            EsmScriptDiagnosticsCsvWriter.BuildScriptReferencesCsv(result), Encoding.UTF8);
        File.WriteAllText(Path.Combine(outputDirectory, "target_terminal_items.csv"),
            EsmScriptDiagnosticsCsvWriter.BuildTerminalItemsCsv(result), Encoding.UTF8);
        var summary = EsmScriptDiagnosticsCsvWriter.BuildSummary(result);
        if (result.QuestScripts.Count > 0)
            summary += $"\n## Quest stage scripts\n\n{result.QuestScripts.Count} physical fragments. "
                + "[Source, reconstructions and provenance](quest-scripts/manifest.json).\n";
        File.WriteAllText(Path.Combine(outputDirectory, "summary.md"), summary, Encoding.UTF8);
    }

    /// <summary>
    ///     Writes each distinct block source file once (see <see cref="WriteReport" />). Rows of one block share a
    ///     name, and the analysis reads every row of a FormID from the one record it keeps for that FormID (the
    ///     first), so rows that share a name share the bytes.
    /// </summary>
    private static void WriteSourceTextFiles(EsmScriptDiagnosticsResult result, string outputDirectory)
    {
        var written = new HashSet<string>(StringComparer.Ordinal);
        string? sourceDirectory = null;
        foreach (var row in result.ScriptBlocks)
        {
            var fileName = EsmScriptDiagnosticsCsvWriter.SourceTextFileName(row, result.Game);
            if (fileName is null || row.SourceTextBytes is not { } bytes || !written.Add(fileName))
            {
                continue;
            }

            sourceDirectory ??= Directory.CreateDirectory(
                Path.Combine(outputDirectory, EsmScriptDiagnosticsCsvWriter.SourceTextDirectoryName)).FullName;
            File.WriteAllBytes(Path.Combine(sourceDirectory, fileName), bytes);
        }
    }

    /// <summary>
    ///     Gives the block rows <see cref="EsmScriptBlockRowBuilder" /> just added for <paramref name="record" />
    ///     (<paramref name="scriptBlocks" /> from <paramref name="firstNewRow" /> on) their SCTX as stored bytes.
    ///     The SCTX is found the way the row builder finds the one it decodes into <c>SourceText</c>: the first
    ///     SCTX after the block's SCHR (after its SCDA for an SCHR-less block) and before the block's end, with
    ///     blocks located and numbered by <see cref="EsmScriptBlockReader.LocateScriptBlocks" />.
    /// </summary>
    private static void AttachSourceTextBytes(
        ParsedMainRecord record,
        List<EsmScriptDiagnosticBlockRow> scriptBlocks,
        int firstNewRow)
    {
        if (firstNewRow >= scriptBlocks.Count)
        {
            return;
        }

        var subs = record.Subrecords;
        var spans = EsmScriptBlockReader.LocateScriptBlocks(subs).ToDictionary(span => span.BlockIndex);
        for (var i = firstNewRow; i < scriptBlocks.Count; i++)
        {
            var row = scriptBlocks[i];
            if (!spans.TryGetValue(row.BlockIndex, out var span))
            {
                continue;
            }

            var blockStart = span.SchrIndex >= 0 ? span.SchrIndex + 1 : span.ScdaIndex + 1;
            var sctxIndex = EsmScriptBlockReader.FindFirstSubrecord(subs, "SCTX", blockStart, span.End);
            if (sctxIndex >= 0)
            {
                scriptBlocks[i] = row with { SourceTextBytes = WithoutOneTrailingNul(subs[sctxIndex].Data) };
            }
        }
    }

    /// <summary>A copy of <paramref name="data" /> without its last byte when that byte is a NUL.</summary>
    private static byte[] WithoutOneTrailingNul(byte[] data)
    {
        var length = data.Length > 0 && data[^1] == 0 ? data.Length - 1 : data.Length;
        return data.AsSpan(0, length).ToArray();
    }

    private static Dictionary<uint, EsmScriptFormIdInfo> BuildFormIdIndex(IReadOnlyList<ParsedMainRecord> records)
    {
        return records
            .GroupBy(r => r.Header.FormId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var record = g.First();
                    return new EsmScriptFormIdInfo(
                        record.Header.FormId,
                        record.Header.Signature,
                        ReadFirstStringSubrecord(record, "EDID") ?? string.Empty,
                        ReadFirstStringSubrecord(record, "FULL") ?? string.Empty);
                });
    }

    private static List<TargetRecordMatch> FindTargetRecords(
        string target,
        IReadOnlyList<ParsedMainRecord> records,
        Dictionary<uint, EsmScriptFormIdInfo> index)
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

    /// <summary>
    ///     Attaches each explicit record, in FormID order, to every target whose discovered relations
    ///     already contain it, or under <see cref="ExplicitTargetLabel" /> when none does.
    /// </summary>
    /// <returns>Every requested FormID, sorted, and the subset absent from the file.</returns>
    private static (List<uint> Requested, List<uint> Missing) AttachExplicitRecords(
        Dictionary<(string Target, string RecordType, uint FormId), HashSet<string>> relations,
        Dictionary<uint, ParsedMainRecord> byFormId,
        IReadOnlySet<uint>? explicitRecordFormIds)
    {
        var requested = new List<uint>();
        var missing = new List<uint>();
        if (explicitRecordFormIds is not { Count: > 0 })
        {
            return (requested, missing);
        }

        foreach (var formId in explicitRecordFormIds.Order())
        {
            requested.Add(formId);
            if (!byFormId.TryGetValue(formId, out var record))
            {
                missing.Add(formId);
                continue;
            }

            var recordType = record.Header.Signature;
            var relatedTargets = relations.Keys
                .Where(key => key.FormId == formId && string.Equals(key.RecordType, recordType, StringComparison.Ordinal))
                .Select(key => key.Target)
                .ToList();
            if (relatedTargets.Count == 0)
            {
                relatedTargets.Add(ExplicitTargetLabel);
            }

            foreach (var target in relatedTargets)
            {
                AddRelation(relations, target, recordType, formId, "explicit-record");
            }
        }

        return (requested, missing);
    }

    private static List<EsmScriptDiagnosticRecordRow> BuildRecordRows(
        Dictionary<(string Target, string RecordType, uint FormId), HashSet<string>> relations,
        Dictionary<uint, ParsedMainRecord> byFormId,
        Dictionary<uint, EsmScriptFormIdInfo> index,
        BethesdaGame game)
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
                    record is null ? string.Empty : BuildInterestingSubrecordSummary(record, index, game));
            })
            .OrderBy(r => r.Target, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.RecordType, StringComparer.Ordinal)
            .ThenBy(r => r.FormId)
            .ToList();
    }

    private static List<EsmScriptDiagnosticDialogueRow> BuildDialogueRows(
        Dictionary<(string Target, string RecordType, uint FormId), HashSet<string>> relations,
        Dictionary<uint, ParsedMainRecord> byFormId,
        IReadOnlyDictionary<uint, EsmScriptFormIdInfo> index,
        BethesdaGame game)
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
                speakerId = ReadSpeakerFromPositiveGetIsIdCondition(info, game);
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

        // A dialogue row is unique per (Target, INFO), not per INFO: the same INFO reaches every
        // target that relates to it, so follow-ups resolve within the row's own target.
        var rowByTargetInfo = new Dictionary<(string Target, uint InfoFormId), EsmScriptDiagnosticDialogueRow>();
        foreach (var row in dialogueRows)
        {
            rowByTargetInfo.TryAdd((row.Target, row.InfoFormId), row);
        }

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
                if (rowByTargetInfo.TryGetValue((row.Target, infoId), out var followUp))
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
        IReadOnlyDictionary<uint, EsmScriptFormIdInfo> index,
        BethesdaGame game)
    {
        var results = new List<EsmScriptConditionAuditRow>();
        foreach (var recordRow in recordRows)
        {
            if (!byFormId.TryGetValue(recordRow.FormId, out var record))
            {
                continue;
            }

            var conditionIndex = 0;
            foreach (var sub in record.Subrecords.Where(s => s.Signature == "CTDA"))
            {
                conditionIndex++;
                var layoutStatus = GetCtdaLayoutStatus(game, sub.Data.Length);
                if (!CtdaParser.TryDecode(sub.Data, sub.BigEndian, out var condition, out var physical))
                {
                    results.Add(new EsmScriptConditionAuditRow(
                        recordRow.Target,
                        recordRow.Relation,
                        recordRow.RecordType,
                        recordRow.FormId,
                        recordRow.EditorId,
                        conditionIndex,
                        "Invalid CTDA",
                        0,
                        0,
                        0,
                        0,
                        string.Empty,
                        0,
                        string.Empty,
                        0,
                        string.Empty,
                        null,
                        null,
                        string.Empty,
                        Convert.ToHexString(sub.Data),
                        null,
                        false,
                        null,
                        sub.Data.Length,
                        layoutStatus));
                    continue;
                }

                var layoutIsValid = layoutStatus == "valid";
                var comparisonRawBits = physical.ComparisonRawBits;
                var comparisonGlobalLabel = layoutIsValid && condition.UsesGlobalComparison
                    ? ResolveLabel(index, comparisonRawBits)
                    : string.Empty;
                var referenceStorageIsSemantic = layoutIsValid && physical.ReferenceStorage.HasValue &&
                                                 DialogueConditionReferencePolicy.IsSemanticReferenceSlot(
                                                     condition, game);
                uint? semanticReferenceFormId = referenceStorageIsSemantic && condition.Reference != 0
                    ? condition.Reference
                    : null;
                var referenceLabel = semanticReferenceFormId is { } reference
                    ? ResolveLabel(index, reference)
                    : string.Empty;
                results.Add(new EsmScriptConditionAuditRow(
                    recordRow.Target,
                    recordRow.Relation,
                    recordRow.RecordType,
                    recordRow.FormId,
                    recordRow.EditorId,
                    conditionIndex,
                    layoutIsValid
                        ? ResolveConditionFunctionName(game, condition.FunctionIndex)
                        : $"Func0x{condition.FunctionIndex:X4}",
                    condition.FunctionIndex,
                    condition.Type,
                    condition.ComparisonValue,
                    comparisonRawBits,
                    comparisonGlobalLabel,
                    condition.Parameter1,
                    layoutIsValid
                        ? ResolveParameterLabel(
                            index,
                            game,
                            condition.FunctionIndex,
                            0,
                            condition.Parameter1,
                            condition.Type,
                            condition.RunOn,
                            condition.Parameter1)
                        : string.Empty,
                    condition.Parameter2,
                    layoutIsValid
                        ? ResolveParameterLabel(
                            index,
                            game,
                            condition.FunctionIndex,
                            1,
                            condition.Parameter2,
                            condition.Type,
                            condition.RunOn,
                            condition.Parameter1)
                        : string.Empty,
                    physical.RunOn,
                    physical.ReferenceStorage,
                    referenceLabel,
                    Convert.ToHexString(sub.Data),
                    physical.Parameter3,
                    referenceStorageIsSemantic,
                    semanticReferenceFormId,
                    sub.Data.Length,
                    layoutStatus));
            }
        }

        return results
            .OrderBy(r => r.Target, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.RecordType, StringComparer.Ordinal)
            .ThenBy(r => r.FormId)
            .ThenBy(r => r.ConditionIndex)
            .ToList();
    }

    private static bool IsInfoRelatedToActor(
        ParsedMainRecord record,
        HashSet<uint> actorIds,
        BethesdaGame game)
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

            if (sub.Signature == "CTDA" &&
                TryDecodeCtdaForGame(sub, game, out var condition, out _) &&
                ((IsFormIdConditionParameter(
                      game,
                      condition.FunctionIndex,
                      0,
                      condition.Type,
                      condition.RunOn,
                      condition.Parameter1)
                  && actorIds.Contains(condition.Parameter1))
                 || (IsFormIdConditionParameter(
                         game,
                         condition.FunctionIndex,
                         1,
                         condition.Type,
                         condition.RunOn,
                         condition.Parameter1)
                     && actorIds.Contains(condition.Parameter2))
                 || (DialogueConditionReferencePolicy.TryGetSemanticReference(
                         condition,
                         game,
                         out var reference)
                     && actorIds.Contains(reference))))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAnyFormReference(
        ParsedMainRecord record,
        HashSet<uint> formIds,
        BethesdaGame game)
    {
        if (formIds.Count == 0)
        {
            return false;
        }

        foreach (var sub in record.Subrecords)
        {
            // A bare 4-byte value is a candidate FormID unless the value-kind policy knows it is
            // something else (a SCRV local index, PKE2 escort distance, a float...): with a low
            // target FormID those used to match by coincidence.
            if (sub.Data.Length == 4
                && !EsmScriptSubrecordSummaryFormatter.FirstDwordIsKnownNonFormId(record, sub)
                && formIds.Contains(sub.DataAsFormId))
            {
                return true;
            }

            if (sub.Signature == "CTDA" &&
                TryDecodeCtdaForGame(sub, game, out var condition, out _) &&
                ((IsFormIdConditionParameter(
                      game,
                      condition.FunctionIndex,
                      0,
                      condition.Type,
                      condition.RunOn,
                      condition.Parameter1)
                  && formIds.Contains(condition.Parameter1))
                 || (IsFormIdConditionParameter(
                         game,
                         condition.FunctionIndex,
                         1,
                         condition.Type,
                         condition.RunOn,
                         condition.Parameter1)
                     && formIds.Contains(condition.Parameter2))
                 || (DialogueConditionReferencePolicy.TryGetSemanticReference(
                         condition,
                         game,
                         out var reference)
                     && formIds.Contains(reference))))
            {
                return true;
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

    private static uint ReadSpeakerFromPositiveGetIsIdCondition(
        ParsedMainRecord info,
        BethesdaGame game)
    {
        var candidates = new HashSet<uint>();
        foreach (var sub in info.Subrecords)
        {
            if (sub.Signature != "CTDA" ||
                !TryDecodeCtdaForGame(sub, game, out var condition, out _))
            {
                continue;
            }

            var table = ConditionFunctionTable.For(game);
            if (table.Get(condition.FunctionIndex)?.Name == "GetIsID"
                && IsFormIdConditionParameter(
                    game,
                    condition.FunctionIndex,
                    0,
                    condition.Type,
                    condition.RunOn,
                    condition.Parameter1)
                && DialogueSpeakerBinding.IsPositiveSubjectGetIsId(condition))
            {
                candidates.Add(condition.Parameter1);
                if (candidates.Count > 1)
                {
                    return 0;
                }
            }
        }

        return candidates.SingleOrDefault();
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

    private static string BuildInterestingSubrecordSummary(
        ParsedMainRecord record,
        IReadOnlyDictionary<uint, EsmScriptFormIdInfo> index,
        BethesdaGame game)
    {
        var parts = new List<string>();
        Func<uint, string> labelSuffix = formId => ResolveLabelSuffix(index, formId);
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

            // Package unions, local-variable indexes and per-record scalars are decoded by what
            // they are; only true FormIDs go through the label index.
            if (EsmScriptSubrecordSummaryFormatter.TryFormatSummaryToken(record, sub, game, labelSuffix,
                    out var token))
            {
                parts.Add(token);
            }
            else if (sub.Signature == "CTDA")
            {
                var layoutStatus = GetCtdaLayoutStatus(game, sub.Data.Length);
                if (!CtdaParser.TryDecode(sub.Data, sub.BigEndian, out var condition, out var physical) ||
                    layoutStatus != "valid")
                {
                    parts.Add(
                        $"CTDA(length={sub.Data.Length},status={layoutStatus},raw={Convert.ToHexString(sub.Data)})");
                    continue;
                }

                var parameter1Suffix = IsFormIdConditionParameter(
                    game,
                    condition.FunctionIndex,
                    0,
                    condition.Type,
                    condition.RunOn,
                    condition.Parameter1)
                    ? ResolveLabelSuffix(index, condition.Parameter1)
                    : string.Empty;
                var parameter2Suffix = IsFormIdConditionParameter(
                    game,
                    condition.FunctionIndex,
                    1,
                    condition.Type,
                    condition.RunOn,
                    condition.Parameter1)
                    ? ResolveLabelSuffix(index, condition.Parameter2)
                    : string.Empty;
                var referenceSlotIsSemantic = physical.ReferenceStorage.HasValue &&
                                              DialogueConditionReferencePolicy.IsSemanticReferenceSlot(
                                                  condition, game);
                string referenceSummary;
                if (referenceSlotIsSemantic)
                {
                    referenceSummary =
                        $"ref=0x{condition.Reference:X8}{ResolveLabelSuffix(index, condition.Reference)}";
                }
                else if (physical.ReferenceStorage is { } referenceStorage)
                {
                    referenceSummary = $"reference_storage=0x{referenceStorage:X8}";
                }
                else
                {
                    referenceSummary = "reference_storage=absent";
                }

                var runOnSummary = physical.RunOn is { } runOn ? runOn.ToString() : "absent";
                var parameter3Summary = physical.Parameter3 is { } parameter3
                    ? parameter3.ToString()
                    : "absent";
                parts.Add(
                    $"CTDA(fn=0x{condition.FunctionIndex:X},p1=0x{condition.Parameter1:X8}{parameter1Suffix}," +
                    $"p2=0x{condition.Parameter2:X8}{parameter2Suffix},runOn={runOnSummary}," +
                    $"{referenceSummary},p3={parameter3Summary})");
            }
        }

        return string.Join("; ", parts.Take(24));
    }

    private static bool TryDecodeCtdaForGame(
        ParsedSubrecord subrecord,
        BethesdaGame game,
        out DialogueCondition condition,
        out ConditionSubrecord physical)
    {
        if (!CtdaParser.IsSupportedBodyLength(game, subrecord.Data.Length))
        {
            condition = null!;
            physical = null!;
            return false;
        }

        return CtdaParser.TryDecode(subrecord.Data, subrecord.BigEndian, out condition, out physical);
    }

    private static string GetCtdaLayoutStatus(BethesdaGame game, int bodyLength)
    {
        return CtdaParser.GetLayoutStatus(game, bodyLength);
    }

    private static string FormatInfoFlags(ParsedMainRecord info)
    {
        var data = info.Subrecords.FirstOrDefault(s => s.Signature == "DATA" && s.Data.Length >= 4)?.Data;
        return data is null ? string.Empty : $"0x{data[2]:X2}/0x{data[3]:X2}";
    }

    private sealed record TargetRecordMatch(EsmScriptFormIdInfo Info, string Reason);
}
