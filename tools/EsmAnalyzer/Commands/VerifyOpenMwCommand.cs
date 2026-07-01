using System.CommandLine;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using BethesdaMultitool.Core.Analysis;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Black-box verification harness: cross-checks our ESM parse against OpenMW's <c>esmtool</c>, used
///     purely as an external oracle (we run the compiled tool and diff its OUTPUT — we never read or
///     translate its GPLv3 source, matching the project's clean-room policy). OpenMW reads the same retail
///     data we do, so a record-type census diff catches whole classes of parse bugs (missing/extra record
///     types, gross mis-parses) the way an <c>esmtool</c>-vs-ours diff would have caught the LAND VTEX
///     swizzle.
///     <para>
///         Tier 1 (this command) is a per-record-type CENSUS: our side is the exact raw scan census
///         (<c>EsmRecordScanResult.MainRecordCounts</c>); the esmtool side is parsed tolerantly from
///         <c>esmtool dump</c> text. The raw esmtool output is ALWAYS saved so the text adapter can be
///         finalized against real output (esmtool isn't bundled — its exact dump format must be observed,
///         not guessed). Field-level diffs (e.g. per-cell VTEX grids) are a planned tier 2.
///     </para>
/// </summary>
internal static partial class VerifyOpenMwCommand
{
    // TES3 top-level record types (UESP Tes3Mod:File Format). Used both to recognize esmtool's record
    // headers and to scope the census to real records.
    private static readonly HashSet<string> Tes3RecordTypes = new(StringComparer.Ordinal)
    {
        "ACTI", "ALCH", "APPA", "ARMO", "BODY", "BOOK", "BSGN", "CELL", "CLAS", "CLOT", "CONT", "CREA",
        "DIAL", "DOOR", "ENCH", "FACT", "GLOB", "GMST", "INFO", "INGR", "LAND", "LEVC", "LEVI", "LIGH",
        "LOCK", "LTEX", "MGEF", "MISC", "NPC_", "PGRD", "PROB", "RACE", "REGN", "REPA", "SCPT", "SKIL",
        "SNDG", "SOUN", "SPEL", "SSCR", "STAT", "WEAP", "TES3"
    };

    internal static Command Create()
    {
        var command = new Command("verify",
            "Cross-check our parse against external reference implementations (black-box oracles)");
        command.Subcommands.Add(CreateOpenMwCommand());
        return command;
    }

    private static Command CreateOpenMwCommand()
    {
        var command = new Command("openmw",
            "Diff our record-type census against OpenMW esmtool (run as a black box) for an ESM/ESP");

        var fileArg = new Argument<string>("file") { Description = "The .esm/.esp to verify" };
        var esmtoolOption = new Option<string?>("--esmtool")
        {
            Description = "Path to OpenMW esmtool(.exe). Defaults to the ESMTOOL env var, then PATH."
        };
        var rawOption = new Option<string?>("--save-dump")
        {
            Description = "Where to save esmtool's raw dump (default: <esmname>.esmtool.txt in the working dir)."
        };
        var fieldsOption = new Option<bool>("--fields")
        {
            Description =
                "Tier 2: diff per-field values (GMST/LTEX/CELL/NPC_/REGN) instead of the record-type census."
        };

        command.Arguments.Add(fileArg);
        command.Options.Add(esmtoolOption);
        command.Options.Add(rawOption);
        command.Options.Add(fieldsOption);
        command.SetAction(parseResult => Execute(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(esmtoolOption),
            parseResult.GetValue(rawOption),
            parseResult.GetValue(fieldsOption)));
        return command;
    }

    private static int Execute(string file, string? esmtoolPath, string? rawPath, bool fields)
    {
        if (!File.Exists(file))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", Markup.Escape(file));
            return 1;
        }

        // Tier 2: field-level diffs need esmtool to run (no census-only fallback), so resolve it up front.
        if (fields)
        {
            var esmtoolForFields = ResolveEsmtool(esmtoolPath);
            if (esmtoolForFields is null)
            {
                AnsiConsole.MarkupLine(
                    "[yellow]esmtool not found.[/] Tier-2 field diff needs esmtool; pass [grey]--esmtool <path>[/], " +
                    "set the [grey]ESMTOOL[/] env var, or put it on PATH.");
                return 1;
            }

            return VerifyOpenMwFields.Run(file, esmtoolForFields);
        }

        // Our side: the exact raw record-type census from the scan (matches what esmtool counts).
        var ours = LoadOurCensus(file);
        if (ours is null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Our parser produced no record scan for {0}.", Markup.Escape(file));
            return 1;
        }

        var resolvedEsmtool = ResolveEsmtool(esmtoolPath);
        if (resolvedEsmtool is null)
        {
            AnsiConsole.MarkupLine("[yellow]esmtool not found.[/] Showing our census only.");
            AnsiConsole.MarkupLine(
                "Install OpenMW (ships esmtool), then pass [grey]--esmtool <path>[/], set the [grey]ESMTOOL[/] env var, or put it on PATH.");
            PrintOurCensus(ours);
            return 0;
        }

        var (dumpText, ranOk) = RunEsmtoolDump(resolvedEsmtool, file);
        // Default next to the working dir (not the retail Data Files folder the ESM may live in).
        var savePath = rawPath ?? Path.GetFileName(file) + ".esmtool.txt";
        try
        {
            File.WriteAllText(savePath, dumpText);
            AnsiConsole.MarkupLine("[grey]Saved esmtool raw output → {0}[/]", Markup.Escape(savePath));
        }
        catch (IOException ex)
        {
            AnsiConsole.MarkupLine("[yellow]Could not save raw output:[/] {0}", Markup.Escape(ex.Message));
        }

        if (!ranOk || dumpText.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]esmtool produced no output.[/] Showing our census only; check the saved file.");
            PrintOurCensus(ours);
            return 1;
        }

        var (theirs, pattern) = ParseEsmtoolCensus(dumpText);
        if (theirs.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "[yellow]Could not recognize any record headers in esmtool output[/] — its dump format differs from " +
                "the patterns tried. Share the saved raw file so the text adapter can be finalized against it.");
            PrintOurCensus(ours);
            return 1;
        }

        PrintDiff(ours, theirs, pattern);
        return 0;
    }

    private static Dictionary<string, int>? LoadOurCensus(string file)
    {
        using var result = UnifiedAnalyzer.AnalyzeAsync(file, null, default).GetAwaiter().GetResult();
        var scan = result.RawResult.EsmRecords;
        if (scan?.MainRecordCounts is not { Count: > 0 } counts)
        {
            return null;
        }

        var census = new Dictionary<string, int>(counts, StringComparer.Ordinal);
        // The TES3 (or TES4) file header is metadata, not a data record — esmtool dump lists it as the
        // Author/Description/version banner, not a "Record:", so drop it for an apples-to-apples census.
        census.Remove("TES3");
        census.Remove("TES4");
        return census;
    }

    internal static string? ResolveEsmtool(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        var env = Environment.GetEnvironmentVariable("ESMTOOL");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        // Bare name resolves via PATH when Process runs it; probe both the Windows and POSIX names.
        foreach (var name in new[] { "esmtool", "esmtool.exe" })
        {
            var onPath = Environment.GetEnvironmentVariable("PATH")?
                .Split(Path.PathSeparator)
                .Select(dir => Path.Combine(dir.Trim(), name))
                .FirstOrDefault(File.Exists);
            if (onPath is not null)
            {
                return onPath;
            }
        }

        return null;
    }

    internal static (string Output, bool Ok) RunEsmtoolDump(string esmtool, string file, string? type = null)
    {
        // esmtool's invocation has varied across releases; try the modern "dump" verb first, then the bare
        // form (older builds default to dumping). An optional -t TYPE filter keeps tier-2 dumps small.
        // Capture stdout; the caller saves it for adapter work.
        var variants = type is null
            ? new[] { new[] { "dump", file }, new[] { file } }
            : new[] { new[] { "-t", type, "dump", file }, new[] { "-t", type, file } };
        foreach (var args in variants)
        {
            var psi = new ProcessStartInfo(esmtool)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            try
            {
                using var proc = Process.Start(psi);
                if (proc is null)
                {
                    continue;
                }

                var stdout = proc.StandardOutput.ReadToEnd();
                proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                if (stdout.Length > 0)
                {
                    return (stdout, true);
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return ($"<failed to launch esmtool: {ex.Message}>", false);
            }
        }

        return (string.Empty, false);
    }

    /// <summary>
    ///     Counts records per type from esmtool's text dump. esmtool's exact format must be observed (it
    ///     isn't bundled), so several plausible record-header patterns are tried and the one that
    ///     recognizes the most known TES3 types wins; the matched pattern name is reported so a mismatch is
    ///     obvious rather than silently wrong.
    /// </summary>
    private static (Dictionary<string, int> Census, string Pattern) ParseEsmtoolCensus(string dump)
    {
        var candidates = new (string Name, Regex Rx)[]
        {
            ("Record: TYPE", RecordColonRx()),
            ("TYPE: (line start)", TypeColonRx()),
            ("TYPE (standalone line)", StandaloneTypeRx())
        };

        var best = new Dictionary<string, int>(StringComparer.Ordinal);
        var bestName = "(none)";
        var bestTotal = 0;

        foreach (var (name, rx) in candidates)
        {
            var census = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Match m in rx.Matches(dump))
            {
                var type = m.Groups[1].Value;
                if (Tes3RecordTypes.Contains(type))
                {
                    census[type] = census.GetValueOrDefault(type) + 1;
                }
            }

            var total = census.Values.Sum();
            if (total > bestTotal)
            {
                best = census;
                bestName = name;
                bestTotal = total;
            }
        }

        return (best, bestName);
    }

    private static void PrintOurCensus(Dictionary<string, int> ours)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Type");
        table.AddColumn(new TableColumn("Ours").RightAligned());
        foreach (var (type, count) in ours.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            table.AddRow(Markup.Escape(type), count.ToString("N0"));
        }

        table.AddRow("[bold]TOTAL[/]", $"[bold]{ours.Values.Sum():N0}[/]");
        AnsiConsole.Write(table);
    }

    private static void PrintDiff(Dictionary<string, int> ours, Dictionary<string, int> theirs, string pattern)
    {
        AnsiConsole.MarkupLine("[grey]esmtool census parsed via pattern:[/] {0}", Markup.Escape(pattern));

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Type");
        table.AddColumn(new TableColumn("Ours").RightAligned());
        table.AddColumn(new TableColumn("esmtool").RightAligned());
        table.AddColumn(new TableColumn("Δ").RightAligned());

        var mismatches = 0;
        var allTypes = ours.Keys.Union(theirs.Keys).OrderBy(t => t, StringComparer.Ordinal);
        foreach (var type in allTypes)
        {
            var o = ours.GetValueOrDefault(type);
            var t = theirs.GetValueOrDefault(type);
            var delta = o - t;
            if (delta != 0)
            {
                mismatches++;
            }

            var deltaCell = delta == 0 ? "[green]0[/]" : $"[red]{delta:+#;-#;0}[/]";
            table.AddRow(Markup.Escape(type), o.ToString("N0"), t.ToString("N0"), deltaCell);
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(mismatches == 0
            ? "[green]Census matches[/] (record-type counts agree)."
            : $"[yellow]{mismatches} record type(s) differ.[/] A non-zero Δ is a parse discrepancy to investigate " +
              "(note: our LAND count is the decoded heightmap count; synthetic worldspace/texture-set records are " +
              "excluded from the raw scan, so those rows can legitimately differ).");
    }

    [GeneratedRegex(@"^\s*Record:\s*([A-Z][A-Z0-9_]{3})\b", RegexOptions.Multiline)]
    private static partial Regex RecordColonRx();

    [GeneratedRegex(@"^\s*([A-Z][A-Z0-9_]{3}):", RegexOptions.Multiline)]
    private static partial Regex TypeColonRx();

    [GeneratedRegex(@"^([A-Z][A-Z0-9_]{3})\s*$", RegexOptions.Multiline)]
    private static partial Regex StandaloneTypeRx();
}
