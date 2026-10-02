using System.CommandLine;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Games;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Esm;

/// <summary>
///     Semantic diff command - shows human-readable field-by-field differences.
///     <para>
///         <c>--format json</c> writes exactly one <see cref="SemdiffJsonWriter" /> document to stdout and
///         nothing else: warnings (also listed in the document), errors and <see cref="Logger" /> output go
///         to stderr, and no banner or table is written. Exit codes: 0 when the comparison ran (differences
///         and refused signature mismatches included), 1 for an invalid option value or a missing file; an
///         unknown <c>--format</c> value is a parse error.
///     </para>
/// </summary>
public static class EsmSemdiffCommand
{
    /// <summary>
    ///     Creates the 'semdiff' command for semantic comparison.
    /// </summary>
    public static Command CreateSemanticDiffCommand()
    {
        var command = new Command("semdiff", "Semantic diff - shows human-readable field differences (like TES5Edit)");

        var fileAArg = new Argument<string>("fileA") { Description = "First ESM file" };
        var fileBArg = new Argument<string>("fileB") { Description = "Second ESM file" };
        var formIdOption = new Option<string?>("-f", "--formid")
            { Description = "Specific FormID to compare (hex, with or without 0x, e.g., 0x0017B37C)" };
        var typeOption = new Option<string?>("-t", "--type")
            { Description = "Record type to filter (e.g., PROJ, WEAP, NPC_)" };
        var limitOption = new Option<int>("-l", "--limit")
        {
            Description = "Max records to show (default: 10); with --format json, the most records written to " +
                          "records[] (the summary still counts every record)",
            DefaultValueFactory = _ => 10
        };
        var showAllOption = new Option<bool>("--all")
            { Description = "Show all fields, not just differences" };
        var formatOption = new Option<string>("--format")
        {
            Description = "Output format: table (default) or json (one document on stdout; warnings and " +
                          "errors on stderr)",
            DefaultValueFactory = _ => FormatTable
        };
        AcceptValuesIgnoringCase(formatOption, FormatTable, FormatJson);
        var matchOption = new Option<string>("--match")
        {
            Description = "How records are paired: formid (default; a repeated FormID pairs in file order) or " +
                          "editorid (signature + EditorID, falling back to signature + FormID)",
            DefaultValueFactory = _ => MatchFormId
        };
        AcceptValuesIgnoringCase(matchOption, MatchFormId, MatchEditorId);
        var mapOption = new Option<string[]>("--map")
        {
            Description = "Repeatable: compare FormID A of the first file with FormID B of the second " +
                          "(hex, e.g., 0x010134AA=0x01011316). Not combinable with -f, -t or --match editorid"
        };

        command.Arguments.Add(fileAArg);
        command.Arguments.Add(fileBArg);
        command.Options.Add(formIdOption);
        command.Options.Add(typeOption);
        command.Options.Add(limitOption);
        command.Options.Add(showAllOption);
        command.Options.Add(formatOption);
        command.Options.Add(matchOption);
        command.Options.Add(mapOption);

        command.SetAction(parseResult =>
        {
            var request = new SemdiffRequest(parseResult.GetValue(fileAArg)!, parseResult.GetValue(fileBArg)!)
            {
                FormIdText = parseResult.GetValue(formIdOption),
                RecordType = parseResult.GetValue(typeOption),
                Limit = parseResult.GetValue(limitOption),
                ShowAll = parseResult.GetValue(showAllOption),
                Format = parseResult.GetValue(formatOption) ?? FormatTable,
                Match = parseResult.GetValue(matchOption) ?? MatchFormId,
                Maps = parseResult.GetValue(mapOption) ?? []
            };
            var stderr = parseResult.InvocationConfiguration?.Error ?? Console.Error;
            if (!IsJsonFormat(request.Format))
            {
                return RunSemanticDiffCore(request, AnsiConsole.Console, Stream.Null, stderr);
            }

            // stdout carries the document alone; the core writes nothing to the console in JSON mode, and
            // the stderr console only catches what a future change might write there by mistake.
            using var stdout = Console.OpenStandardOutput();
            return RunSemanticDiffCore(request, CliConsoles.Stderr, stdout, stderr);
        });

        return command;
    }

    private const string MatchFormId = "formid";
    private const string MatchEditorId = "editorid";
    private const string FormatTable = "table";
    private const string FormatJson = "json";

    private static void AcceptValuesIgnoringCase(Option<string> option, params string[] values)
    {
        // Keep the completion candidates while matching the command's case-insensitive runtime checks.
        option.CompletionSources.Add(values);
        option.Validators.Add(result =>
        {
            foreach (var token in result.Tokens)
            {
                if (!values.Contains(token.Value, StringComparer.OrdinalIgnoreCase))
                {
                    result.AddError($"Argument '{token.Value}' not recognized. Must be one of: {string.Join(", ", values)}");
                }
            }
        });
    }

    private static bool IsJsonFormat(string format)
    {
        return string.Equals(format, FormatJson, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Public entry point for semantic diff with custom labels (called by unified diff command).
    ///     Always pairs by FormID and renders the table (never JSON).
    /// </summary>
    public static int RunSemanticDiffLabeled(string fileAPath, string fileBPath, string labelA, string labelB,
        string? formIdStr, string? recordType, int limit, bool showAll,
        bool skipHeader = false)
    {
        var request = new SemdiffRequest(fileAPath, fileBPath)
        {
            LabelA = labelA,
            LabelB = labelB,
            FormIdText = formIdStr,
            RecordType = recordType,
            Limit = limit,
            ShowAll = showAll,
            SkipHeader = skipHeader,
            Format = FormatTable
        };
        return RunSemanticDiffCore(request, AnsiConsole.Console, Stream.Null, Console.Error);
    }

    /// <summary>
    ///     Parses the <c>--map</c> values. Each must be <c>A=B</c> with two hex FormIDs (0x optional).
    /// </summary>
    internal static bool TryParseMappings(IReadOnlyList<string> values,
        out List<SemdiffTypes.FormIdMapping> mappings, out string? error)
    {
        mappings = [];
        foreach (var value in values)
        {
            var parts = value.Split('=');
            if (parts.Length != 2 || CliHelpers.ParseFormId(parts[0]) is not { } formIdA ||
                CliHelpers.ParseFormId(parts[1]) is not { } formIdB)
            {
                error = $"--map '{value}' is not a FormID pair; expected A=B in hex, e.g. 0x010134AA=0x01011316";
                return false;
            }

            mappings.Add(new SemdiffTypes.FormIdMapping(formIdA, formIdB));
        }

        error = null;
        return true;
    }

    /// <summary>
    ///     Runs one comparison. In table mode the result is rendered to <paramref name="console" /> and
    ///     <paramref name="jsonOut" /> is not touched. In JSON mode (<see cref="SemdiffRequest.Format" />
    ///     <c>json</c>) nothing is written to <paramref name="console" />: the document plus one newline goes
    ///     to <paramref name="jsonOut" />, built in memory first so a failure can never leave half a document
    ///     there, and warnings (one line per <see cref="SemdiffTypes.SemdiffWarningEntry" />), errors and
    ///     <see cref="Logger" /> output go to <paramref name="stderr" />. Option errors go to
    ///     <paramref name="stderr" /> in both modes.
    /// </summary>
    internal static int RunSemanticDiffCore(SemdiffRequest request, IAnsiConsole console, Stream jsonOut,
        TextWriter stderr)
    {
        // Options first, so a malformed request fails before either file is read.
        bool json;
        if (IsJsonFormat(request.Format))
        {
            json = true;
        }
        else if (string.Equals(request.Format, FormatTable, StringComparison.OrdinalIgnoreCase))
        {
            json = false;
        }
        else
        {
            stderr.WriteLine($"Error: --format '{request.Format}' is not one of: {FormatTable}, {FormatJson}");
            return 1;
        }

        if (json)
        {
            // Scoped to this call's async context: every log line of the run belongs on stderr.
            Logger.SetOutput(stderr);
        }

        uint? targetFormId = null;
        if (!string.IsNullOrWhiteSpace(request.FormIdText))
        {
            targetFormId = CliHelpers.ParseFormId(request.FormIdText);
            if (targetFormId == null)
            {
                stderr.WriteLine(
                    $"Error: invalid FormID '{request.FormIdText}' (expected hex, e.g. 0x0017B37C or 0017B37C)");
                return 1;
            }
        }

        SemdiffTypes.MatchMode match;
        if (string.Equals(request.Match, MatchFormId, StringComparison.OrdinalIgnoreCase))
        {
            match = SemdiffTypes.MatchMode.FormId;
        }
        else if (string.Equals(request.Match, MatchEditorId, StringComparison.OrdinalIgnoreCase))
        {
            match = SemdiffTypes.MatchMode.EditorId;
        }
        else
        {
            stderr.WriteLine($"Error: --match '{request.Match}' is not one of: {MatchFormId}, {MatchEditorId}");
            return 1;
        }

        if (!TryParseMappings(request.Maps, out var mappings, out var mapError))
        {
            stderr.WriteLine($"Error: {mapError}");
            return 1;
        }

        if (mappings.Count > 0)
        {
            if (targetFormId != null)
            {
                stderr.WriteLine("Error: --map cannot be combined with -f/--formid; each --map names its own pair");
                return 1;
            }

            if (match == SemdiffTypes.MatchMode.EditorId)
            {
                stderr.WriteLine("Error: --map cannot be combined with --match editorid");
                return 1;
            }

            // A type filter would drop a mapped record of another signature before the comparer sees it,
            // turning a reused-FormID signature mismatch into a false "not in File B".
            if (!string.IsNullOrWhiteSpace(request.RecordType))
            {
                stderr.WriteLine(
                    "Error: --map cannot be combined with -t/--type; each --map names its own pair, and a type " +
                    "filter would hide a mapped record whose signature differs");
                return 1;
            }

            match = SemdiffTypes.MatchMode.ExplicitMap;
        }

        foreach (var path in new[] { request.FileAPath, request.FileBPath })
        {
            if (File.Exists(path))
            {
                continue;
            }

            if (json)
            {
                stderr.WriteLine($"Error: File not found: {path}");
            }
            else
            {
                console.MarkupLine($"[red]Error:[/] File not found: {Markup.Escape(path)}");
            }

            return 1;
        }

        var dataA = File.ReadAllBytes(request.FileAPath);
        var dataB = File.ReadAllBytes(request.FileBPath);
        var bigEndianA = EsmParser.IsBigEndian(dataA);
        var bigEndianB = EsmParser.IsBigEndian(dataB);
        var fileNameA = Path.GetFileName(request.FileAPath);
        var fileNameB = Path.GetFileName(request.FileBPath);
        var labelA = request.LabelA;
        var labelB = request.LabelB;

        if (!request.SkipHeader && !json)
        {
            console.MarkupLine("[bold]Semantic ESM Diff[/]");
            console.MarkupLine(
                $"{Markup.Escape(labelA)}: [cyan]{Markup.Escape(fileNameA)}[/] ({(bigEndianA ? "Big-endian" : "Little-endian")})");
            console.MarkupLine(
                $"{Markup.Escape(labelB)}: [cyan]{Markup.Escape(fileNameB)}[/] ({(bigEndianB ? "Big-endian" : "Little-endian")})");
            console.WriteLine();
        }

        var warnings = new List<SemdiffTypes.SemdiffWarning>();

        // Header flag names depend on the game (bit 10 is Persistent on an ACHR in New Vegas, a Quest
        // Item on a WEAP); bit deltas are named with file A's game.
        var gameA = GameDetector.DetectFromBytes(dataA, fileNameA).Game;
        var gameB = GameDetector.DetectFromBytes(dataB, fileNameB).Game;
        if (gameA != gameB)
        {
            warnings.Add(new SemdiffTypes.SemdiffWarning("game-mismatch", null, null,
                $"{labelA} is detected as {gameA} and {labelB} as {gameB}; header flag changes are named with " +
                $"{labelA}'s game"));
        }

        // A FormID's top byte indexes the file's master list; with different lists, the same number can
        // name a record of a different plugin. Slice 1 warns and does not remap.
        var mastersA = EsmParser.ParseFileHeader(dataA)?.Masters ?? [];
        var mastersB = EsmParser.ParseFileHeader(dataB)?.Masters ?? [];
        if (!mastersA.SequenceEqual(mastersB, StringComparer.OrdinalIgnoreCase))
        {
            warnings.Add(new SemdiffTypes.SemdiffWarning("master-list-mismatch", null, null,
                $"Master lists differ ({labelA}: {DescribeMasters(mastersA)}; {labelB}: {DescribeMasters(mastersB)}); " +
                "FormID load-order bytes may not denote the same plugin"));
        }

        List<SemdiffTypes.ParsedRecord> recordsA;
        List<SemdiffTypes.ParsedRecord> recordsB;
        int skippedCompressedA;
        int skippedCompressedB;
        if (match == SemdiffTypes.MatchMode.ExplicitMap)
        {
            // No type filter: -t is refused with --map, and each side must keep whatever it holds at the
            // mapped FormID so a signature mismatch is refused rather than reported as a missing record.
            recordsA = SemdiffRecordParser.ParseRecordsWithFormIds(dataA, bigEndianA, null,
                mappings.Select(m => m.FormIdA).ToHashSet(), out skippedCompressedA);
            recordsB = SemdiffRecordParser.ParseRecordsWithFormIds(dataB, bigEndianB, null,
                mappings.Select(m => m.FormIdB).ToHashSet(), out skippedCompressedB);
        }
        else if (match == SemdiffTypes.MatchMode.EditorId && targetFormId is { } editorIdAnchor)
        {
            (recordsA, recordsB) = ParseEditorIdNeighbourhood(dataA, bigEndianA, dataB, bigEndianB,
                request.RecordType, editorIdAnchor, out skippedCompressedA, out skippedCompressedB);
        }
        else
        {
            recordsA = SemdiffRecordParser.ParseRecordsWithSubrecords(
                dataA, bigEndianA, request.RecordType, targetFormId, out skippedCompressedA);
            recordsB = SemdiffRecordParser.ParseRecordsWithSubrecords(
                dataB, bigEndianB, request.RecordType, targetFormId, out skippedCompressedB);
        }

        if (skippedCompressedA > 0)
        {
            warnings.Add(new SemdiffTypes.SemdiffWarning("compressed-skipped", SemdiffTypes.SemdiffSide.A, null,
                $"{labelA}: {skippedCompressedA} compressed record(s) could not be decompressed and were " +
                "skipped from the diff."));
        }

        if (skippedCompressedB > 0)
        {
            warnings.Add(new SemdiffTypes.SemdiffWarning("compressed-skipped", SemdiffTypes.SemdiffSide.B, null,
                $"{labelB}: {skippedCompressedB} compressed record(s) could not be decompressed and were " +
                "skipped from the diff."));
        }

        var result = SemdiffComparer.Compare(recordsA, recordsB, new SemdiffTypes.SemdiffCompareOptions
        {
            Match = match,
            ExplicitPairs = mappings,
            ShowAll = request.ShowAll,
            GameA = gameA,
            GameB = gameB,
            BigEndianA = bigEndianA,
            BigEndianB = bigEndianB,
            LabelA = labelA,
            LabelB = labelB
        });
        result.Warnings.InsertRange(0, warnings);

        // With -f the listing is a record or two, so looking each unpaired EditorID up in the other file
        // costs one bounded walk per record; without -f it would cost one per listed record.
        if (targetFormId != null && match == SemdiffTypes.MatchMode.FormId)
        {
            for (var i = 0; i < result.Records.Count; i++)
            {
                var hint = SemdiffComparer.BuildEditorIdHint(result.Records[i],
                    (signature, editorId) =>
                        SemdiffRecordParser.ParseRecordsWithEditorId(dataA, bigEndianA, signature, editorId),
                    (signature, editorId) =>
                        SemdiffRecordParser.ParseRecordsWithEditorId(dataB, bigEndianB, signature, editorId),
                    labelA, labelB);
                if (hint != null)
                {
                    result.Records[i] = result.Records[i] with { EditorIdHint = hint };
                }
            }
        }

        if (json)
        {
            var query = new SemdiffTypes.SemdiffQuery
            {
                FormId = targetFormId,
                RecordType = request.RecordType,
                Match = match,
                Maps = mappings,
                Limit = request.Limit,
                ShowAll = request.ShowAll
            };
            var fileA = new SemdiffTypes.SemdiffFileInfo(labelA, Path.GetFullPath(request.FileAPath),
                dataA.LongLength, bigEndianA, gameA, mastersA);
            var fileB = new SemdiffTypes.SemdiffFileInfo(labelB, Path.GetFullPath(request.FileBPath),
                dataB.LongLength, bigEndianB, gameB, mastersB);
            WriteJson(query, result, fileA, fileB, jsonOut, stderr);
            return 0;
        }

        SemdiffFieldFormatter.DisplayResult(console, result, labelA, labelB, request.Limit, request.ShowAll);
        return 0;
    }

    /// <summary>
    ///     JSON mode's output: one stderr line per warning entry (the document lists the same entries), then
    ///     the document and a newline, built in memory and copied to <paramref name="jsonOut" /> only once
    ///     it is complete.
    /// </summary>
    private static void WriteJson(SemdiffTypes.SemdiffQuery query, SemdiffTypes.SemdiffResult result,
        SemdiffTypes.SemdiffFileInfo fileA, SemdiffTypes.SemdiffFileInfo fileB, Stream jsonOut, TextWriter stderr)
    {
        foreach (var entry in SemdiffJsonWriter.SummarizeWarnings(result.Warnings, fileA.Label, fileB.Label))
        {
            stderr.WriteLine($"Warning: {entry.Message}");
        }

        using var document = new MemoryStream();
        SemdiffJsonWriter.Write(document, query, result, fileA, fileB);
        document.WriteByte((byte)'\n');
        document.Position = 0;
        document.CopyTo(jsonOut);
        jsonOut.Flush();
    }

    /// <summary>
    ///     The records <c>--match editorid -f X</c> needs, without parsing either file whole: the
    ///     records with FormID X on each side, plus, for each of them that has an EditorID, the
    ///     other file's records of the same signature and EditorID. Whatever else the other file
    ///     holds under FormID X is kept too, so a reused FormID shows up as a separate record.
    /// </summary>
    private static (List<SemdiffTypes.ParsedRecord> A, List<SemdiffTypes.ParsedRecord> B)
        ParseEditorIdNeighbourhood(byte[] dataA, bool bigEndianA, byte[] dataB, bool bigEndianB,
            string? typeFilter, uint formId, out int skippedCompressedA, out int skippedCompressedB)
    {
        var byFormIdA = SemdiffRecordParser.ParseRecordsWithSubrecords(
            dataA, bigEndianA, typeFilter, formId, out skippedCompressedA);
        var byFormIdB = SemdiffRecordParser.ParseRecordsWithSubrecords(
            dataB, bigEndianB, typeFilter, formId, out skippedCompressedB);
        var recordsA = new List<SemdiffTypes.ParsedRecord>(byFormIdA);
        var recordsB = new List<SemdiffTypes.ParsedRecord>(byFormIdB);

        foreach (var record in byFormIdA)
        {
            if (record.EditorId is { } editorId)
            {
                AddUnseen(recordsB,
                    SemdiffRecordParser.ParseRecordsWithEditorId(dataB, bigEndianB, record.Type, editorId));
            }
        }

        foreach (var record in byFormIdB)
        {
            if (record.EditorId is { } editorId)
            {
                AddUnseen(recordsA,
                    SemdiffRecordParser.ParseRecordsWithEditorId(dataA, bigEndianA, record.Type, editorId));
            }
        }

        return (recordsA, recordsB);
    }

    private static void AddUnseen(List<SemdiffTypes.ParsedRecord> target,
        IEnumerable<SemdiffTypes.ParsedRecord> found)
    {
        foreach (var record in found)
        {
            if (!target.Exists(existing => existing.Offset == record.Offset))
            {
                target.Add(record);
            }
        }
    }

    private static string DescribeMasters(IReadOnlyList<string> masters)
    {
        return masters.Count == 0 ? "none" : string.Join(", ", masters);
    }

    /// <summary>
    ///     One semdiff invocation. <see cref="Match" /> is the raw <c>--match</c> value,
    ///     <see cref="Maps" /> the raw <c>--map</c> values and <see cref="Format" /> the raw
    ///     <c>--format</c> value (<c>table</c> or <c>json</c>); all are validated by
    ///     <see cref="RunSemanticDiffCore" />.
    /// </summary>
    internal sealed record SemdiffRequest(string FileAPath, string FileBPath)
    {
        public string LabelA { get; init; } = "File A";
        public string LabelB { get; init; } = "File B";
        public string? FormIdText { get; init; }
        public string? RecordType { get; init; }
        public int Limit { get; init; } = 10;
        public bool ShowAll { get; init; }
        public bool SkipHeader { get; init; }
        public string Format { get; init; } = FormatTable;
        public string Match { get; init; } = MatchFormId;
        public IReadOnlyList<string> Maps { get; init; } = [];
    }
}
