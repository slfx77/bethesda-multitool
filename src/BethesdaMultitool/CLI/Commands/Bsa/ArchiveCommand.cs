using System.CommandLine;
using BethesdaMultitool.Core.Formats.Bsa.Ba2;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Bsa.Models;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Bsa;

/// <summary>
///     Format-agnostic archive command group. Every subcommand accepts either a classic BSA or a
///     Bethesda Archive 2 (<c>.ba2</c>); the format is detected by file magic via
///     <see cref="ArchiveReader" />. The legacy <c>bsa</c> and <c>ba2</c> groups are deprecated
///     aliases that expose this same subcommand set (see <see cref="BsaCommand" /> / <see cref="Ba2Command" />).
///     Inherently Xbox-360/BSA operations (<c>convert</c>, <c>validate</c>, <c>compare</c>) report a
///     graceful message when handed a BA2 rather than failing.
/// </summary>
public static class ArchiveCommand
{
    public static Command Create()
    {
        return BuildGroup("archive", "Archive operations (BSA / BA2) — accepts .bsa or .ba2");
    }

    /// <summary>
    ///     Builds an archive command group under <paramref name="name" /> with the full subcommand set.
    ///     Each call constructs fresh subcommand instances, so the canonical <c>archive</c> group and the
    ///     deprecated <c>bsa</c> / <c>ba2</c> aliases never share a <see cref="Command" /> instance.
    /// </summary>
    internal static Command BuildGroup(string name, string description)
    {
        var group = new Command(name, description);
        group.Subcommands.Add(CreateListCommand());
        group.Subcommands.Add(BsaExtractCommand.CreateExtractCommand());
        group.Subcommands.Add(BsaConvertCommand.CreateConvertCommand());
        group.Subcommands.Add(CreateInfoCommand());
        group.Subcommands.Add(BsaValidateCommand.CreateValidateCommand());
        group.Subcommands.Add(BsaDebugCommand.CreateCompareCommand());
        group.Subcommands.Add(BsaFindCommand.CreateFindCommand());
        group.Subcommands.Add(BsaFindCommand.CreateInspectCommand());
        group.Subcommands.Add(BsaDebugCommand.CreateRawDumpCommand());
        group.Subcommands.Add(BsaDebugCommand.CreateFileCompareCommand());
        return group;
    }

    private static Command CreateInfoCommand()
    {
        var command = new Command("info", "Display archive information (BSA or BA2)");
        var inputArg = new Argument<string>("input") { Description = "Path to BSA or BA2 file" };
        command.Arguments.Add(inputArg);
        command.SetAction((parseResult, _) =>
        {
            RunInfo(parseResult.GetValue(inputArg)!);
            return Task.CompletedTask;
        });
        return command;
    }

    private static Command CreateListCommand()
    {
        var command = new Command("list", "List files in an archive (BSA or BA2)");
        var inputArg = new Argument<string>("input") { Description = "Path to BSA or BA2 file" };
        var filterOption = new Option<string?>("-f", "--filter")
            { Description = "Filter by extension (e.g., .nif, .dds)" };
        var folderOption = new Option<string?>("-d", "--folder")
            { Description = "Filter by folder / path substring" };
        command.Arguments.Add(inputArg);
        command.Options.Add(filterOption);
        command.Options.Add(folderOption);
        command.SetAction((parseResult, _) =>
        {
            RunList(parseResult.GetValue(inputArg)!, parseResult.GetValue(filterOption),
                parseResult.GetValue(folderOption));
            return Task.CompletedTask;
        });
        return command;
    }

    private static void RunInfo(string input)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", input);
            return;
        }

        try
        {
            using var reader = ArchiveReader.Open(input);

            AnsiConsole.MarkupLine("[bold cyan]{0} Archive Info[/]", reader.FormatName);
            AnsiConsole.WriteLine();

            if (reader.Bsa is { } bsa)
            {
                RenderBsaInfoTable(input, bsa);
            }
            else
            {
                RenderBa2InfoTable(input, reader.Ba2!);
            }

            RenderExtensionStats(reader.GetExtensionStats());
            RenderFolderStats(reader.GetFolderStats());
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]Error parsing archive:[/] {0}", ex.Message);
        }
    }

    private static void RenderBsaInfoTable(string input, BsaArchive archive)
    {
        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("Property");
        table.AddColumn("Value");

        table.AddRow("File", Path.GetFileName(input));
        table.AddRow("Format", "BSA");
        table.AddRow("Version", archive.Header.Version.ToString());
        table.AddRow("Platform", archive.Header.IsXbox360 ? "[yellow]Xbox 360[/]" : "[green]PC[/]");
        table.AddRow("Folders", archive.Header.FolderCount.ToString("N0"));
        table.AddRow("Files", archive.Header.FileCount.ToString("N0"));
        table.AddRow("Compressed", archive.Header.DefaultCompressed ? "[green]Yes[/]" : "No");
        table.AddRow("Embed Names", archive.Header.EmbedFileNames ? "Yes" : "No");

        var flags = new List<string>();
        if (archive.Header.ArchiveFlags.HasFlag(BsaArchiveFlags.IncludeDirectoryNames)) flags.Add("DirNames");
        if (archive.Header.ArchiveFlags.HasFlag(BsaArchiveFlags.IncludeFileNames)) flags.Add("FileNames");
        if (archive.Header.ArchiveFlags.HasFlag(BsaArchiveFlags.CompressedArchive)) flags.Add("Compressed");
        if (archive.Header.ArchiveFlags.HasFlag(BsaArchiveFlags.Xbox360Archive)) flags.Add("Xbox360");
        if (archive.Header.ArchiveFlags.HasFlag(BsaArchiveFlags.EmbedFileNames)) flags.Add("EmbedNames");
        if (archive.Header.ArchiveFlags.HasFlag(BsaArchiveFlags.XMemCodec)) flags.Add("XMem");
        table.AddRow("Flags", string.Join(", ", flags));

        var fileTypes = new List<string>();
        if (archive.Header.FileFlags.HasFlag(BsaFileFlags.Meshes)) fileTypes.Add("Meshes");
        if (archive.Header.FileFlags.HasFlag(BsaFileFlags.Textures)) fileTypes.Add("Textures");
        if (archive.Header.FileFlags.HasFlag(BsaFileFlags.Menus)) fileTypes.Add("Menus");
        if (archive.Header.FileFlags.HasFlag(BsaFileFlags.Sounds)) fileTypes.Add("Sounds");
        if (archive.Header.FileFlags.HasFlag(BsaFileFlags.Voices)) fileTypes.Add("Voices");
        if (archive.Header.FileFlags.HasFlag(BsaFileFlags.Shaders)) fileTypes.Add("Shaders");
        if (archive.Header.FileFlags.HasFlag(BsaFileFlags.Trees)) fileTypes.Add("Trees");
        if (archive.Header.FileFlags.HasFlag(BsaFileFlags.Fonts)) fileTypes.Add("Fonts");
        if (archive.Header.FileFlags.HasFlag(BsaFileFlags.Misc)) fileTypes.Add("Misc");
        table.AddRow("Content Types", string.Join(", ", fileTypes));

        AnsiConsole.Write(table);
    }

    private static void RenderBa2InfoTable(string input, Ba2Archive archive)
    {
        var header = archive.Header;
        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("Property");
        table.AddColumn("Value");

        table.AddRow("File", Path.GetFileName(input));
        table.AddRow("Format", "BA2");
        table.AddRow("Magic", Ba2Header.Magic);
        table.AddRow("Version", header.Version.ToString());
        table.AddRow("Platform", "[green]PC[/]");
        table.AddRow("Type", $"{header.TypeTag} ({header.Type})");
        table.AddRow("Files", header.FileCount.ToString("N0"));
        table.AddRow("Compression", header.CompressionFormat.ToString());
        table.AddRow("Name Table", header.HasNameTable ? "[green]Yes[/]" : "No");

        AnsiConsole.Write(table);
    }

    private static void RenderExtensionStats(Dictionary<string, int> extStats)
    {
        if (extStats.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]File Extensions:[/]");
        var extTable = new Table { Border = TableBorder.Simple };
        extTable.AddColumn("Extension");
        extTable.AddColumn(new TableColumn("Count").RightAligned());
        foreach (var (ext, count) in extStats.Take(15))
        {
            extTable.AddRow(ext, count.ToString("N0"));
        }

        if (extStats.Count > 15)
        {
            extTable.AddRow("...", $"({extStats.Count - 15} more)");
        }

        AnsiConsole.Write(extTable);
    }

    private static void RenderFolderStats(Dictionary<string, int> folderStats)
    {
        if (folderStats.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Top Folders:[/]");
        var folderTable = new Table { Border = TableBorder.Simple };
        folderTable.AddColumn("Folder");
        folderTable.AddColumn(new TableColumn("Files").RightAligned());
        foreach (var (folder, count) in folderStats.Take(10))
        {
            folderTable.AddRow(Markup.Escape(folder), count.ToString("N0"));
        }

        if (folderStats.Count > 10)
        {
            folderTable.AddRow("...", $"({folderStats.Count - 10} more)");
        }

        AnsiConsole.Write(folderTable);
    }

    private static void RunList(string input, string? filter, string? folder)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", input);
            return;
        }

        try
        {
            using var reader = ArchiveReader.Open(input);

            var files = reader.ListFiles().AsEnumerable();

            if (!string.IsNullOrEmpty(filter))
            {
                var ext = filter.StartsWith('.') ? filter : $".{filter}";
                files = files.Where(f => f.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrEmpty(folder))
            {
                files = files.Where(f => f.FolderPath.Contains(folder, StringComparison.OrdinalIgnoreCase));
            }

            var fileList = files.ToList();

            AnsiConsole.MarkupLine("[cyan]{0}:[/] {1} ([yellow]{2}[/])",
                reader.FormatName, Path.GetFileName(input), reader.PlatformLabel);
            AnsiConsole.MarkupLine("[cyan]Files:[/] {0:N0} (of {1:N0} total)", fileList.Count, reader.TotalFiles);
            AnsiConsole.WriteLine();

            var table = new Table { Border = TableBorder.Simple };
            table.AddColumn("Path");
            table.AddColumn(new TableColumn("Size").RightAligned());
            table.AddColumn("Compressed");

            foreach (var file in fileList.Take(100))
            {
                table.AddRow(
                    Markup.Escape(file.FullPath),
                    CliHelpers.FormatSize(file.Size),
                    file.Compressed ? "[green]Yes[/]" : "");
            }

            if (fileList.Count > 100)
            {
                table.AddRow($"... and {fileList.Count - 100:N0} more files", "", "");
            }

            AnsiConsole.Write(table);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] {0}", ex.Message);
        }
    }
}
