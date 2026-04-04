using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Show;

internal sealed class MusicTypeShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var musc = records.MusicTypes.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, m => m.FormId, m => m.EditorId));
        if (musc == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]       0x{musc.FormId:X8}",
            $"[cyan]EditorID:[/]     {Markup.Escape(musc.EditorId ?? "(none)")}",
            $"[cyan]File:[/]         {Markup.Escape(musc.FileName ?? "(none)")}"
        };

        if (musc.Attenuation is not 0f)
        {
            lines.Add($"[cyan]Attenuation:[/]  {musc.Attenuation:F2} dB");
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader($"[bold]MUSC[/] {Markup.Escape(musc.EditorId ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }
}
