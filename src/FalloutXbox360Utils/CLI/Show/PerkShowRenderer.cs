using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Show;

internal sealed class PerkShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var perk = records.Perks.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, p => p.FormId, p => p.EditorId));
        if (perk == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]      0x{perk.FormId:X8}",
            $"[cyan]EditorID:[/]    {Markup.Escape(perk.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]        {Markup.Escape(perk.FullName ?? "(none)")}",
            $"[cyan]Ranks:[/]       {perk.Ranks}",
            $"[cyan]Min Level:[/]   {perk.MinLevel}",
            $"[cyan]Playable:[/]    {perk.IsPlayable}",
            $"[cyan]Trait:[/]       {perk.IsTrait}"
        };

        if (!string.IsNullOrEmpty(perk.Description))
        {
            lines.Add("");
            lines.Add("[bold]Description:[/]");
            lines.Add($"  {Markup.Escape(perk.Description)}");
        }

        if (perk.Entries.Count > 0)
        {
            lines.Add("");
            lines.Add("[bold]Entries:[/]");
            foreach (var entry in perk.Entries)
            {
                var entryLine = $"  [{entry.TypeName}] Rank {entry.Rank}, Priority {entry.Priority}";
                if (entry.AbilityFormId is > 0)
                {
                    entryLine += $" \u2192 {resolver.FormatWithEditorId(entry.AbilityFormId.Value)}";
                }

                lines.Add(entryLine);
            }
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]PERK[/] {Markup.Escape(perk.EditorId ?? "")} — {Markup.Escape(perk.FullName ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }
}
