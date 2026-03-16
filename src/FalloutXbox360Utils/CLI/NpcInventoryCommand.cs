using System.CommandLine;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Minidump;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

internal static class NpcInventoryCommand
{
    public static Command CreateDmpCommand()
    {
        var inputArg = new Argument<string>("dump")
        {
            Description = "Path to the Xbox 360 minidump file"
        };
        var formatOpt = new Option<string>("-f", "--format")
        {
            Description = "Output format: text, csv",
            DefaultValueFactory = _ => "text"
        };

        var command = new Command("npcs", "List unique NPC FormIDs present in a DMP runtime hash table");
        command.Arguments.Add(inputArg);
        command.Options.Add(formatOpt);
        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var format = parseResult.GetValue(formatOpt)!;
            WriteEntries(LoadFromDmp(input), format, Path.GetFileName(input), "DMP");
        });

        return command;
    }

    public static Command CreateEsmCommand()
    {
        var inputArg = new Argument<string>("input")
        {
            Description = "Path to the ESM/ESP file"
        };
        var formatOpt = new Option<string>("-f", "--format")
        {
            Description = "Output format: text, csv",
            DefaultValueFactory = _ => "text"
        };

        var command = new Command("npcs", "List unique NPC FormIDs present in an ESM/ESP");
        command.Arguments.Add(inputArg);
        command.Options.Add(formatOpt);
        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var format = parseResult.GetValue(formatOpt)!;
            WriteEntries(LoadFromEsm(input), format, Path.GetFileName(input), "ESM");
        });

        return command;
    }

    internal static List<NpcInventoryEntry> LoadFromDmp(string dmpPath)
    {
        if (!File.Exists(dmpPath))
        {
            throw new FileNotFoundException("DMP file not found.", dmpPath);
        }

        var fileInfo = new FileInfo(dmpPath);
        using var mmf = MemoryMappedFile.CreateFromFile(dmpPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        var scanResult = EsmRecordScanner.ScanForRecordsMemoryMapped(accessor, fileInfo.Length);
        var minidumpInfo = MinidumpParser.Parse(dmpPath);
        if (!minidumpInfo.IsValid)
        {
            throw new InvalidOperationException("Invalid minidump format.");
        }

        EsmEditorIdExtractor.ExtractRuntimeEditorIds(accessor, fileInfo.Length, minidumpInfo, scanResult);

        return Deduplicate(scanResult.RuntimeEditorIds
            .Where(entry => entry.FormType == 0x2A && entry.FormId != 0)
            .Select(entry => new NpcInventoryEntry(
                entry.FormId,
                Normalize(entry.EditorId),
                Normalize(entry.DisplayName))));
    }

    internal static List<NpcInventoryEntry> LoadFromEsm(string esmPath)
    {
        var esm = EsmFileLoader.Load(esmPath, false)
                  ?? throw new InvalidOperationException($"Failed to load ESM file: {esmPath}");
        var resolver = NpcAppearanceResolver.Build(esm.Data, esm.IsBigEndian);

        return Deduplicate(resolver.GetAllNpcs()
            .Select(pair => new NpcInventoryEntry(
                pair.Key,
                Normalize(pair.Value.EditorId),
                Normalize(pair.Value.FullName))));
    }

    private static void WriteEntries(
        IReadOnlyList<NpcInventoryEntry> entries,
        string format,
        string sourceName,
        string sourceType)
    {
        switch (format.ToLowerInvariant())
        {
            case "csv":
                Console.WriteLine("FormId,EditorId,DisplayName");
                foreach (var entry in entries)
                {
                    Console.WriteLine(
                        $"0x{entry.FormId:X8},{CliHelpers.CsvEscape(entry.EditorId)},{CliHelpers.CsvEscape(entry.DisplayName)}");
                }

                break;

            case "text":
                AnsiConsole.MarkupLine("[cyan]{0}:[/] {1}", sourceType, sourceName);
                AnsiConsole.MarkupLine("[cyan]NPCs:[/] {0:N0}", entries.Count);
                AnsiConsole.WriteLine();

                var table = new Table().Border(TableBorder.Rounded);
                table.AddColumn("FormID");
                table.AddColumn("EditorID");
                table.AddColumn("Name");

                foreach (var entry in entries)
                {
                    table.AddRow(
                        $"0x{entry.FormId:X8}",
                        Markup.Escape(entry.EditorId ?? ""),
                        Markup.Escape(entry.DisplayName ?? ""));
                }

                AnsiConsole.Write(table);
                break;

            default:
                throw new InvalidOperationException($"Unsupported format '{format}'. Use 'text' or 'csv'.");
        }
    }

    private static List<NpcInventoryEntry> Deduplicate(IEnumerable<NpcInventoryEntry> entries)
    {
        return entries
            .GroupBy(entry => entry.FormId)
            .Select(group => new NpcInventoryEntry(
                group.Key,
                group.Select(entry => entry.EditorId)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                group.Select(entry => entry.DisplayName)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))))
            .OrderBy(entry => entry.FormId)
            .ToList();
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    internal readonly record struct NpcInventoryEntry(
        uint FormId,
        string? EditorId,
        string? DisplayName);
}
