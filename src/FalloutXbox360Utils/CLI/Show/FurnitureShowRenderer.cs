using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Show;

internal sealed class FurnitureShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var furn = records.Furniture.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, f => f.FormId, f => f.EditorId));
        if (furn == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]      0x{furn.FormId:X8}",
            $"[cyan]EditorID:[/]    {Markup.Escape(furn.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]        {Markup.Escape(furn.FullName ?? "(none)")}"
        };

        if (!string.IsNullOrEmpty(furn.ModelPath))
        {
            lines.Add($"[cyan]Model:[/]       {Markup.Escape(furn.ModelPath)}");
        }

        if (furn.MarkerFlags != 0)
        {
            lines.Add($"[cyan]Markers:[/]     0x{furn.MarkerFlags:X8}");
        }

        if (furn.Script.HasValue)
        {
            lines.Add($"[cyan]Script:[/]      {resolver.FormatWithEditorId(furn.Script.Value)}");
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader($"[bold]FURN[/] {Markup.Escape(furn.EditorId ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }
}
