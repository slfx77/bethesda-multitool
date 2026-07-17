using System.CommandLine;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BethesdaMultitool;
using BethesdaMultitool.Core.Formats.Esm.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Builds a human-reviewable inventory of INFOs quarantined by the corpus conversion
///     gate. Coverage and reasons come from structured JSONL events; dialogue identity and
///     direct NAM1 text come from the source DMPs; configured transcriber CSVs provide the
///     same deterministic fallback text used by conversion. No plugin rebuild is required.
/// </summary>
internal static partial class CorpusSuppressionReportCommand
{
    private const string QuestVariableSuppressionCode = "quest-variable.record-suppressed";
    private const string ProducerSuppressionCode = "quest-variable.record-suppressed-no-emitted-producer";
    private const string ScriptVariableSuppressionCode = "script-variable.record-suppressed";
    private const string ScriptVariableOwnerNotEmittedCode = "script-variable.owner-not-emitted";
    private const string InlineScriptSuppressionCode = "inline-script.suppress-unsafe-owner";

    private static readonly string[] ScriptSuppressionCodes =
    [
        "script.suppress-unsafe-reference-table",
        "script.suppress-post-verdict-reference-table",
    ];

    public static Command Create()
    {
        var artifactArg = new Argument<string>("artifact-directory")
        {
            Description = "Certified corpus artifact directory containing corpus-build-inputs.json and *.events.jsonl",
        };
        var outputOpt = new Option<string?>("--output-directory")
        {
            Description = "Report output directory (default: <artifact-directory>/suppression-report)",
        };
        var skipDmpScanOpt = new Option<bool>("--skip-dmp-scan")
        {
            Description = "Use event metadata and CSVs only; faster, but legacy event logs may lack prompt/topic identity",
        };

        var command = new Command(
            "corpus-suppressions",
            "List corpus-wide quarantined dialogue lines with suppression reasons, identities, and dump coverage");
        command.Arguments.Add(artifactArg);
        command.Options.Add(outputOpt);
        command.Options.Add(skipDmpScanOpt);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var artifactDirectory = parseResult.GetValue(artifactArg)!;
            var outputDirectory = parseResult.GetValue(outputOpt);
            var skipDmpScan = parseResult.GetValue(skipDmpScanOpt);
            await RunAsync(artifactDirectory, outputDirectory, skipDmpScan, cancellationToken);
        });
        return command;
    }

    private static async Task RunAsync(
        string artifactDirectory,
        string? outputDirectory,
        bool skipDmpScan,
        CancellationToken cancellationToken)
    {
        artifactDirectory = Path.GetFullPath(artifactDirectory);
        var manifestPath = Path.Combine(artifactDirectory, "corpus-build-inputs.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("Corpus build-input manifest was not found.", manifestPath);
        }

        var manifest = LoadManifest(manifestPath);
        var occurrences = LoadOccurrences(artifactDirectory, manifest.Dumps);
        var infoOccurrences = occurrences
            .Where(occurrence => occurrence.RecordType == "INFO"
                                 && IsTrackedSuppression(occurrence.RecordType, occurrence.Code))
            .ToArray();
        var scriptOccurrences = occurrences
            .Where(occurrence => occurrence.RecordType == "SCPT"
                                 && IsTrackedSuppression(occurrence.RecordType, occurrence.Code))
            .ToArray();
        if (infoOccurrences.Length == 0 && scriptOccurrences.Length == 0)
        {
            throw new InvalidDataException("No structured suppressed-INFO or suppressed-SCPT events were found.");
        }

        var scriptRows = BuildScriptRows(scriptOccurrences);

        AnsiConsole.MarkupLine(
            $"[cyan]Suppressed INFOs:[/] {infoOccurrences.Select(item => item.FormId).Distinct().Count():N0} " +
            $"distinct across {infoOccurrences.Select(item => item.DumpName).Distinct().Count():N0} dump(s)");
        AnsiConsole.MarkupLine(
            $"[cyan]Suppressed SCPTs:[/] {scriptRows.Count:N0} " +
            $"distinct source script(s) across " +
            $"{scriptOccurrences.Select(item => item.DumpName).Distinct().Count():N0} dump(s)");

        var csvCatalog = DialogueCsvCatalog.Load(manifest.DialogueCsvPaths);
        var csvMetadata = LoadCsvMetadata(manifest.DialogueCsvPaths);
        var csvLines = csvCatalog.SelectedRows
            .Select(row => new CsvDialogueLine(
                row.FormId,
                row.ResponseNumber,
                row.Text,
                csvMetadata.GetValueOrDefault((Path.GetFullPath(row.CsvPath), row.RowOrder))?.Speaker,
                csvMetadata.GetValueOrDefault((Path.GetFullPath(row.CsvPath), row.RowOrder))?.Quest,
                row.CsvOrder,
                row.RowOrder))
            .ToArray();

        var master = await LoadEsmAsync(manifest.MasterEsmPath, cancellationToken);
        var names = MasterNames.From(master);
        var captures = new List<InfoCapture>();
        if (!skipDmpScan)
        {
            var idsByDump = infoOccurrences
                .GroupBy(occurrence => occurrence.DumpName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(item => item.FormId).ToHashSet(),
                    StringComparer.OrdinalIgnoreCase);
            var dumpsToScan = manifest.Dumps
                .Where(dump => idsByDump.ContainsKey(dump.Name))
                .ToArray();

            for (var index = 0; index < dumpsToScan.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dump = dumpsToScan[index];
                AnsiConsole.MarkupLine(
                    $"[grey]Scanning {index + 1}/{dumpsToScan.Length}: {Markup.Escape(dump.Name)}[/]");
                var loaded = await DmpScriptCommands.LoadDumpAsync(dump.Path);
                if (loaded is null)
                {
                    throw new InvalidDataException($"Could not parse suppression source dump '{dump.Path}'.");
                }

                captures.AddRange(CaptureInfos(
                    dump.Name,
                    loaded.Value.Collection,
                    idsByDump[dump.Name],
                    names));
            }
        }

        // Newer schema-v1 event logs carry the same fields directly. Add them even after a
        // DMP scan: the builder deduplicates exact snapshots and they cover any parser gap.
        captures.AddRange(infoOccurrences
            .Select(occurrence => TryCaptureFromEventMetadata(occurrence, names))
            .OfType<InfoCapture>());

        var rows = BuildDialogueRows(infoOccurrences, captures, csvLines, names.ScriptEditorIds);
        var expectedInfoIds = infoOccurrences.Select(item => item.FormId).Distinct().Order().ToArray();
        var reportedInfoIds = rows.Select(row => row.InfoFormId).Distinct().Order().ToArray();
        if (!expectedInfoIds.SequenceEqual(reportedInfoIds))
        {
            var missing = expectedInfoIds.Except(reportedInfoIds).Select(id => $"0x{id:X8}");
            throw new InvalidDataException(
                $"Report did not reconcile to the structured suppression gate; missing: {string.Join(", ", missing)}.");
        }
        outputDirectory = Path.GetFullPath(outputDirectory
                                           ?? Path.Combine(artifactDirectory, "suppression-report"));
        Directory.CreateDirectory(outputDirectory);
        var csvPath = Path.Combine(outputDirectory, "suppressed-dialogue-lines.csv");
        var markdownPath = Path.Combine(outputDirectory, "suppressed-dialogue-lines.md");
        var scriptCsvPath = Path.Combine(outputDirectory, "suppressed-scripts.csv");
        var scriptMarkdownPath = Path.Combine(outputDirectory, "suppressed-scripts.md");
        WriteDialogueCsv(csvPath, rows);
        WriteDialogueMarkdown(
            markdownPath,
            rows,
            infoOccurrences,
            occurrences.Count(item => item.Code is QuestVariableSuppressionCode
                or ScriptVariableSuppressionCode or ScriptVariableOwnerNotEmittedCode),
            occurrences.Count(item => item.Code == ProducerSuppressionCode),
            occurrences.Count(item => item.Code == InlineScriptSuppressionCode),
            skipDmpScan);
        WriteScriptCsv(scriptCsvPath, scriptRows);
        WriteScriptMarkdown(scriptMarkdownPath, scriptRows, scriptOccurrences);

        AnsiConsole.MarkupLine($"[green]Wrote:[/] {Markup.Escape(csvPath)}");
        AnsiConsole.MarkupLine($"[green]Wrote:[/] {Markup.Escape(markdownPath)}");
        AnsiConsole.MarkupLine($"[green]Wrote:[/] {Markup.Escape(scriptCsvPath)}");
        AnsiConsole.MarkupLine($"[green]Wrote:[/] {Markup.Escape(scriptMarkdownPath)}");
        AnsiConsole.MarkupLine(
            $"[cyan]Rows:[/] {rows.Count:N0} response variant(s) for " +
            $"{rows.Select(row => row.InfoFormId).Distinct().Count():N0} INFOs");
    }

    internal static IReadOnlyList<DialogueReportRow> BuildDialogueRows(
        IReadOnlyList<SuppressionOccurrence> occurrences,
        IReadOnlyList<InfoCapture> captures,
        IReadOnlyList<CsvDialogueLine> csvLines,
        IReadOnlyDictionary<uint, string?> targetScriptEditorIds)
    {
        var capturesByDumpAndInfo = captures
            .GroupBy(capture => (capture.DumpName.ToUpperInvariant(), capture.InfoFormId))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var csvByInfo = csvLines
            .GroupBy(line => line.InfoFormId)
            .ToDictionary(group => group.Key, group => group
                .OrderBy(line => line.ResponseNumber)
                .ThenBy(line => line.CsvOrder)
                .ThenBy(line => line.RowOrder)
                .ToArray());
        var aggregates = new Dictionary<(uint InfoId, byte Response, string Text), MutableDialogueRow>();

        foreach (var occurrenceGroup in occurrences
                     .Where(item => item.RecordType == "INFO")
                     .GroupBy(item => (item.DumpName.ToUpperInvariant(), item.FormId))
                     .OrderBy(group => group.Key.Item1, StringComparer.Ordinal)
                     .ThenBy(group => group.Key.FormId))
        {
            var dumpName = occurrenceGroup.First().DumpName;
            var infoId = occurrenceGroup.Key.FormId;
            var conditionDetails = occurrenceGroup
                .Select(ParseConditionDetail)
                .Distinct()
                .ToArray();
            var infoCaptures = capturesByDumpAndInfo.GetValueOrDefault(occurrenceGroup.Key) ?? [];
            var fallbackLines = csvByInfo.GetValueOrDefault(infoId) ?? [];
            var directByResponse = infoCaptures
                .SelectMany(capture => capture.Responses)
                .Where(response => IsUsableText(response.Text))
                .GroupBy(response => response.ResponseNumber)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(response => response.Text.Trim()).Distinct(StringComparer.Ordinal).ToArray());
            var slots = directByResponse.Keys
                .Concat(fallbackLines.Select(line => line.ResponseNumber))
                .Distinct()
                .Order()
                .ToArray();
            if (slots.Length == 0)
            {
                slots = [0];
            }

            foreach (var responseNumber in slots)
            {
                var texts = directByResponse.GetValueOrDefault(responseNumber);
                var source = "DMP NAM1";
                if (texts is null or { Length: 0 })
                {
                    texts = fallbackLines
                        .Where(line => line.ResponseNumber == responseNumber && IsUsableText(line.Text))
                        .Select(line => line.Text.Trim())
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    source = texts.Length > 0 ? "CSV fallback" : "Unavailable";
                }

                if (texts.Length == 0)
                {
                    texts = ["(no response text captured or selected)"];
                }

                foreach (var text in texts)
                {
                    var key = (infoId, responseNumber, text);
                    if (!aggregates.TryGetValue(key, out var aggregate))
                    {
                        aggregate = new MutableDialogueRow(infoId, responseNumber, text);
                        aggregates.Add(key, aggregate);
                    }

                    aggregate.TextSources.Add(source);
                    aggregate.Dumps.Add(dumpName);
                    foreach (var capture in infoCaptures)
                    {
                        aggregate.EditorIds.AddIfPresent(capture.EditorId);
                        aggregate.Prompts.AddIfPresent(capture.Prompt);
                        aggregate.TopicFormIds.AddIfPresent(FormatFormId(capture.TopicFormId));
                        aggregate.TopicNames.AddIfPresent(capture.TopicName);
                        aggregate.QuestFormIds.AddIfPresent(FormatFormId(capture.QuestFormId));
                        aggregate.QuestNames.AddIfPresent(capture.QuestName);
                        aggregate.SpeakerFormIds.AddIfPresent(FormatFormId(capture.SpeakerFormId));
                        aggregate.SpeakerNames.AddIfPresent(capture.SpeakerName);
                    }

                    foreach (var fallback in fallbackLines.Where(line => line.ResponseNumber == responseNumber))
                    {
                        aggregate.SpeakerNames.AddIfPresent(fallback.Speaker);
                        aggregate.QuestNames.AddIfPresent(fallback.Quest);
                    }

                    foreach (var detail in conditionDetails)
                    {
                        aggregate.TargetFormIds.AddIfPresent(FormatFormId(detail.TargetFormId));
                        aggregate.TargetScriptFormIds.AddIfPresent(FormatFormId(detail.TargetScriptFormId));
                        if (detail.TargetScriptFormId is { } scriptId
                            && targetScriptEditorIds.TryGetValue(scriptId, out var scriptEditorId))
                        {
                            aggregate.TargetScriptEditorIds.AddIfPresent(scriptEditorId);
                        }

                        if (detail.VariableIndex.HasValue)
                        {
                            aggregate.MissingVariableIds.Add(detail.VariableIndex.Value.ToString(
                                CultureInfo.InvariantCulture));
                        }
                        aggregate.MissingVariableNames.AddIfPresent(detail.VariableName);
                        aggregate.ReasonCodes.Add(detail.Code);
                        aggregate.Reasons.Add(detail.Reason);
                    }
                }
            }
        }

        return aggregates.Values
            .Select(aggregate => aggregate.Freeze())
            .OrderBy(row => row.InfoFormId)
            .ThenBy(row => row.ResponseNumber)
            .ThenBy(row => row.Text, StringComparer.Ordinal)
            .ToArray();
    }

    internal static IReadOnlyList<ScriptReportRow> BuildScriptRows(
        IReadOnlyList<SuppressionOccurrence> occurrences)
    {
        ArgumentNullException.ThrowIfNull(occurrences);

        var aggregates = new Dictionary<uint, MutableScriptRow>();
        foreach (var occurrence in occurrences.Where(static item => item.RecordType == "SCPT"))
        {
            var metadata = occurrence.Metadata;
            var sourceFormId = ParseFormId(metadata?.GetValueOrDefault("script-source-form-id"))
                               ?? occurrence.FormId;
            if (!aggregates.TryGetValue(sourceFormId, out var aggregate))
            {
                aggregate = new MutableScriptRow(sourceFormId);
                aggregates.Add(sourceFormId, aggregate);
            }

            aggregate.EmittedFormIds.Add(FormatFormId(
                ParseFormId(metadata?.GetValueOrDefault("script-emitted-form-id"))
                ?? occurrence.FormId)!);
            aggregate.EditorIds.AddIfPresent(metadata?.GetValueOrDefault("script-editor-id"));
            aggregate.OwnerQuestFormIds.AddIfPresent(
                NormalizeFormId(metadata?.GetValueOrDefault("script-owner-quest-form-id")));
            aggregate.ReferenceFields.AddIfPresent(metadata?.GetValueOrDefault("reference-field"));
            aggregate.ReferenceActions.AddIfPresent(metadata?.GetValueOrDefault("reference-action"));
            aggregate.TargetSourceFormIds.AddIfPresent(
                NormalizeFormId(metadata?.GetValueOrDefault("target-source-form-id")));
            aggregate.TargetEmittedFormIds.AddIfPresent(
                NormalizeFormId(metadata?.GetValueOrDefault("target-emitted-form-id")));
            aggregate.ReasonCodes.Add(occurrence.Code);
            aggregate.Reasons.AddIfPresent(occurrence.Message);
            aggregate.Dumps.Add(occurrence.DumpName);
        }

        return aggregates.Values
            .Select(static aggregate => aggregate.Freeze())
            .OrderBy(static row => row.SourceFormId)
            .ToArray();
    }

    private static IEnumerable<InfoCapture> CaptureInfos(
        string dumpName,
        RecordCollection collection,
        IReadOnlySet<uint> suppressedInfoIds,
        MasterNames masterNames)
    {
        var topicNames = collection.DialogTopics
            .GroupBy(topic => topic.FormId)
            .ToDictionary(group => group.Key, group => BestName(
                group.Select(topic => topic.EditorId), group.Select(topic => topic.FullName)));
        var questNames = collection.Quests
            .GroupBy(quest => quest.FormId)
            .ToDictionary(group => group.Key, group => BestName(
                group.Select(quest => quest.EditorId), group.Select(quest => quest.FullName)));
        var speakerNames = BuildSpeakerNames(collection);

        foreach (var info in collection.Dialogues.Where(info => suppressedInfoIds.Contains(info.FormId)))
        {
            var topicId = info.RawParentTopicFormId ?? info.TopicFormId;
            var speakerId = info.SpeakerFormId
                            ?? info.SpeakerFactionFormId
                            ?? info.SpeakerRaceFormId
                            ?? info.SpeakerVoiceTypeFormId;
            yield return new InfoCapture(
                dumpName,
                info.FormId,
                info.EditorId,
                info.PromptText,
                topicId,
                ResolveName(topicId, topicNames, masterNames.TopicNames),
                info.QuestFormId,
                ResolveName(info.QuestFormId, questNames, masterNames.QuestNames),
                speakerId,
                ResolveName(speakerId, speakerNames, masterNames.SpeakerNames),
                info.Responses.Select((response, index) => new CapturedResponse(
                    response.ResponseNumber > 0 ? response.ResponseNumber : checked((byte)(index + 1)),
                    response.Text ?? string.Empty)).ToArray());
        }
    }

    internal static InfoCapture? TryCaptureFromEventMetadata(
        SuppressionOccurrence occurrence,
        MasterNames masterNames)
    {
        var metadata = occurrence.Metadata;
        if (metadata is null || metadata.Count == 0)
        {
            return null;
        }

        var responses = new List<CapturedResponse>();
        for (var index = 0; ; index++)
        {
            var prefix = $"info-response-{index:D3}";
            if (!metadata.TryGetValue($"{prefix}-number", out var numberText))
            {
                break;
            }

            if (byte.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                responses.Add(new CapturedResponse(
                    number > 0 ? number : checked((byte)(index + 1)),
                    metadata.GetValueOrDefault($"{prefix}-text") ?? string.Empty));
            }
        }

        var topicId = ParseFormId(metadata.GetValueOrDefault("info-topic-form-id"));
        var questId = ParseFormId(metadata.GetValueOrDefault("info-quest-form-id"));
        var speakerId = ParseFormId(metadata.GetValueOrDefault("info-speaker-scope-form-id"))
                        ?? ParseFormId(metadata.GetValueOrDefault("info-speaker-form-id"))
                        ?? ParseFormId(metadata.GetValueOrDefault("info-speaker-faction-form-id"))
                        ?? ParseFormId(metadata.GetValueOrDefault("info-speaker-race-form-id"))
                        ?? ParseFormId(metadata.GetValueOrDefault("info-speaker-voice-type-form-id"));
        return new InfoCapture(
            occurrence.DumpName,
            occurrence.FormId,
            metadata.GetValueOrDefault("record-editor-id"),
            metadata.GetValueOrDefault("info-prompt"),
            topicId,
            (topicId is { } capturedTopic
                ? masterNames.TopicNames.GetValueOrDefault(capturedTopic)
                : null)
            ?? BestName(
                [metadata.GetValueOrDefault("info-topic-editor-id")],
                [metadata.GetValueOrDefault("info-topic-name")]),
            questId,
            (questId is { } capturedQuest
                ? masterNames.QuestNames.GetValueOrDefault(capturedQuest)
                : null)
            ?? BestName(
                [metadata.GetValueOrDefault("info-quest-editor-id")],
                [metadata.GetValueOrDefault("info-quest-name")]),
            speakerId,
            (speakerId is { } capturedSpeaker
                ? masterNames.SpeakerNames.GetValueOrDefault(capturedSpeaker)
                : null)
            ?? BestName(
                [metadata.GetValueOrDefault("info-speaker-editor-id")],
                [metadata.GetValueOrDefault("info-speaker-name")]),
            responses);
    }

    private static ConditionDetail ParseConditionDetail(SuppressionOccurrence occurrence)
    {
        var metadata = occurrence.Metadata;
        if (metadata is not null && metadata.Count > 0)
        {
            if (occurrence.Code == InlineScriptSuppressionCode)
            {
                return new ConditionDetail(
                    occurrence.Code,
                    ParseFormId(metadata.GetValueOrDefault("target-source-form-id")),
                    null,
                    ParseDecimalUInt32(metadata.GetValueOrDefault("local-variable-id")),
                    null,
                    occurrence.Message);
            }

            if (occurrence.Code == ProducerSuppressionCode)
            {
                return new ConditionDetail(
                    occurrence.Code,
                    ParseFormId(metadata.GetValueOrDefault("target-quest-form-id")),
                    ParseFormId(metadata.GetValueOrDefault("target-script-form-id")),
                    ParseDecimalUInt32(metadata.GetValueOrDefault("target-variable-index")),
                    metadata.GetValueOrDefault("target-variable-name"),
                    occurrence.Message);
            }

            return new ConditionDetail(
                occurrence.Code,
                ParseFormId(metadata.GetValueOrDefault("condition-target-form-id")),
                ParseFormId(metadata.GetValueOrDefault("condition-target-script-form-id")),
                ParseDecimalUInt32(metadata.GetValueOrDefault("condition-variable-index")),
                metadata.GetValueOrDefault("condition-variable-name"),
                occurrence.Message);
        }

        // Backward-compatible adapter for certified schema-v1 logs written before optional
        // Metadata existed. This is deliberately confined to one versioned legacy path.
        var match = LegacyQuestVariableMessage().Match(occurrence.Message);
        if (!match.Success)
        {
            return new ConditionDetail(occurrence.Code, null, null, null, null, occurrence.Message);
        }

        _ = uint.TryParse(match.Groups["target"].Value, NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out var target);
        _ = uint.TryParse(match.Groups["script"].Value, NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out var script);
        _ = uint.TryParse(match.Groups["index"].Value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var index);
        var variable = match.Groups["variable"].Value.Trim();
        return new ConditionDetail(
            occurrence.Code,
            target == 0 ? null : target,
            script == 0 ? null : script,
            index == 0 ? null : index,
            variable.StartsWith("ID ", StringComparison.Ordinal) ? null : variable,
            occurrence.Message);
    }

    [GeneratedRegex(
        @"Target=0x(?<target>[0-9A-Fa-f]{8});\s*variable=(?<variable>.*?)\s*\(ID\s+(?<index>\d+)\);\s*target SCPT=0x(?<script>[0-9A-Fa-f]{8})\.",
        RegexOptions.CultureInvariant)]
    private static partial Regex LegacyQuestVariableMessage();

    private static IReadOnlyList<SuppressionOccurrence> LoadOccurrences(
        string artifactDirectory,
        IReadOnlyList<ManifestDump> dumps)
    {
        var result = new List<SuppressionOccurrence>();
        foreach (var dump in dumps)
        {
            var eventPath = Path.Combine(
                artifactDirectory,
                $"{Path.GetFileNameWithoutExtension(dump.Name)}.events.jsonl");
            if (!File.Exists(eventPath))
            {
                throw new FileNotFoundException($"Structured event log for '{dump.Name}' is missing.", eventPath);
            }

            var lineNumber = 0;
            foreach (var line in File.ReadLines(eventPath))
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (root.GetProperty("Kind").GetString() != "Event"
                    || root.GetProperty("Code").GetString() is not { } code)
                {
                    continue;
                }

                var recordType = root.GetProperty("FormType").GetString() ?? string.Empty;
                if (!IsTrackedSuppression(recordType, code))
                {
                    continue;
                }

                var formIdText = root.GetProperty("FormId").GetString();
                var formId = ParseFormId(formIdText)
                             ?? throw new InvalidDataException(
                                 $"Invalid FormId in {eventPath}:{lineNumber}: '{formIdText}'.");
                Dictionary<string, string?>? metadata = null;
                if (root.TryGetProperty("Metadata", out var metadataElement))
                {
                    metadata = metadataElement.EnumerateObject().ToDictionary(
                        property => property.Name,
                        property => property.Value.ValueKind == JsonValueKind.Null
                            ? null
                            : property.Value.GetString(),
                        StringComparer.Ordinal);
                }

                result.Add(new SuppressionOccurrence(
                    dump.Name,
                    recordType,
                    formId,
                    code,
                    root.GetProperty("Message").GetString() ?? string.Empty,
                    metadata));
            }
        }

        return result;
    }

    internal static bool IsTrackedSuppression(string recordType, string code)
    {
        if (code is QuestVariableSuppressionCode or ProducerSuppressionCode
            or ScriptVariableSuppressionCode or ScriptVariableOwnerNotEmittedCode)
        {
            return recordType is "INFO" or "PACK";
        }

        if (code == InlineScriptSuppressionCode)
        {
            return recordType is "INFO" or "PACK" or "TERM";
        }

        return recordType == "SCPT"
               && ScriptSuppressionCodes.Contains(code, StringComparer.Ordinal);
    }

    private static CorpusManifest LoadManifest(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;
        var master = root.GetProperty("MasterEsm").GetProperty("Path").GetString()
                     ?? throw new InvalidDataException("Manifest MasterEsm.Path is missing.");
        var csvPaths = new[]
            {
                root.GetProperty("JulyDialogueCsv").GetProperty("Path").GetString(),
                root.GetProperty("AprilDialogueCsv").GetProperty("Path").GetString(),
            }
            .Where(pathValue => !string.IsNullOrWhiteSpace(pathValue))
            .Select(pathValue => Path.GetFullPath(pathValue!))
            .ToArray();
        var dumps = root.GetProperty("Dumps").EnumerateArray()
            .Select(element => new ManifestDump(
                element.GetProperty("Name").GetString()
                ?? throw new InvalidDataException("Manifest dump name is missing."),
                element.GetProperty("Path").GetString()
                ?? throw new InvalidDataException("Manifest dump path is missing.")))
            .ToArray();
        return new CorpusManifest(Path.GetFullPath(master), csvPaths, dumps);
    }

    private static async Task<RecordCollection> LoadEsmAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var result = await EsmFileAnalyzer.AnalyzeAsync(path, cancellationToken: cancellationToken);
        if (result.EsmRecords is null)
        {
            throw new InvalidDataException($"No records could be parsed from master ESM '{path}'.");
        }

        using var mmf = MemoryMappedFile.CreateFromFile(
            path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        return new RecordParser(
            result.EsmRecords,
            result.FormIdMap,
            accessor,
            result.FileSize).ParseAll();
    }

    private static IReadOnlyDictionary<(string CsvPath, int RowOrder), CsvMetadata> LoadCsvMetadata(
        IReadOnlyList<string> csvPaths)
    {
        var result = new Dictionary<(string, int), CsvMetadata>();
        foreach (var csvPath in csvPaths)
        {
            using var reader = new StreamReader(csvPath);
            var header = DialogueAudioCsvReader.ReadCsvRecord(reader);
            var speakerColumn = DialogueAudioCsvReader.FindColumn(header, "Speaker");
            var questColumn = DialogueAudioCsvReader.FindColumn(header, "Quest");
            var rowOrder = 0;
            while (!reader.EndOfStream)
            {
                var fields = DialogueAudioCsvReader.ReadCsvRecord(reader);
                if (fields.Count == 0)
                {
                    continue;
                }

                rowOrder++;
                result[(Path.GetFullPath(csvPath), rowOrder)] = new CsvMetadata(
                    GetField(fields, speakerColumn),
                    GetField(fields, questColumn));
            }
        }

        return result;
    }

    private static string? GetField(IReadOnlyList<string> fields, int index) =>
        index >= 0 && index < fields.Count && !string.IsNullOrWhiteSpace(fields[index])
            ? fields[index].Trim()
            : null;

    private static void WriteDialogueCsv(string path, IReadOnlyList<DialogueReportRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "InfoFormId,ResponseNumber,ResponseText,TextSource,InfoEditorId,Prompt," +
            "TopicFormId,TopicName,QuestFormId,QuestName,SpeakerFormId,SpeakerName," +
            "TargetFormId,TargetScptFormId,TargetScptEditorId,MissingVariableId," +
            "MissingVariableName,ReasonCode,Reason,DumpCount,Dumps");
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(",", new[]
            {
                $"0x{row.InfoFormId:X8}",
                row.ResponseNumber.ToString(CultureInfo.InvariantCulture),
                row.Text,
                row.TextSources,
                row.EditorIds,
                row.Prompts,
                row.TopicFormIds,
                row.TopicNames,
                row.QuestFormIds,
                row.QuestNames,
                row.SpeakerFormIds,
                row.SpeakerNames,
                row.TargetFormIds,
                row.TargetScriptFormIds,
                row.TargetScriptEditorIds,
                row.MissingVariableIds,
                row.MissingVariableNames,
                row.ReasonCodes,
                row.Reasons,
                row.DumpCount.ToString(CultureInfo.InvariantCulture),
                row.Dumps,
            }.Select(CsvEscape)));
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    private static void WriteDialogueMarkdown(
        string path,
        IReadOnlyList<DialogueReportRow> rows,
        IReadOnlyList<SuppressionOccurrence> occurrences,
        int allQuestVariableDiagnosticCount,
        int allProducerDiagnosticCount,
        int allInlineScriptDiagnosticCount,
        bool skippedDmpScan)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Corpus skipped dialogue lines");
        builder.AppendLine();
        builder.AppendLine($"- Distinct INFOs: {rows.Select(row => row.InfoFormId).Distinct().Count():N0}");
        builder.AppendLine($"- Response variants: {rows.Count:N0}");
        builder.AppendLine($"- Unique dump + INFO suppressions: " +
                           $"{occurrences.Select(item => (item.DumpName, item.FormId)).Distinct().Count():N0}");
        builder.AppendLine($"- INFO suppression diagnostics: {occurrences.Count:N0}");
        builder.AppendLine($"- INFO + PACK invalid-condition diagnostics: {allQuestVariableDiagnosticCount:N0}");
        builder.AppendLine($"- INFO + PACK missing-producer diagnostics: {allProducerDiagnosticCount:N0}");
        builder.AppendLine($"- INFO + PACK + TERM inline-script diagnostics: {allInlineScriptDiagnosticCount:N0}");
        builder.AppendLine($"- Suppression-bearing dumps: " +
                           $"{occurrences.Select(item => item.DumpName).Distinct(StringComparer.OrdinalIgnoreCase).Count():N0}");
        builder.AppendLine($"- DMP scan: {(skippedDmpScan ? "skipped" : "enabled")}");
        builder.AppendLine();
        builder.AppendLine("The converter suppresses the whole INFO when its conditions or atomic inline-script " +
                           "bundle cannot be emitted safely. Invalid conditions are not deleted because that " +
                           "would make the line unconditional; inline SCDA/SLSD/SCRO/SCRV slots are not dropped " +
                           "or zero-filled because bytecode addresses them by position.");

        foreach (var infoGroup in rows.GroupBy(row => row.InfoFormId).OrderBy(group => group.Key))
        {
            var first = infoGroup.First();
            var infoDumps = occurrences
                .Where(item => item.FormId == infoGroup.Key)
                .Select(item => item.DumpName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            builder.AppendLine();
            builder.AppendLine($"## 0x{infoGroup.Key:X8}");
            builder.AppendLine();
            builder.AppendLine($"- Speaker: {DisplayOrUnknown(first.SpeakerNames)}");
            builder.AppendLine($"- Quest: {DisplayOrUnknown(first.QuestNames)}");
            builder.AppendLine($"- Topic: {DisplayOrUnknown(first.TopicNames)} ({DisplayOrUnknown(first.TopicFormIds)})");
            builder.AppendLine($"- Prompt: {DisplayOrUnknown(first.Prompts)}");
            if (!string.IsNullOrWhiteSpace(first.MissingVariableIds)
                || !string.IsNullOrWhiteSpace(first.MissingVariableNames))
            {
                builder.AppendLine($"- Missing variable: {DisplayOrUnknown(first.MissingVariableNames)} " +
                                   $"(ID {DisplayOrUnknown(first.MissingVariableIds)}) in " +
                                   $"{DisplayOrUnknown(first.TargetScriptEditorIds)} " +
                                   $"{DisplayOrUnknown(first.TargetScriptFormIds)}");
            }
            builder.AppendLine($"- Suppression code: {DisplayOrUnknown(first.ReasonCodes)}");
            builder.AppendLine($"- Coverage: {infoDumps.Length:N0} dump(s): {string.Join(", ", infoDumps)}");
            builder.AppendLine();
            foreach (var row in infoGroup)
            {
                builder.AppendLine($"  {row.ResponseNumber}. {row.Text.ReplaceLineEndings(" ")}");
            }
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    private static void WriteScriptCsv(string path, IReadOnlyList<ScriptReportRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "SourceFormId,EmittedFormIds,EditorIds,OwnerQuestFormIds,ReferenceFields," +
            "ReferenceActions,TargetSourceFormIds,TargetEmittedFormIds,ReasonCodes,Reasons,DumpCount,Dumps");
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(",", new[]
            {
                $"0x{row.SourceFormId:X8}",
                row.EmittedFormIds,
                row.EditorIds,
                row.OwnerQuestFormIds,
                row.ReferenceFields,
                row.ReferenceActions,
                row.TargetSourceFormIds,
                row.TargetEmittedFormIds,
                row.ReasonCodes,
                row.Reasons,
                row.DumpCount.ToString(CultureInfo.InvariantCulture),
                row.Dumps,
            }.Select(CsvEscape)));
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    private static void WriteScriptMarkdown(
        string path,
        IReadOnlyList<ScriptReportRow> rows,
        IReadOnlyList<SuppressionOccurrence> occurrences)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Corpus suppressed scripts");
        builder.AppendLine();
        builder.AppendLine($"- Distinct source SCPTs: {rows.Count:N0}");
        builder.AppendLine($"- Suppression diagnostics: {occurrences.Count:N0}");
        builder.AppendLine($"- Suppression-bearing dumps: " +
                           $"{occurrences.Select(item => item.DumpName).Distinct(StringComparer.OrdinalIgnoreCase).Count():N0}");
        builder.AppendLine();
        builder.AppendLine("Each entry is a same-dump script identity reported by the converter. " +
                           "A script is quarantined as one atomic SCDA/SLSD/SCRO/SCRV bundle when a " +
                           "referenced target cannot survive the final emit plan.");

        foreach (var row in rows)
        {
            builder.AppendLine();
            builder.AppendLine($"## {DisplayOrUnknown(row.EditorIds)} (0x{row.SourceFormId:X8})");
            builder.AppendLine();
            builder.AppendLine($"- Emitted FormID(s): {DisplayOrUnknown(row.EmittedFormIds)}");
            builder.AppendLine($"- Owner quest(s): {DisplayOrUnknown(row.OwnerQuestFormIds)}");
            builder.AppendLine($"- Failed reference(s): {DisplayOrUnknown(row.ReferenceFields)}");
            builder.AppendLine($"- Target source FormID(s): {DisplayOrUnknown(row.TargetSourceFormIds)}");
            builder.AppendLine($"- Target emitted FormID(s): {DisplayOrUnknown(row.TargetEmittedFormIds)}");
            builder.AppendLine($"- Suppression code(s): {DisplayOrUnknown(row.ReasonCodes)}");
            builder.AppendLine($"- Coverage: {row.DumpCount:N0} dump(s): {DisplayOrUnknown(row.Dumps)}");
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    private static string DisplayOrUnknown(string value) =>
        string.IsNullOrWhiteSpace(value) ? "(unknown)" : value;

    private static string CsvEscape(string? value)
    {
        value ??= string.Empty;
        return value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static bool IsUsableText(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && !string.Equals(text, DialogueTextBackfill.PlaceholderText, StringComparison.Ordinal);

    private static string? ResolveName(
        uint? formId,
        IReadOnlyDictionary<uint, string?> local,
        IReadOnlyDictionary<uint, string?> master) =>
        formId is { } id
            // Retail identity is authoritative. Runtime DIAL/FULL fields in old dumps can
            // contain transient prompt text, so DMP names are only a prototype-only fallback.
            ? master.GetValueOrDefault(id) ?? local.GetValueOrDefault(id)
            : null;

    private static string? BestName(IEnumerable<string?> editorIds, IEnumerable<string?> fullNames) =>
        fullNames.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
        ?? editorIds.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static IReadOnlyDictionary<uint, string?> BuildSpeakerNames(RecordCollection collection)
    {
        var result = new Dictionary<uint, string?>();
        foreach (var group in collection.Npcs.GroupBy(record => record.FormId))
        {
            result.TryAdd(group.Key, BestName(
                group.Select(record => record.EditorId), group.Select(record => record.FullName)));
        }

        foreach (var group in collection.Factions.GroupBy(record => record.FormId))
        {
            var name = BestName(
                group.Select(record => record.EditorId), group.Select(record => record.FullName));
            result.TryAdd(group.Key, string.IsNullOrWhiteSpace(name) ? null : $"Faction: {name}");
        }

        foreach (var group in collection.Races.GroupBy(record => record.FormId))
        {
            var name = BestName(
                group.Select(record => record.EditorId), group.Select(record => record.FullName));
            result.TryAdd(group.Key, string.IsNullOrWhiteSpace(name) ? null : $"Race: {name}");
        }

        foreach (var group in collection.VoiceTypes.GroupBy(record => record.FormId))
        {
            var name = group.Select(record => record.EditorId)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            result.TryAdd(group.Key, string.IsNullOrWhiteSpace(name) ? null : $"Voice: {name}");
        }

        return result;
    }

    private static string? FormatFormId(uint? formId) =>
        formId.HasValue ? $"0x{formId.Value:X8}" : null;

    private static uint? ParseFormId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        return uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var formId)
            ? formId
            : null;
    }

    private static string? NormalizeFormId(string? value) => FormatFormId(ParseFormId(value));

    private static uint? ParseDecimalUInt32(string? value) =>
        uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private sealed record CorpusManifest(
        string MasterEsmPath,
        IReadOnlyList<string> DialogueCsvPaths,
        IReadOnlyList<ManifestDump> Dumps);

    internal sealed record ManifestDump(string Name, string Path);

    internal sealed record SuppressionOccurrence(
        string DumpName,
        string RecordType,
        uint FormId,
        string Code,
        string Message,
        IReadOnlyDictionary<string, string?>? Metadata);

    internal sealed record CapturedResponse(byte ResponseNumber, string Text);

    internal sealed record InfoCapture(
        string DumpName,
        uint InfoFormId,
        string? EditorId,
        string? Prompt,
        uint? TopicFormId,
        string? TopicName,
        uint? QuestFormId,
        string? QuestName,
        uint? SpeakerFormId,
        string? SpeakerName,
        IReadOnlyList<CapturedResponse> Responses);

    internal sealed record CsvDialogueLine(
        uint InfoFormId,
        byte ResponseNumber,
        string Text,
        string? Speaker,
        string? Quest,
        int CsvOrder,
        int RowOrder);

    private sealed record CsvMetadata(string? Speaker, string? Quest);

    private sealed record ConditionDetail(
        string Code,
        uint? TargetFormId,
        uint? TargetScriptFormId,
        uint? VariableIndex,
        string? VariableName,
        string Reason);

    internal sealed record DialogueReportRow(
        uint InfoFormId,
        byte ResponseNumber,
        string Text,
        string TextSources,
        string EditorIds,
        string Prompts,
        string TopicFormIds,
        string TopicNames,
        string QuestFormIds,
        string QuestNames,
        string SpeakerFormIds,
        string SpeakerNames,
        string TargetFormIds,
        string TargetScriptFormIds,
        string TargetScriptEditorIds,
        string MissingVariableIds,
        string MissingVariableNames,
        string ReasonCodes,
        string Reasons,
        int DumpCount,
        string Dumps);

    internal sealed record ScriptReportRow(
        uint SourceFormId,
        string EmittedFormIds,
        string EditorIds,
        string OwnerQuestFormIds,
        string ReferenceFields,
        string ReferenceActions,
        string TargetSourceFormIds,
        string TargetEmittedFormIds,
        string ReasonCodes,
        string Reasons,
        int DumpCount,
        string Dumps);

    private sealed class MutableDialogueRow(uint infoFormId, byte responseNumber, string text)
    {
        public SortedSet<string> TextSources { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> EditorIds { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> Prompts { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> TopicFormIds { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> TopicNames { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> QuestFormIds { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> QuestNames { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> SpeakerFormIds { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> SpeakerNames { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> TargetFormIds { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> TargetScriptFormIds { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> TargetScriptEditorIds { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> MissingVariableIds { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> MissingVariableNames { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> ReasonCodes { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> Reasons { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> Dumps { get; } = new(StringComparer.OrdinalIgnoreCase);

        public DialogueReportRow Freeze() => new(
            infoFormId,
            responseNumber,
            text,
            Join(TextSources),
            Join(EditorIds),
            Join(Prompts),
            Join(TopicFormIds),
            Join(TopicNames),
            Join(QuestFormIds),
            Join(QuestNames),
            Join(SpeakerFormIds),
            Join(SpeakerNames),
            Join(TargetFormIds),
            Join(TargetScriptFormIds),
            Join(TargetScriptEditorIds),
            Join(MissingVariableIds),
            Join(MissingVariableNames),
            Join(ReasonCodes),
            Join(Reasons),
            Dumps.Count,
            Join(Dumps));

        private static string Join(IEnumerable<string> values) => string.Join("; ", values);
    }

    private sealed class MutableScriptRow(uint sourceFormId)
    {
        public SortedSet<string> EmittedFormIds { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> EditorIds { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> OwnerQuestFormIds { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> ReferenceFields { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> ReferenceActions { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> TargetSourceFormIds { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> TargetEmittedFormIds { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> ReasonCodes { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> Reasons { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> Dumps { get; } = new(StringComparer.OrdinalIgnoreCase);

        public ScriptReportRow Freeze() => new(
            sourceFormId,
            Join(EmittedFormIds),
            Join(EditorIds),
            Join(OwnerQuestFormIds),
            Join(ReferenceFields),
            Join(ReferenceActions),
            Join(TargetSourceFormIds),
            Join(TargetEmittedFormIds),
            Join(ReasonCodes),
            Join(Reasons),
            Dumps.Count,
            Join(Dumps));

        private static string Join(IEnumerable<string> values) => string.Join("; ", values);
    }

    internal sealed record MasterNames(
        IReadOnlyDictionary<uint, string?> TopicNames,
        IReadOnlyDictionary<uint, string?> QuestNames,
        IReadOnlyDictionary<uint, string?> SpeakerNames,
        IReadOnlyDictionary<uint, string?> ScriptEditorIds)
    {
        public static MasterNames From(RecordCollection collection) => new(
            collection.DialogTopics.GroupBy(record => record.FormId).ToDictionary(
                group => group.Key,
                group => BestName(group.Select(record => record.EditorId), group.Select(record => record.FullName))),
            collection.Quests.GroupBy(record => record.FormId).ToDictionary(
                group => group.Key,
                group => BestName(group.Select(record => record.EditorId), group.Select(record => record.FullName))),
            BuildSpeakerNames(collection),
            collection.Scripts.GroupBy(record => record.FormId).ToDictionary(
                group => group.Key,
                group => group.Select(record => record.EditorId)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))));
    }

    private static void AddIfPresent(this ISet<string> target, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            target.Add(value.Trim());
        }
    }
}
