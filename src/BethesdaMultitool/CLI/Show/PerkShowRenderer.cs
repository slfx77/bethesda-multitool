using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Show;

internal sealed class PerkShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId, ShowRenderContext context)
    {
        var perk = records.Perks.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, p => p.FormId, p => p.EditorId));
        if (perk == null)
        {
            return false;
        }

        context.Console.WriteLine();
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

        foreach (var issue in perk.RuntimeRecoveryIssues) { lines.Add($"[cyan]Runtime Recovery:[/] {Markup.Escape(issue)}"); }

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
            for (var entryIndex = 0; entryIndex < perk.Entries.Count; entryIndex++)
            {
                var entry = perk.Entries[entryIndex];
                lines.Add($"  Entry [[{entryIndex}]]");
                foreach (var field in PerkEffectProjection.Fields(entry, resolver).Concat(PerkEffectProjection.Conditions(entry)))
                {
                    lines.Add($"    {Markup.Escape(field.Key)}: {Markup.Escape(field.Value)}");
                }
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
                    $"  {Markup.Escape(condition.FunctionName)}({Markup.Escape(parameter)}) {condition.OperatorDisplay} {PerkEffectProjection.Comparison(condition)}");
                foreach (var field in PerkEffectProjection.ConditionFields(condition))
                {
                    lines.Add($"    {Markup.Escape(field.Key)}: {Markup.Escape(field.Value)}");
                }
            }
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]PERK[/] {Markup.Escape(perk.EditorId ?? "")} — {Markup.Escape(perk.FullName ?? "")}")
        };
        context.Console.Write(panel);
        return true;
    }
}
