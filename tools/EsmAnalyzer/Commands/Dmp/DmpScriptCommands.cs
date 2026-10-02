using System.CommandLine;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Esm.Export.Geck;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Minidump;
using Spectre.Console;

namespace EsmAnalyzer.Commands.Dmp;

/// <summary>
///     Commands for script analysis in memory dumps.
/// </summary>
public static class DmpScriptCommands
{
    /// <summary>
    ///     Creates the 'scripts' parent command with subcommands.
    /// </summary>
    public static Command CreateScriptsCommand()
    {
        var command = new Command("scripts",
            "Inspect and compare scripts recovered from a memory dump (list, show, compare, crossrefs)");
        command.Subcommands.Add(CreateListCommand());
        command.Subcommands.Add(CreateShowCommand());
        command.Subcommands.Add(CreateCompareCommand());
        command.Subcommands.Add(DmpScriptAuditCommand.Create());
        command.Subcommands.Add(CreateCrossRefsCommand());
        return command;
    }

    public static Command CreateListCommand()
    {
        var inputArg = new Argument<string>("dump") { Description = "Path to the Xbox 360 minidump file" };

        var command = new Command("list", "List all scripts found in a memory dump");
        command.Arguments.Add(inputArg);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            await ListScriptsAsync(input);
        });

        return command;
    }

    public static Command CreateShowCommand()
    {
        var inputArg = new Argument<string>("dump") { Description = "Path to the Xbox 360 minidump file" };
        var nameArg = new Argument<string>("script") { Description = "Script EditorId or FormId (hex)" };

        var command = new Command("show", "Show details of a specific script");
        command.Arguments.Add(inputArg);
        command.Arguments.Add(nameArg);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var name = parseResult.GetValue(nameArg)!;
            await ShowScriptAsync(input, name);
        });

        return command;
    }

    public static Command CreateCompareCommand()
    {
        var inputArg = new Argument<string>("dump") { Description = "Path to the Xbox 360 minidump file" };
        var reportOpt = new Option<string?>("-r", "--report")
            { Description = "Write detailed mismatch report to file" };
        var scriptOpt = new Option<string?>("--script")
            { Description = "Compare only this script (EditorId or FormId)" };
        var categoryOpt = new Option<string?>("--category")
            { Description = "Filter report to specific category (e.g., Other, UnresolvedVariable)" };

        var command = new Command("compare", "Semantic comparison of SCTX source vs decompiled SCDA bytecode");
        command.Arguments.Add(inputArg);
        command.Options.Add(reportOpt);
        command.Options.Add(scriptOpt);
        command.Options.Add(categoryOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var reportPath = parseResult.GetValue(reportOpt);
            var scriptFilter = parseResult.GetValue(scriptOpt);
            var categoryFilter = parseResult.GetValue(categoryOpt);
            await DmpScriptCompareCommand.CompareScriptsAsync(input, reportPath, scriptFilter, categoryFilter);
        });

        return command;
    }

    public static Command CreateCrossRefsCommand()
    {
        var inputArg = new Argument<string>("dump") { Description = "Path to the Xbox 360 minidump file" };

        var command = new Command("crossrefs", "Show cross-reference chain diagnostics for variable resolution");
        command.Arguments.Add(inputArg);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            await DmpScriptCrossRefCommand.CrossRefDiagnosticsAsync(input);
        });

        return command;
    }

    #region Shared loader

    internal static async Task<(RecordCollection Collection, List<ScriptRecord> Scripts)?> LoadDumpAsync(string path)
    {
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]Error: File not found: {Markup.Escape(path)}[/]");
            return null;
        }

        RecordCollection collection;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Analyzing dump...", async ctx =>
            {
                await Task.Yield(); // Ensure async context
            });

        var analyzer = new MinidumpAnalyzer();
        var analysisResult = await analyzer.AnalyzeAsync(path, includeMetadata: true);

        if (analysisResult.EsmRecords == null)
        {
            AnsiConsole.MarkupLine("[red]Error: No ESM records found in dump[/]");
            return null;
        }

        var fileInfo = new FileInfo(path);
        using var mmf = MemoryMappedFile.CreateFromFile(
            path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        var reconstructor = new RecordParser(
            analysisResult.EsmRecords,
            analysisResult.FormIdMap,
            accessor,
            fileInfo.Length,
            analysisResult.MinidumpInfo);

        collection = reconstructor.ParseAll();

        return (collection, collection.Scripts);
    }

    #endregion

    #region List command

    private static async Task ListScriptsAsync(string path)
    {
        var result = await LoadDumpAsync(path);
        if (result == null)
        {
            return;
        }

        var (_, scripts) = result.Value;
        RenderScriptList(AnsiConsole.Console, scripts);
    }

    /// <summary>
    ///     The <c>dmp scripts list</c> table and totals. Source text is classified by
    ///     <see cref="ScriptSourceProvenance" /> as dump input: the Source column prints the provenance token,
    ///     "With source (SCTX)" counts only captured text, and a BethesdaMultitool decompilation that stands in
    ///     for missing SCTX is counted as reconstructed, never as source. EditorIDs are escaped.
    /// </summary>
    internal static void RenderScriptList(IAnsiConsole console, IReadOnlyList<ScriptRecord> scripts)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(scripts);

        var sources = scripts.Select(s => ScriptSourceProvenance.Classify(s, true)).ToList();

        var table = new Table();
        table.AddColumn(new TableColumn("#").RightAligned());
        table.AddColumn("EditorId");
        table.AddColumn("FormId");
        table.AddColumn(new TableColumn("Vars").RightAligned());
        table.AddColumn(new TableColumn("Refs").RightAligned());
        table.AddColumn("Source");
        table.AddColumn("SCDA");
        table.AddColumn("Decompiled");
        table.AddColumn("Runtime");

        for (var i = 0; i < scripts.Count; i++)
        {
            var s = scripts[i];
            table.AddRow(
                (i + 1).ToString(CultureInfo.InvariantCulture),
                s.EditorId != null ? Markup.Escape(s.EditorId) : "[dim]?[/]",
                $"0x{s.FormId:X8}",
                s.Variables.Count.ToString(CultureInfo.InvariantCulture),
                s.ReferencedObjects.Count.ToString(CultureInfo.InvariantCulture),
                FormatSourceCell(sources[i]),
                s.CompiledData is { Length: > 0 } ? "[green]Yes[/]" : "[dim]No[/]",
                !string.IsNullOrEmpty(s.DecompiledText) ? "[green]Yes[/]" : "[dim]No[/]",
                s.FromRuntime ? "[cyan]RT[/]" : "[dim]ESM[/]");
        }

        console.Write(table);

        var withSource = sources.Count(source => source.IsAuthorWrittenText);
        var withReconstructed = sources.Count(source => source.IsReconstructed);
        var withoutSource = sources.Count(source => !source.HasSourceText);
        var withBytecode = scripts.Count(s => s.CompiledData is { Length: > 0 });
        var withDecompiled = scripts.Count(s => !string.IsNullOrEmpty(s.DecompiledText));
        // Comparable = captured source beside a decompilation. A reconstruction compared with the
        // decompilation it was rendered from would only compare the decompiler with itself.
        var withBoth = 0;
        for (var i = 0; i < scripts.Count; i++)
        {
            if (sources[i].IsAuthorWrittenText && !string.IsNullOrEmpty(scripts[i].DecompiledText))
            {
                withBoth++;
            }
        }

        var fromRuntime = scripts.Count(s => s.FromRuntime);

        console.WriteLine();
        console.MarkupLine($"[cyan]Total scripts:[/] {scripts.Count}");
        console.MarkupLine($"[cyan]With source (SCTX):[/] {withSource}");
        console.MarkupLine($"[cyan]With reconstructed source (decompiled from SCDA):[/] {withReconstructed}");
        console.MarkupLine(
            $"[cyan]Without source text:[/] {withoutSource} ({Markup.Escape(ScriptSourceProvenance.PartialDumpAbsenceWording)})");
        console.MarkupLine($"[cyan]With bytecode (SCDA):[/] {withBytecode}");
        console.MarkupLine($"[cyan]With decompiled text:[/] {withDecompiled}");
        console.MarkupLine($"[cyan]With both (comparable):[/] {withBoth}");
        console.MarkupLine($"[cyan]From runtime structs:[/] {fromRuntime}");
    }

    /// <summary>The Source cell: the provenance token, green for captured text, yellow for a reconstruction.</summary>
    private static string FormatSourceCell(ScriptSourceClassification source)
    {
        var token = Markup.Escape(source.Token);
        if (source.IsAuthorWrittenText)
        {
            return $"[green]{token}[/]";
        }

        return source.IsReconstructed ? $"[yellow]{token}[/]" : $"[dim]{token}[/]";
    }

    #endregion

    #region Show command

    private static async Task ShowScriptAsync(string path, string scriptName)
    {
        var result = await LoadDumpAsync(path);
        if (result == null)
        {
            return;
        }

        var (_, scripts) = result.Value;

        // Find script by EditorId or FormId
        var script = FindScript(scripts, scriptName);
        if (script == null)
        {
            AnsiConsole.MarkupLine($"[red]Script not found: {Markup.Escape(scriptName)}[/]");
            return;
        }

        RenderScriptDetail(AnsiConsole.Console, script);
    }

    /// <summary>
    ///     The <c>dmp scripts show</c> body. The source body is headed by its
    ///     <see cref="ScriptSourceProvenance" /> label (dump input), so a reconstruction decompiled from SCDA
    ///     is never presented as captured SCTX, and a missing body says that absence from a partial capture is
    ///     not evidence of absence from the build. EditorIDs are escaped.
    /// </summary>
    internal static void RenderScriptDetail(IAnsiConsole console, ScriptRecord script)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(script);

        var source = ScriptSourceProvenance.Classify(script, true);

        // Header
        console.MarkupLine($"[cyan]Script:[/] {Markup.Escape(script.EditorId ?? "(no editor ID)")}");
        console.MarkupLine($"[cyan]FormId:[/] 0x{script.FormId:X8}");
        console.MarkupLine($"[cyan]Variables:[/] {script.Variables.Count}");
        console.MarkupLine($"[cyan]Referenced Objects:[/] {script.ReferencedObjects.Count}");
        console.MarkupLine($"[cyan]Quest Script:[/] {script.IsQuestScript}");
        console.MarkupLine($"[cyan]Runtime:[/] {script.FromRuntime}");
        console.MarkupLine(
            $"[cyan]Source Origin:[/] {Markup.Escape(GeckScriptWriter.FormatSourceOrigin(source))}");

        if (script.OwnerQuestFormId.HasValue)
        {
            console.MarkupLine($"[cyan]Owner Quest:[/] 0x{script.OwnerQuestFormId.Value:X8}");
        }

        // Variables
        if (script.Variables.Count > 0)
        {
            console.WriteLine();
            console.MarkupLine("[yellow]--- Variables ---[/]");
            foreach (var v in script.Variables)
            {
                var typeName = v.Type == 1 ? "int" : "float";
                console.WriteLine($"  [{v.Index,3}] {typeName,-5} {v.Name ?? "(unnamed)"}");
            }
        }

        // Source text, headed by what it is
        console.WriteLine();
        if (source.HasSourceText)
        {
            console.MarkupLine($"[yellow]--- {Markup.Escape(source.Label)} ---[/]");
            console.WriteLine(script.SourceText!);
        }
        else
        {
            console.MarkupLine($"[grey]{Markup.Escape(source.Label)}[/]");
        }

        // Decompiled text
        if (!string.IsNullOrEmpty(script.DecompiledText))
        {
            console.WriteLine();
            console.MarkupLine(
                $"[yellow]--- {Markup.Escape(ScriptSourceProvenance.DecompiledTextLabel)} ---[/]");
            console.WriteLine(script.DecompiledText);
        }
    }

    #endregion

    #region Helpers

    private static ScriptRecord? FindScript(List<ScriptRecord> scripts, string filter)
    {
        // Try exact EditorId match first
        var match = scripts.FirstOrDefault(s =>
            s.EditorId != null && s.EditorId.Equals(filter, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            return match;
        }

        // Try FormId (hex)
        var hexStr = filter.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? filter[2..] : filter;
        if (uint.TryParse(hexStr, NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var formId))
        {
            match = scripts.FirstOrDefault(s => s.FormId == formId);
            if (match != null)
            {
                return match;
            }
        }

        // Try partial EditorId match
        return scripts.FirstOrDefault(s =>
            s.EditorId != null && s.EditorId.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    #endregion
}