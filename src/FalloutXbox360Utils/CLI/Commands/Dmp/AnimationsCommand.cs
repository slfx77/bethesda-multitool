using System.CommandLine;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Minidump;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Dmp;

/// <summary>
///     CLI command for discovering loaded animations in memory dumps via RTTI-based scanning.
/// </summary>
public static class AnimationsCommand
{
    public static Command Create()
    {
        var command = new Command("animations", "Discover loaded TESAnimGroup animations in a memory dump");

        var inputArg = new Argument<string>("input") { Description = "Path to memory dump file (.dmp)" };

        command.Arguments.Add(inputArg);

        command.SetAction(async (parseResult, _) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            await ExecuteAsync(input);
        });

        return command;
    }

    private static async Task ExecuteAsync(string input)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {input}");
            return;
        }

        AnsiConsole.MarkupLine($"[blue]Animation Discovery:[/] {Path.GetFileName(input)}");
        AnsiConsole.WriteLine();

        var fileSize = new FileInfo(input).Length;
        var minidump = MinidumpParser.Parse(input);
        if (!minidump.IsValid)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Not a valid minidump file");
            return;
        }

        // Phase 1: Find TESAnimGroup vtable via RTTI census
        uint vtableVa;
        using (var stream = File.OpenRead(input))
        {
            AnsiConsole.MarkupLine("[blue]Running RTTI census to find TESAnimGroup vtable...[/]");
            var reader = new RttiReader(minidump, stream);
            var census = reader.RunCensus((current, total, _) =>
            {
                if (current % 200 == 0)
                {
                    AnsiConsole.MarkupLine($"[dim]  Scanning heap regions {current}/{total}[/]");
                }
            });

            vtableVa = RuntimeAnimationScanner.FindTesAnimGroupVtable(census);

            if (vtableVa == 0)
            {
                AnsiConsole.MarkupLine(
                    "[yellow]TESAnimGroup class not found in RTTI census — no animations to discover.[/]");
                return;
            }

            AnsiConsole.MarkupLine($"[green]Found TESAnimGroup vtable at 0x{vtableVa:X8}[/]");
        }

        // Phase 2: Scan for all TESAnimGroup instances
        AnsiConsole.MarkupLine("[blue]Scanning for animation instances...[/]");

        List<DiscoveredAnimation> animations;
        using (var mmf = MemoryMappedFile.CreateFromFile(input, FileMode.Open, null, 0, MemoryMappedFileAccess.Read))
        using (var accessor = mmf.CreateViewAccessor(0, fileSize, MemoryMappedFileAccess.Read))
        {
            var context = new RuntimeMemoryContext(new MmfMemoryAccessor(accessor), fileSize, minidump);
            var scanner = new RuntimeAnimationScanner(context);
            animations = await Task.Run(() => scanner.ScanForAnimations(vtableVa));
        }

        if (animations.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No TESAnimGroup instances found.[/]");
            return;
        }

        // Phase 3: Display results
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]Found {animations.Count} animation groups[/]");
        AnsiConsole.WriteLine();

        // Summary by type
        var byType = animations
            .GroupBy(a => a.GroupTypeName)
            .OrderByDescending(g => g.Count())
            .ToList();

        var summaryTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Animation Groups by Type[/]");

        summaryTable.AddColumn(new TableColumn("[bold]Type[/]").LeftAligned());
        summaryTable.AddColumn(new TableColumn("[bold]Count[/]").RightAligned());
        summaryTable.AddColumn(new TableColumn("[bold]Sample Names[/]").LeftAligned());

        foreach (var group in byType)
        {
            var sampleNames = group
                .Where(a => a.Name != null)
                .Select(a => a.Name!)
                .Distinct()
                .Take(3)
                .ToList();

            var nameDisplay = sampleNames.Count > 0
                ? Markup.Escape(string.Join(", ", sampleNames))
                : "[dim](no names resolved)[/]";

            summaryTable.AddRow(
                group.Key,
                group.Count().ToString(),
                nameDisplay);
        }

        AnsiConsole.Write(summaryTable);
        AnsiConsole.WriteLine();

        // Unique animation names
        var uniqueNames = animations
            .Where(a => a.Name != null)
            .Select(a => a.Name!)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        if (uniqueNames.Count > 0)
        {
            var nameTable = new Table()
                .Border(TableBorder.Rounded)
                .Title($"[bold]Unique Animation Names ({uniqueNames.Count})[/]");

            nameTable.AddColumn("[bold]Name[/]");
            nameTable.AddColumn(new TableColumn("[bold]Types Using It[/]").LeftAligned());

            foreach (var name in uniqueNames)
            {
                var types = animations
                    .Where(a => a.Name == name)
                    .Select(a => a.GroupTypeName)
                    .Distinct()
                    .OrderBy(t => t);

                nameTable.AddRow(
                    Markup.Escape(name),
                    string.Join(", ", types));
            }

            AnsiConsole.Write(nameTable);
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]No animation name strings could be resolved from pointers.[/]");
        }
    }
}
