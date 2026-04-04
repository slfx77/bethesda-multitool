using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Show;

internal sealed class ArmorShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var armor = records.Armor.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, a => a.FormId, a => a.EditorId));
        if (armor == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]    0x{armor.FormId:X8}",
            $"[cyan]EditorID:[/]  {Markup.Escape(armor.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]      {Markup.Escape(armor.FullName ?? "(none)")}",
            $"[cyan]DT:[/]        {armor.DamageThreshold:F1}",
            $"[cyan]Weight:[/]    {armor.Weight:F1}",
            $"[cyan]Value:[/]     {armor.Value}",
            $"[cyan]Health:[/]    {armor.Health}"
        };

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]ARMO[/] {Markup.Escape(armor.EditorId ?? "")} — {Markup.Escape(armor.FullName ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }
}
