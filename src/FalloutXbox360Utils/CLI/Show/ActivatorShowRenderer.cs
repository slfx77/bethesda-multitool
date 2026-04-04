using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Show;

internal sealed class ActivatorShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var acti = records.Activators.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, a => a.FormId, a => a.EditorId));
        if (acti == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]      0x{acti.FormId:X8}",
            $"[cyan]EditorID:[/]    {Markup.Escape(acti.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]        {Markup.Escape(acti.FullName ?? "(none)")}"
        };

        if (!string.IsNullOrEmpty(acti.ModelPath))
        {
            lines.Add($"[cyan]Model:[/]       {Markup.Escape(acti.ModelPath)}");
        }

        if (acti.ActivationSoundFormId.HasValue)
        {
            lines.Add($"[cyan]Activ Sound:[/] {resolver.FormatWithEditorId(acti.ActivationSoundFormId.Value)}");
        }

        if (acti.RadioStationFormId.HasValue)
        {
            lines.Add($"[cyan]Radio:[/]       {resolver.FormatWithEditorId(acti.RadioStationFormId.Value)}");
        }

        if (acti.WaterTypeFormId.HasValue)
        {
            lines.Add($"[cyan]Water Type:[/]  {resolver.FormatWithEditorId(acti.WaterTypeFormId.Value)}");
        }

        if (acti.Script.HasValue)
        {
            lines.Add($"[cyan]Script:[/]      {resolver.FormatWithEditorId(acti.Script.Value)}");
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader($"[bold]ACTI[/] {Markup.Escape(acti.EditorId ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }
}
