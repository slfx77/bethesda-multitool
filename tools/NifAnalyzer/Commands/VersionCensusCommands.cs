using System.Collections.Concurrent;
using System.CommandLine;
using System.Globalization;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using Spectre.Console;

namespace NifAnalyzer.Commands;

/// <summary>
///     Tallies the NIF header version (binary version + user version + BS stream version) of every NIF in
///     one or more archives (BSA/BA2, format auto-detected) or loose-file directories, and flags versions
///     the parser does not yet accept. Header-only — no geometry decode — so it is cheap enough to sweep a
///     whole game's mesh archives. Reusable across games: point it at any game's mesh container(s).
/// </summary>
internal static class VersionCensusCommands
{
    public static Command CreateVersionCensusCommand()
    {
        var command = new Command(
            "versioncensus",
            "Tally NIF header versions across archives/dirs and flag versions the parser doesn't accept");
        var pathsArg = new Argument<string[]>("paths")
        {
            Description = "Archive (.bsa/.ba2) or directory paths to scan (one or more)",
            Arity = ArgumentArity.OneOrMore
        };
        var labelOption = new Option<string?>("-l", "--label") { Description = "Label for this scan (e.g. game name)" };
        command.Arguments.Add(pathsArg);
        command.Options.Add(labelOption);
        command.SetAction(parseResult => Census(
            parseResult.GetValue(pathsArg) ?? [],
            parseResult.GetValue(labelOption)));
        return command;
    }

    private static void Census(string[] paths, string? label)
    {
        if (!string.IsNullOrWhiteSpace(label))
        {
            AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(label)}[/]").LeftJustified());
        }

        var tally = new ConcurrentDictionary<(uint Bin, uint User, uint Bs), VersionStat>();
        var totalNifs = 0;
        var unreadable = 0;

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                ScanDirectory(path, tally, ref totalNifs, ref unreadable);
            }
            else if (File.Exists(path))
            {
                ScanArchive(path, tally, ref totalNifs, ref unreadable);
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Not found:[/] {Markup.Escape(path)}");
            }
        }

        RenderTable(tally, totalNifs, unreadable);
    }

    private static void ScanArchive(
        string archivePath, ConcurrentDictionary<(uint, uint, uint), VersionStat> tally,
        ref int totalNifs, ref int unreadable)
    {
        ArchiveReader reader;
        try
        {
            reader = ArchiveReader.Open(archivePath);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to open[/] {Markup.Escape(archivePath)}: {Markup.Escape(ex.Message)}");
            return;
        }

        using (reader)
        {
            var nifEntries = reader.ListFiles()
                .Where(e => e.Extension.Equals(".nif", StringComparison.OrdinalIgnoreCase))
                .ToList();
            AnsiConsole.MarkupLine(
                $"[dim]{Markup.Escape(Path.GetFileName(archivePath))} ({reader.FormatName}/{reader.PlatformLabel}): " +
                $"{nifEntries.Count} NIFs[/]");

            var local = 0;
            var localUnreadable = 0;
            Parallel.ForEach(nifEntries, entry =>
            {
                byte[] bytes;
                try
                {
                    bytes = reader.Extract(entry);
                }
                catch
                {
                    Interlocked.Increment(ref localUnreadable);
                    return;
                }

                Interlocked.Increment(ref local);
                Record(tally, bytes, entry.FullPath, ref localUnreadable);
            });

            totalNifs += local;
            unreadable += localUnreadable;
        }
    }

    private static void ScanDirectory(
        string dir, ConcurrentDictionary<(uint, uint, uint), VersionStat> tally,
        ref int totalNifs, ref int unreadable)
    {
        var files = Directory.EnumerateFiles(dir, "*.nif", SearchOption.AllDirectories).ToList();
        AnsiConsole.MarkupLine($"[dim]{Markup.Escape(dir)}: {files.Count} loose NIFs[/]");

        var local = 0;
        var localUnreadable = 0;
        Parallel.ForEach(files, file =>
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(file);
            }
            catch
            {
                Interlocked.Increment(ref localUnreadable);
                return;
            }

            Interlocked.Increment(ref local);
            Record(tally, bytes, file, ref localUnreadable);
        });

        totalNifs += local;
        unreadable += localUnreadable;
    }

    private static void Record(
        ConcurrentDictionary<(uint, uint, uint), VersionStat> tally, byte[] bytes, string path,
        ref int localUnreadable)
    {
        var probe = NifParser.ProbeVersionInfo(bytes);
        if (probe is not { } p)
        {
            Interlocked.Increment(ref localUnreadable);
            return;
        }

        var stat = tally.GetOrAdd((p.BinaryVersion, p.UserVersion, p.BsVersion),
            _ => new VersionStat { Example = path });
        Interlocked.Increment(ref stat.Count);
    }

    private static void RenderTable(
        ConcurrentDictionary<(uint Bin, uint User, uint Bs), VersionStat> tally, int totalNifs, int unreadable)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("NIF Version");
        table.AddColumn(new TableColumn("UserVer").RightAligned());
        table.AddColumn(new TableColumn("BsVer").RightAligned());
        table.AddColumn(new TableColumn("Count").RightAligned());
        table.AddColumn("Parser support");
        table.AddColumn("Example");

        var gaps = 0;
        foreach (var ((bin, user, bs), stat) in tally.OrderByDescending(kv => kv.Value.Count))
        {
            var supported = NifParser.IsBethesdaVersion(bin, user) || bin == 0x04000002;
            if (!supported)
            {
                gaps += stat.Count;
            }

            table.AddRow(
                Markup.Escape(FormatVersion(bin)),
                user.ToString(CultureInfo.InvariantCulture),
                bs.ToString(CultureInfo.InvariantCulture),
                stat.Count.ToString(CultureInfo.InvariantCulture),
                supported ? "[green]accepted[/]" : "[red]MISSING[/]",
                Markup.Escape(Shorten(stat.Example)));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            $"[bold]{totalNifs}[/] NIFs, [bold]{tally.Count}[/] distinct versions, " +
            $"[red]{gaps}[/] in unsupported versions" +
            (unreadable > 0 ? $", [yellow]{unreadable}[/] unreadable" : string.Empty));
        AnsiConsole.WriteLine();
    }

    private static string FormatVersion(uint v) => FormattableString.Invariant(
        $"{(v >> 24) & 0xFF}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}");

    private static string Shorten(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 2 ? path : string.Join('/', parts[^2], parts[^1]);
    }

    private sealed class VersionStat
    {
        public int Count;
        public string Example = "";
    }
}
