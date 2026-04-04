using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Show;

internal sealed class StaticShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var stat = records.Statics.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, s => s.FormId, s => s.EditorId));
        if (stat == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]      0x{stat.FormId:X8}",
            $"[cyan]EditorID:[/]    {Markup.Escape(stat.EditorId ?? "(none)")}"
        };

        if (!string.IsNullOrEmpty(stat.ModelPath))
        {
            lines.Add($"[cyan]Model:[/]       {Markup.Escape(stat.ModelPath)}");
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader($"[bold]STAT[/] {Markup.Escape(stat.EditorId ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }
}
