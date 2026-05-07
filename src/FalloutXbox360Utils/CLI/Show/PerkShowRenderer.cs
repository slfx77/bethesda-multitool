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
                var entryLine = $"  {Markup.Escape(entry.TypeName)}: Rank {entry.Rank}, Priority {entry.Priority}";
                if (entry.AbilityFormId is > 0)
                {
                    entryLine += $" \u2192 {resolver.FormatWithEditorId(entry.AbilityFormId.Value)}";
                }

                if (entry.FunctionTypeName != null)
                {
                    entryLine += $", {Markup.Escape(entry.FunctionTypeName)}";
                }

                if (!string.IsNullOrEmpty(entry.EffectData))
                {
                    entryLine += $", Data {Markup.Escape(entry.EffectData)}";
                }

                lines.Add(entryLine);
            }
        }

        if (perk.Conditions.Count > 0)
        {
            lines.Add("");
            lines.Add("[bold]Conditions:[/]");
            foreach (var condition in perk.Conditions)
            {
                var parameter = condition.Parameter1Display
                                ?? (condition.Parameter1FormId.HasValue
                                    ? resolver.FormatWithEditorId(condition.Parameter1FormId.Value)
                                    : condition.Parameter1.ToString());
                lines.Add(
                    $"  {Markup.Escape(condition.FunctionName)}({Markup.Escape(parameter)}) {condition.OperatorDisplay} {condition.ComparisonValue:G}");
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
