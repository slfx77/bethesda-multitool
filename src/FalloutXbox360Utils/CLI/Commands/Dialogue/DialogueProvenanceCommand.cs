using System.CommandLine;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.CLI.Shared;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis.Helpers;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing;
using FalloutXbox360Utils.Core.Minidump;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Dialogue;

internal static class DialogueProvenanceCommand
{
    internal static Command CreateProvenanceCommand()
    {
        var command = new Command("provenance", "Inspect dump-backed INFO/DIAL provenance and TES-file recovery");

        var inputArg = new Argument<string>("input") { Description = "Path to memory dump (.dmp) file" };
        var formIdArg = new Argument<string>("formid") { Description = "INFO or DIAL FormID (hex, e.g. 0x00146E1C)" };
        var hexOpt = new Option<bool>("--hex") { Description = "Include compact hex dumps for runtime/raw evidence" };

        command.Arguments.Add(inputArg);
        command.Arguments.Add(formIdArg);
        command.Options.Add(hexOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var formIdText = parseResult.GetValue(formIdArg)!;
            var includeHex = parseResult.GetValue(hexOpt);
            await RunAsync(input, formIdText, includeHex, cancellationToken);
        });

        return command;
    }

    private static async Task RunAsync(string input, string formIdText, bool includeHex,
        CancellationToken cancellationToken)
    {
        var formId = CliHelpers.ParseFormId(formIdText) ?? 0;
        if (formId == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Invalid FormID: {0}", formIdText);
            return;
        }

        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", input);
            return;
        }

        if (!Path.GetExtension(input).Equals(".dmp", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] dialogue provenance is dump-only for now.");
            return;
        }

        var analyzer = new MinidumpAnalyzer();
        var analysisResult = await CliProgressRunner.RunWithProgressAsync(
            "Analyzing memory dump...",
            (progress, ct) => analyzer.AnalyzeAsync(input, progress, true, false, ct),
            cancellationToken);

        if (analysisResult.EsmRecords == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No ESM records found in dump.");
            return;
        }

        var fileInfo = new FileInfo(input);
        using var mmf = MemoryMappedFile.CreateFromFile(input, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        var parser = new RecordParser(
            analysisResult.EsmRecords,
            analysisResult.FormIdMap,
            accessor,
            fileInfo.Length,
            analysisResult.MinidumpInfo);
        var parsed = parser.ParseAll();
        var inspector = new DialogueProvenanceInspector(parser._context, parsed.Dialogues);

        var dialogue = parsed.Dialogues.FirstOrDefault(info => info.FormId == formId);
        if (dialogue != null)
        {
            PrintInfoReport(
                inspector.InspectInfo(dialogue, includeHex),
                parsed,
                analysisResult.FormIdMap,
                includeHex);
            return;
        }

        var topic = parsed.DialogTopics.FirstOrDefault(dial => dial.FormId == formId);
        if (topic != null)
        {
            PrintTopicReport(
                inspector.InspectTopic(topic, includeHex),
                parsed,
                analysisResult.FormIdMap,
                includeHex);
            return;
        }

        AnsiConsole.MarkupLine("[yellow]Form 0x{0:X8} was not found in parsed dialogue/topic records.[/]", formId);
    }

    private static void PrintInfoReport(
        DialogueInfoProvenanceReport report,
        RecordCollection parsed,
        Dictionary<uint, string> formIdMap,
        bool includeHex)
    {
        var dialogue = report.Dialogue;
        AnsiConsole.Write(new Rule($"[blue]INFO 0x{dialogue.FormId:X8}[/]").LeftJustified());

        var table = new Table().Border(TableBorder.Rounded).HideHeaders();
        table.AddColumn("Property");
        table.AddColumn("Value");

        table.AddRow("EditorID", Markup.Escape(dialogue.EditorId ?? "(none)"));
        table.AddRow("Prompt", Markup.Escape(dialogue.PromptText ?? "(none)"));
        table.AddRow("Quest", FormatFormRef(dialogue.QuestFormId, parsed, formIdMap));
        table.AddRow("Topic", FormatFormRef(dialogue.TopicFormId, parsed, formIdMap));
        table.AddRow("Raw Record Offset", FormatOffset(dialogue.RawRecordOffset));
        table.AddRow("Runtime Struct Offset", FormatOffset(dialogue.RuntimeStructOffset));
        table.AddRow("TES-file Offset", dialogue.TesFileOffset != 0 ? $"0x{dialogue.TesFileOffset:X8}" : "(none)");
        table.AddRow("Result Script Status", FormatRecoveryStatus(report.ResultScriptRecovery.Status));
        table.AddRow("Mapped TES VA",
            report.ResultScriptRecovery.TargetVirtualAddress.HasValue
                ? $"0x{report.ResultScriptRecovery.TargetVirtualAddress.Value:X8}"
                : "(none)");
        table.AddRow("Mapped Dump Offset",
            report.ResultScriptRecovery.MappedDumpOffset.HasValue
                ? $"0x{report.ResultScriptRecovery.MappedDumpOffset.Value:X}"
                : "(none)");
        table.AddRow("Header",
            report.ResultScriptRecovery.Signature != null
                ? $"{report.ResultScriptRecovery.Signature} / 0x{report.ResultScriptRecovery.RecordFormId ?? 0:X8}"
                : "(none)");
        table.AddRow("Link From", FormatFormList(dialogue.LinkFromTopics, parsed, formIdMap));
        table.AddRow("Link To", FormatFormList(dialogue.LinkToTopics, parsed, formIdMap));
        table.AddRow("Follow-Up INFOs", FormatFormList(dialogue.FollowUpInfos, parsed, formIdMap));
        table.AddRow("Calibrated TES Segments", report.TesFileSegments.Count.ToString());

        AnsiConsole.Write(table);

        if (report.TesFileSegments.Count > 0)
        {
            var segmentTable = new Table().Border(TableBorder.Simple);
            segmentTable.AddColumn("Base VA");
            segmentTable.AddColumn("Offset Range");
            segmentTable.AddColumn("Matches");

            foreach (var segment in report.TesFileSegments.Take(6))
            {
                segmentTable.AddRow(
                    $"0x{segment.BaseVirtualAddress:X8}",
                    $"0x{segment.MinTesFileOffset:X8} - 0x{segment.MaxTesFileOffset:X8}",
                    segment.MatchCount.ToString());
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[blue]TES-file segments:[/]");
            AnsiConsole.Write(segmentTable);
        }

        if (report.ResultScriptRecovery.Scripts.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[blue]Recovered result scripts:[/]");
            for (var i = 0; i < report.ResultScriptRecovery.Scripts.Count; i++)
            {
                var script = report.ResultScriptRecovery.Scripts[i];
                var body = script.SourceText ?? script.DecompiledText ?? "(compiled only)";
                AnsiConsole.MarkupLine($"  [yellow]{i + 1}.[/] {Markup.Escape(body)}");
            }
        }

        if (includeHex)
        {
            if (report.RuntimeStructBytes != null)
            {
                PrintHexSection("Runtime INFO Struct", report.RuntimeStructBytes, dialogue.RuntimeStructOffset);
            }

            if (report.ConversationDataBytes != null && report.ConversationDataOffset.HasValue)
            {
                PrintHexSection("TESConversationData", report.ConversationDataBytes,
                    report.ConversationDataOffset.Value);
            }

            if (report.ResultScriptRecovery.HeaderBytes != null &&
                report.ResultScriptRecovery.MappedDumpOffset.HasValue)
            {
                var bytes = report.ResultScriptRecovery.RecordDataBytes != null
                    ?
                    [
                        .. report.ResultScriptRecovery.HeaderBytes,
                        .. report.ResultScriptRecovery.RecordDataBytes.Take(128)
                    ]
                    : report.ResultScriptRecovery.HeaderBytes;
                PrintHexSection("Mapped INFO Header/Data", bytes, report.ResultScriptRecovery.MappedDumpOffset.Value);
            }
        }
    }

    private static void PrintTopicReport(
        DialogTopicProvenanceReport report,
        RecordCollection parsed,
        Dictionary<uint, string> formIdMap,
        bool includeHex)
    {
        var topic = report.Topic;
        AnsiConsole.Write(new Rule($"[blue]DIAL 0x{topic.FormId:X8}[/]").LeftJustified());

        var table = new Table().Border(TableBorder.Rounded).HideHeaders();
        table.AddColumn("Property");
        table.AddColumn("Value");

        table.AddRow("EditorID", Markup.Escape(topic.EditorId ?? "(none)"));
        table.AddRow("Text", Markup.Escape(report.DecodedText ?? topic.FullName ?? "(none)"));
        table.AddRow("Quest", FormatFormRef(topic.QuestFormId, parsed, formIdMap));
        table.AddRow("Raw Record Offset", FormatOffset(topic.RawRecordOffset));
        table.AddRow("Runtime Struct Offset", FormatOffset(topic.RuntimeStructOffset));
        table.AddRow("String Pointer", report.StringPointer.HasValue ? $"0x{report.StringPointer.Value:X8}" : "(none)");
        table.AddRow("String Length", report.StringLength.HasValue ? report.StringLength.Value.ToString() : "(none)");
        table.AddRow("String Offset", report.StringOffset.HasValue ? $"0x{report.StringOffset.Value:X}" : "(none)");

        AnsiConsole.Write(table);

        if (includeHex)
        {
            if (report.RuntimeStructBytes != null)
            {
                PrintHexSection("Runtime DIAL Struct", report.RuntimeStructBytes, topic.RuntimeStructOffset);
            }

            if (report.StringBytes != null && report.StringOffset.HasValue)
            {
                PrintHexSection("Topic String Bytes", report.StringBytes, report.StringOffset.Value);
            }
        }
    }

    private static void PrintHexSection(string title, byte[] bytes, long baseOffset)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[blue]{Markup.Escape(title)}:[/]");
        EsmDisplayHelpers.RenderHexDump(bytes, baseOffset);
    }

    private static string FormatFormRef(uint? formId, RecordCollection parsed, Dictionary<uint, string> formIdMap)
    {
        if (formId is not > 0)
        {
            return "(none)";
        }

        var label = parsed.FormIdToDisplayName.GetValueOrDefault(formId.Value)
                    ?? parsed.FormIdToEditorId.GetValueOrDefault(formId.Value)
                    ?? formIdMap.GetValueOrDefault(formId.Value)
                    ?? "?";
        return $"0x{formId.Value:X8} ({Markup.Escape(label)})";
    }

    private static string FormatFormList(
        IReadOnlyList<uint> formIds,
        RecordCollection parsed,
        Dictionary<uint, string> formIdMap)
    {
        if (formIds.Count == 0)
        {
            return "(none)";
        }

        return string.Join(", ", formIds.Select(formId => FormatFormRef(formId, parsed, formIdMap)));
    }

    private static string FormatOffset(long offset)
    {
        return offset > 0 ? $"0x{offset:X}" : "(none)";
    }

    private static string FormatRecoveryStatus(DialogueTesFileScriptRecoveryStatus status)
    {
        return status switch
        {
            DialogueTesFileScriptRecoveryStatus.NoTesFileOffset => "No TES-file offset",
            DialogueTesFileScriptRecoveryStatus.UncalibratedBase => "TES-file base not calibrated",
            DialogueTesFileScriptRecoveryStatus.MappedPageMissing => "Mapped TES-file page missing",
            DialogueTesFileScriptRecoveryStatus.HeaderReadFailed => "Mapped header unreadable",
            DialogueTesFileScriptRecoveryStatus.SignatureMismatch => "Mapped bytes are not INFO",
            DialogueTesFileScriptRecoveryStatus.FormIdMismatch => "Mapped INFO FormID mismatch",
            DialogueTesFileScriptRecoveryStatus.CompressedRecord => "Mapped INFO is compressed",
            DialogueTesFileScriptRecoveryStatus.NoScriptSubrecords => "Mapped INFO has no script subrecords",
            DialogueTesFileScriptRecoveryStatus.Recovered => "Recovered from dump-resident TES-file bytes",
            _ => status.ToString()
        };
    }
}
