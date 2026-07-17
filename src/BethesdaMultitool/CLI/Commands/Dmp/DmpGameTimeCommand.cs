using System.CommandLine;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Dmp;

/// <summary>
///     Lists the captured in-game clock (GameYear/GameMonth/GameDay/GameHour/GameDaysPassed
///     GLOB runtime values) for each memory dump, ordered by build date (game module PE
///     TimeDateStamp — the same ordering the cross-dump HTML reporter uses).
/// </summary>
internal static class DmpGameTimeCommand
{
    // Engine-reserved game-clock globals; FormIDs are identical across builds
    // (see RuntimeStateRecordPolicy). EditorId is used as a fallback match in case
    // the hash-table entry's FormID pointer follow failed.
    private static readonly (uint FormId, string Name)[] ClockGlobals =
    [
        (0x35, "GameYear"),
        (0x36, "GameMonth"),
        (0x37, "GameDay"),
        (0x38, "GameHour"),
        (0x39, "GameDaysPassed"),
        (0x3A, "TimeScale")
    ];

    public static Command Create()
    {
        var inputArg = new Argument<string>("input")
        {
            Description = "Path to a minidump file or a directory of .dmp files"
        };
        var formatOpt = new Option<string>("-f", "--format")
        {
            Description = "Output format: text, csv",
            DefaultValueFactory = _ => "text"
        };

        var command = new Command(
            "game-time",
            "List the in-game date/time captured in each dump's game-clock globals, ordered by build date");
        command.Arguments.Add(inputArg);
        command.Options.Add(formatOpt);
        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var format = parseResult.GetValue(formatOpt)!;
            Run(input, format);
        });

        return command;
    }

    private static void Run(string input, string format)
    {
        var paths = DiscoverDumps(input);
        if (paths.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]Error: No .dmp files found at: {Markup.Escape(input)}[/]");
            Environment.Exit(1);
            return;
        }

        var results = new List<DumpGameTime>(paths.Count);
        var quiet = !format.Equals("text", StringComparison.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (!quiet)
            {
                AnsiConsole.MarkupLine($"[dim]Reading {Markup.Escape(Path.GetFileName(path))}...[/]");
            }

            results.Add(ReadDump(path));
        }

        var ordered = results
            .OrderBy(r => r.BuildDateUtc)
            .ThenBy(r => r.DumpName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (format.Equals("csv", StringComparison.OrdinalIgnoreCase))
        {
            WriteCsv(ordered);
        }
        else
        {
            WriteTable(ordered);
        }
    }

    private static List<string> DiscoverDumps(string input)
    {
        if (File.Exists(input))
        {
            return [Path.GetFullPath(input)];
        }

        if (!Directory.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error: Path not found: {Markup.Escape(input)}[/]");
            Environment.Exit(1);
            return [];
        }

        return Directory.EnumerateFiles(input, "*.dmp", SearchOption.AllDirectories)
            .Where(p => !Path.GetFileName(p).Contains("hangdump", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFullPath)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DumpGameTime ReadDump(string path)
    {
        var dumpName = Path.GetFileName(path);
        var fileInfo = new FileInfo(path);

        try
        {
            var minidumpInfo = MinidumpParser.Parse(path);
            if (!minidumpInfo.IsValid)
            {
                return new DumpGameTime(dumpName, fileInfo.LastWriteTimeUtc, "file timestamp",
                    "Unknown", [], "not a valid minidump");
            }

            var buildType = MinidumpAnalyzer.DetectBuildType(minidumpInfo) ?? "Unknown";

            var gameModule = minidumpInfo.FindGameModule();
            DateTime buildDate;
            string dateSource;
            if (gameModule is { TimeDateStamp: not 0 })
            {
                buildDate = DateTimeOffset.FromUnixTimeSeconds(gameModule.TimeDateStamp).UtcDateTime;
                dateSource = "PE TimeDateStamp";
            }
            else
            {
                buildDate = fileInfo.LastWriteTimeUtc;
                dateSource = "file timestamp";
            }

            using var mmf = MemoryMappedFile.CreateFromFile(
                path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

            // The clock GLOBs are located via the runtime editor-ID hash table; the full
            // ESM record scan is not needed for this.
            var scanResult = new EsmRecordScanResult();
            EsmEditorIdExtractor.ExtractRuntimeEditorIds(
                accessor, fileInfo.Length, minidumpInfo, scanResult);
            if (scanResult.RuntimeEditorIds.Count == 0)
            {
                return new DumpGameTime(dumpName, buildDate, dateSource, buildType, [],
                    "no runtime editor-ID table found");
            }

            var reader = new RuntimeStructReader(accessor, fileInfo.Length, minidumpInfo);
            var values = new Dictionary<string, GlobalRecord>();
            foreach (var (formId, name) in ClockGlobals)
            {
                foreach (var entry in scanResult.RuntimeEditorIds)
                {
                    if (entry.FormId != formId &&
                        !string.Equals(entry.EditorId, name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var glob = reader.ReadRuntimeGlobal(entry);
                    if (glob != null)
                    {
                        values[name] = glob;
                        break;
                    }
                }
            }

            var note = values.Count == 0
                ? "clock globals not captured"
                : values.Count < ClockGlobals.Length
                    ? "partial capture"
                    : null;

            return new DumpGameTime(dumpName, buildDate, dateSource, buildType, values, note);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new DumpGameTime(dumpName, fileInfo.LastWriteTimeUtc, "file timestamp",
                "Unknown", [], ex.Message);
        }
    }

    private static void WriteTable(List<DumpGameTime> results)
    {
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[bold]Build Date[/]");
        table.AddColumn("[bold]Build[/]");
        table.AddColumn("[bold]Dump[/]");
        table.AddColumn("[bold]In-Game Date/Time[/]");
        table.AddColumn(new TableColumn("[bold]Days Passed[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]TimeScale[/]").RightAligned());
        table.AddColumn("[bold]Notes[/]");

        foreach (var result in results)
        {
            var inGame = FormatInGameDateTime(result.Values);
            table.AddRow(
                result.BuildDateUtc.ToString("yyyy-MM-dd"),
                Markup.Escape(result.BuildType),
                Markup.Escape(result.DumpName),
                inGame == null ? "[dim]—[/]" : $"[green]{Markup.Escape(inGame)}[/]",
                FormatValue(result.Values, "GameDaysPassed", "F1"),
                FormatValue(result.Values, "TimeScale", "F0"),
                result.Note == null ? "" : $"[yellow]{Markup.Escape(result.Note)}[/]");
        }

        AnsiConsole.Write(table);

        var captured = results.Count(r => FormatInGameDateTime(r.Values) != null);
        AnsiConsole.MarkupLine(
            $"\n[dim]{captured}/{results.Count} dumps with a readable in-game clock. " +
            "In-game time = GameYear/GameMonth/GameDay/GameHour GLOB runtime values; " +
            "build date = game module PE TimeDateStamp.[/]");
    }

    private static void WriteCsv(List<DumpGameTime> results)
    {
        Console.WriteLine(
            "Dump,BuildDateUtc,BuildDateSource,BuildType,InGameDateTime," +
            "GameYear,GameMonth,GameDay,GameHour,GameDaysPassed,TimeScale,Notes");
        foreach (var result in results)
        {
            var fields = new[]
            {
                CliHelpers.CsvEscape(result.DumpName),
                result.BuildDateUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                CliHelpers.CsvEscape(result.DateSource),
                CliHelpers.CsvEscape(result.BuildType),
                CliHelpers.CsvEscape(FormatInGameDateTime(result.Values) ?? ""),
                CsvValue(result.Values, "GameYear"),
                CsvValue(result.Values, "GameMonth"),
                CsvValue(result.Values, "GameDay"),
                CsvValue(result.Values, "GameHour"),
                CsvValue(result.Values, "GameDaysPassed"),
                CsvValue(result.Values, "TimeScale"),
                CliHelpers.CsvEscape(result.Note ?? "")
            };
            Console.WriteLine(string.Join(",", fields));
        }
    }

    /// <summary>
    ///     Composes "2281-10-19 09:15" from the year/month/day/hour globals. Returns null when
    ///     the captured values don't form a plausible calendar date (missing globals, or the
    ///     runtime float read produced garbage that the reader zeroed out).
    /// </summary>
    internal static string? FormatInGameDateTime(IReadOnlyDictionary<string, GlobalRecord> values)
    {
        if (!values.TryGetValue("GameYear", out var year) ||
            !values.TryGetValue("GameMonth", out var month) ||
            !values.TryGetValue("GameDay", out var day) ||
            !values.TryGetValue("GameHour", out var hour))
        {
            return null;
        }

        var y = (int)Math.Round(year.Value);
        var mo = (int)Math.Round(month.Value);
        var d = (int)Math.Round(day.Value);
        if (y is < 1 or > 9999 || mo is < 1 or > 12 || d is < 1 or > 31 ||
            hour.Value is < 0f or >= 24f)
        {
            return null;
        }

        var hh = (int)hour.Value;
        var mm = (int)((hour.Value - hh) * 60f);
        return $"{y:0000}-{mo:00}-{d:00} {hh:00}:{mm:00}";
    }

    private static string FormatValue(
        IReadOnlyDictionary<string, GlobalRecord> values, string name, string numberFormat)
    {
        return values.TryGetValue(name, out var glob)
            ? glob.Value.ToString(numberFormat)
            : "[dim]—[/]";
    }

    private static string CsvValue(IReadOnlyDictionary<string, GlobalRecord> values, string name)
    {
        return values.TryGetValue(name, out var glob)
            ? glob.Value.ToString("R")
            : "";
    }

    internal sealed record DumpGameTime(
        string DumpName,
        DateTime BuildDateUtc,
        string DateSource,
        string BuildType,
        Dictionary<string, GlobalRecord> Values,
        string? Note);
}
