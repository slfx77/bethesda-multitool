using System.CommandLine;
using System.Globalization;
using Spectre.Console;
using FalloutXbox360Utils.Core.Minidump;

namespace MinidumpAnalyzer.Commands;

/// <summary>
///     Commands for inspecting minidump memory regions and modules.
/// </summary>
public static class RegionCommands
{
    /// <summary>
    ///     Creates the 'regions' command to list memory regions.
    /// </summary>
    public static Command CreateRegionsCommand()
    {
        var inputArg = new Argument<string>("dump") { Description = "Path to the Xbox 360 minidump file" };

        var command = new Command("regions", "List memory regions in the minidump");
        command.Arguments.Add(inputArg);

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArg)!;
            ListRegions(input);
        });

        return command;
    }

    /// <summary>
    ///     Creates the 'modules' command to list loaded modules.
    /// </summary>
    public static Command CreateModulesCommand()
    {
        var inputArg = new Argument<string>("dump") { Description = "Path to the Xbox 360 minidump file" };

        var command = new Command("modules", "List loaded modules in the minidump");
        command.Arguments.Add(inputArg);

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArg)!;
            ListModules(input);
        });

        return command;
    }

    /// <summary>
    ///     Creates the 'va2offset' command.
    /// </summary>
    public static Command CreateVa2OffsetCommand()
    {
        var inputArg = new Argument<string>("dump") { Description = "Path to the Xbox 360 minidump file" };
        var vaArg = new Argument<string>("va") { Description = "Virtual address (hex, e.g. 0x82000000)" };

        var command = new Command("va2offset", "Convert a virtual address to file offset");
        command.Arguments.Add(inputArg);
        command.Arguments.Add(vaArg);

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var vaStr = parseResult.GetValue(vaArg)!;
            ConvertVaToOffset(input, vaStr);
        });

        return command;
    }

    /// <summary>
    ///     Creates the 'hexdump' command.
    /// </summary>
    public static Command CreateHexDumpCommand()
    {
        var inputArg = new Argument<string>("dump") { Description = "Path to the Xbox 360 minidump file" };
        var addressArg = new Argument<string>("address") { Description = "Address (hex). Prefix with 'va:' for virtual address, otherwise file offset" };
        var lengthOpt = new Option<int>("-n", "--length") { Description = "Number of bytes to dump", DefaultValueFactory = _ => 256 };

        var command = new Command("hexdump", "Hex dump memory at an address");
        command.Arguments.Add(inputArg);
        command.Arguments.Add(addressArg);
        command.Options.Add(lengthOpt);

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var addressStr = parseResult.GetValue(addressArg)!;
            var length = parseResult.GetValue(lengthOpt);
            HexDump(input, addressStr, length);
        });

        return command;
    }

    private static MinidumpInfo ParseDump(string path)
    {
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]Error: File not found: {path}[/]");
            Environment.Exit(1);
        }

        var info = MinidumpParser.Parse(path);
        if (!info.IsValid)
        {
            AnsiConsole.MarkupLine("[red]Error: Not a valid minidump file[/]");
            Environment.Exit(1);
        }

        return info;
    }

    private static void ListRegions(string path)
    {
        var info = ParseDump(path);

        AnsiConsole.MarkupLine($"[cyan]Minidump:[/] {Path.GetFileName(path)}");
        AnsiConsole.MarkupLine($"[cyan]Architecture:[/] {(info.IsXbox360 ? "Xbox 360 (PowerPC)" : $"0x{info.ProcessorArchitecture:X4}")}");
        AnsiConsole.MarkupLine($"[cyan]Memory Regions:[/] {info.MemoryRegions.Count:N0}");
        AnsiConsole.WriteLine();

        var table = new Table();
        table.AddColumn(new TableColumn("#").RightAligned());
        table.AddColumn("VA Start");
        table.AddColumn("VA End");
        table.AddColumn(new TableColumn("Size").RightAligned());
        table.AddColumn("File Offset");

        for (var i = 0; i < info.MemoryRegions.Count; i++)
        {
            var r = info.MemoryRegions[i];
            table.AddRow(
                i.ToString(CultureInfo.InvariantCulture),
                $"0x{r.VirtualAddress:X8}",
                $"0x{r.VirtualAddress + r.Size:X8}",
                $"{r.Size:N0}",
                $"0x{r.FileOffset:X8}");
        }

        AnsiConsole.Write(table);

        var totalSize = info.MemoryRegions.Sum(r => r.Size);
        AnsiConsole.MarkupLine($"\n[dim]Total captured memory: {totalSize:N0} bytes ({totalSize / 1024.0 / 1024.0:F2} MB)[/]");

        var groups = info.GetContiguousRegionGroups();
        AnsiConsole.MarkupLine($"[dim]Contiguous groups: {groups.Count}[/]");
    }

    private static void ListModules(string path)
    {
        var info = ParseDump(path);

        AnsiConsole.MarkupLine($"[cyan]Minidump:[/] {Path.GetFileName(path)}");
        AnsiConsole.MarkupLine($"[cyan]Modules:[/] {info.Modules.Count:N0}");
        AnsiConsole.WriteLine();

        var table = new Table();
        table.AddColumn(new TableColumn("#").RightAligned());
        table.AddColumn("Name");
        table.AddColumn("Base Address");
        table.AddColumn(new TableColumn("Size").RightAligned());
        table.AddColumn("Checksum");

        for (var i = 0; i < info.Modules.Count; i++)
        {
            var m = info.Modules[i];
            table.AddRow(
                i.ToString(CultureInfo.InvariantCulture),
                Path.GetFileName(m.Name),
                $"0x{m.BaseAddress32:X8}",
                $"{m.Size:N0}",
                $"0x{m.Checksum:X8}");
        }

        AnsiConsole.Write(table);

        var gameModule = info.FindGameModule();
        if (gameModule != null)
        {
            AnsiConsole.MarkupLine($"\n[green]Game module:[/] {Path.GetFileName(gameModule.Name)} at 0x{gameModule.BaseAddress32:X8}");
        }
    }

    private static void ConvertVaToOffset(string path, string vaStr)
    {
        var info = ParseDump(path);

        if (!TryParseHex(vaStr, out var va))
        {
            AnsiConsole.MarkupLine($"[red]Error: Invalid hex address: {vaStr}[/]");
            Environment.Exit(1);
            return;
        }

        var offset = info.VirtualAddressToFileOffset(va);
        if (offset == null)
        {
            AnsiConsole.MarkupLine($"[red]VA 0x{va:X8} is not captured in the minidump[/]");
            Environment.Exit(1);
            return;
        }

        AnsiConsole.MarkupLine($"VA 0x{va:X8} -> File offset 0x{offset.Value:X8} ({offset.Value:N0})");

        var module = info.FindModuleByVirtualAddress(va);
        if (module != null)
        {
            AnsiConsole.MarkupLine($"[dim]Module: {Path.GetFileName(module.Name)} (base 0x{module.BaseAddress32:X8})[/]");
        }
    }

    private static void HexDump(string path, string addressStr, int length)
    {
        var info = ParseDump(path);

        long fileOffset;
        if (addressStr.StartsWith("va:", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseHex(addressStr[3..], out var va))
            {
                AnsiConsole.MarkupLine($"[red]Error: Invalid hex address: {addressStr}[/]");
                Environment.Exit(1);
                return;
            }

            var offset = info.VirtualAddressToFileOffset(va);
            if (offset == null)
            {
                AnsiConsole.MarkupLine($"[red]VA 0x{va:X8} is not captured in the minidump[/]");
                Environment.Exit(1);
                return;
            }

            fileOffset = offset.Value;
            AnsiConsole.MarkupLine($"[dim]VA 0x{va:X8} -> File offset 0x{fileOffset:X8}[/]");
        }
        else
        {
            if (!TryParseHex(addressStr, out var fo))
            {
                AnsiConsole.MarkupLine($"[red]Error: Invalid hex address: {addressStr}[/]");
                Environment.Exit(1);
                return;
            }

            fileOffset = fo;

            var va = info.FileOffsetToVirtualAddress(fileOffset);
            if (va != null)
            {
                AnsiConsole.MarkupLine($"[dim]File offset 0x{fileOffset:X8} -> VA 0x{va.Value:X8}[/]");
            }
        }

        AnsiConsole.WriteLine();

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(fileOffset, SeekOrigin.Begin);

        var buffer = new byte[length];
        var bytesRead = fs.Read(buffer, 0, length);

        const int bytesPerLine = 16;
        for (var i = 0; i < bytesRead; i += bytesPerLine)
        {
            var lineOffset = fileOffset + i;
            Console.Write($"{lineOffset:X8}  ");

            for (var j = 0; j < bytesPerLine; j++)
            {
                if (i + j < bytesRead)
                {
                    Console.Write($"{buffer[i + j]:X2} ");
                }
                else
                {
                    Console.Write("   ");
                }

                if (j == 7)
                {
                    Console.Write(" ");
                }
            }

            Console.Write(" ");

            for (var j = 0; j < bytesPerLine && i + j < bytesRead; j++)
            {
                var b = buffer[i + j];
                Console.Write(b is >= 32 and < 127 ? (char)b : '.');
            }

            Console.WriteLine();
        }
    }

    private static bool TryParseHex(string str, out long value)
    {
        str = str.Trim();
        if (str.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            str = str[2..];
        }

        return long.TryParse(str, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}
