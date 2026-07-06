using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Export;
using BethesdaMultitool.Core.Formats.Esm.Models;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Show;

internal sealed class ScriptShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var script = records.Scripts.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, s => s.FormId, s => s.EditorId));
        if (script == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]     0x{script.FormId:X8}",
            $"[cyan]EditorID:[/]   {Markup.Escape(script.EditorId ?? "(none)")}",
            $"[cyan]Type:[/]       {script.ScriptType}",
            $"[cyan]Variables:[/]  {script.VariableCount}",
            $"[cyan]RefCount:[/]   {script.RefObjectCount}",
            $"[cyan]Compiled:[/]   {script.CompiledSize} bytes"
        };

        if (!string.IsNullOrEmpty(script.SourceText))
        {
            lines.Add("");
            lines.Add("[bold]Source (SCTX):[/]");
            lines.Add(Markup.Escape(Truncate(script.SourceText)));
        }

        if (!string.IsNullOrEmpty(script.DecompiledText))
        {
            lines.Add("");
            lines.Add("[bold]Decompiled (SCDA):[/]");
            lines.Add(Markup.Escape(Truncate(script.DecompiledText)));
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader($"[bold]SCPT[/] {Markup.Escape(script.EditorId ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }

    private static string Truncate(string text) =>
        text.Length > 2000 ? text[..2000] + "\n... (truncated)" : text;
}

