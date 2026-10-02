using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Show;

internal sealed class ActivatorShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId, ShowRenderContext context)
    {
        var acti = records.Activators.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, a => a.FormId, a => a.EditorId));
        if (acti == null)
        {
            return false;
        }

        context.Console.WriteLine();
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
            lines.Add($"[cyan]Activ Sound:[/] {ShowHelpers.Ref(resolver, acti.ActivationSoundFormId.Value)}");
        }

        if (acti.RadioStationFormId.HasValue)
        {
            lines.Add($"[cyan]Radio:[/]       {ShowHelpers.Ref(resolver, acti.RadioStationFormId.Value)}");
        }

        if (acti.WaterTypeFormId.HasValue)
        {
            lines.Add($"[cyan]Water Type:[/]  {ShowHelpers.Ref(resolver, acti.WaterTypeFormId.Value)}");
        }

        if (acti.Script.HasValue)
        {
            lines.Add($"[cyan]Script:[/]      {ShowHelpers.Ref(resolver, acti.Script.Value)}");
        }

        ShowHelpers.AppendNestedPayloads(lines, records, acti.FormId, resolver);

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader($"[bold]ACTI[/] {Markup.Escape(acti.EditorId ?? "")}")
        };
        context.Console.Write(panel);
        return true;
    }
}
