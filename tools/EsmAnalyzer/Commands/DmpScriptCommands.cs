using System.CommandLine;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

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
        var command = new Command("scripts", "Script analysis commands");
        command.Subcommands.Add(CreateListCommand());
        command.Subcommands.Add(CreateShowCommand());
        command.Subcommands.Add(CreateCompareCommand());
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
        var reportOpt = new Option<string?>("-r", "--report") { Description = "Write detailed mismatch report to file" };
        var scriptOpt = new Option<string?>("--script") { Description = "Compare only this script (EditorId or FormId)" };
        var categoryOpt = new Option<string?>("--category") { Description = "Filter report to specific category (e.g., Other, UnresolvedVariable)" };

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
            AnsiConsole.MarkupLine($"[red]Error: File not found: {path}[/]");
            return null;
        }

        RecordCollection collection;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Analyzing dump...", async ctx =>
            {
                await Task.Yield(); // Ensure async context
            });

        var analyzer = new FalloutXbox360Utils.Core.Minidump.MinidumpAnalyzer();
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

        var table = new Table();
        table.AddColumn(new TableColumn("#").RightAligned());
        table.AddColumn("EditorId");
        table.AddColumn("FormId");
        table.AddColumn(new TableColumn("Vars").RightAligned());
        table.AddColumn(new TableColumn("Refs").RightAligned());
        table.AddColumn("SCTX");
        table.AddColumn("SCDA");
        table.AddColumn("Decompiled");
        table.AddColumn("Runtime");

        for (var i = 0; i < scripts.Count; i++)
        {
            var s = scripts[i];
            table.AddRow(
                (i + 1).ToString(),
                s.EditorId ?? "[dim]?[/]",
                $"0x{s.FormId:X8}",
                s.Variables.Count.ToString(),
                s.ReferencedObjects.Count.ToString(),
                s.HasSource ? "[green]Yes[/]" : "[dim]No[/]",
                s.CompiledData is { Length: > 0 } ? "[green]Yes[/]" : "[dim]No[/]",
                !string.IsNullOrEmpty(s.DecompiledText) ? "[green]Yes[/]" : "[dim]No[/]",
                s.FromRuntime ? "[cyan]RT[/]" : "[dim]ESM[/]");
        }

        AnsiConsole.Write(table);

        var withSource = scripts.Count(s => s.HasSource);
        var withBytecode = scripts.Count(s => s.CompiledData is { Length: > 0 });
        var withDecompiled = scripts.Count(s => !string.IsNullOrEmpty(s.DecompiledText));
        var withBoth = scripts.Count(s => s.HasSource && !string.IsNullOrEmpty(s.DecompiledText));
        var fromRuntime = scripts.Count(s => s.FromRuntime);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[cyan]Total scripts:[/] {scripts.Count}");
        AnsiConsole.MarkupLine($"[cyan]With source (SCTX):[/] {withSource}");
        AnsiConsole.MarkupLine($"[cyan]With bytecode (SCDA):[/] {withBytecode}");
        AnsiConsole.MarkupLine($"[cyan]With decompiled text:[/] {withDecompiled}");
        AnsiConsole.MarkupLine($"[cyan]With both (comparable):[/] {withBoth}");
        AnsiConsole.MarkupLine($"[cyan]From runtime structs:[/] {fromRuntime}");
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
            AnsiConsole.MarkupLine($"[red]Script not found: {scriptName}[/]");
            return;
        }

        // Header
        AnsiConsole.MarkupLine($"[cyan]Script:[/] {script.EditorId ?? "(no editor ID)"}");
        AnsiConsole.MarkupLine($"[cyan]FormId:[/] 0x{script.FormId:X8}");
        AnsiConsole.MarkupLine($"[cyan]Variables:[/] {script.Variables.Count}");
        AnsiConsole.MarkupLine($"[cyan]Referenced Objects:[/] {script.ReferencedObjects.Count}");
        AnsiConsole.MarkupLine($"[cyan]Quest Script:[/] {script.IsQuestScript}");
        AnsiConsole.MarkupLine($"[cyan]Runtime:[/] {script.FromRuntime}");

        if (script.OwnerQuestFormId.HasValue)
        {
            AnsiConsole.MarkupLine($"[cyan]Owner Quest:[/] 0x{script.OwnerQuestFormId.Value:X8}");
        }

        // Variables
        if (script.Variables.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]--- Variables ---[/]");
            foreach (var v in script.Variables)
            {
                var typeName = v.Type == 1 ? "int" : "float";
                AnsiConsole.MarkupLine($"  [{v.Index,3}] {typeName,-5} {v.Name ?? "(unnamed)"}");
            }
        }

        // Source text
        if (script.HasSource)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]--- Source (SCTX) ---[/]");
            AnsiConsole.WriteLine(script.SourceText!);
        }

        // Decompiled text
        if (!string.IsNullOrEmpty(script.DecompiledText))
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]--- Decompiled (SCDA) ---[/]");
            AnsiConsole.WriteLine(script.DecompiledText);
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
        if (uint.TryParse(hexStr, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var formId))
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
