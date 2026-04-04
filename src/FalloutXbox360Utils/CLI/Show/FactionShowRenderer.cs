using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Show;

internal sealed class FactionShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var faction =
            records.Factions.FirstOrDefault(r =>
                ShowHelpers.Matches(r, formId, editorId, f => f.FormId, f => f.EditorId));
        if (faction == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]   0x{faction.FormId:X8}",
            $"[cyan]EditorID:[/] {Markup.Escape(faction.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]     {Markup.Escape(faction.FullName ?? "(none)")}",
            $"[cyan]Flags:[/]    0x{faction.Flags:X4}"
        };

        if (faction.Relations is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("[bold]Relations:[/]");
            foreach (var rel in faction.Relations)
            {
                lines.Add(
                    $"  {resolver.FormatWithEditorId(rel.FactionFormId)}: {rel.Modifier} (combat: 0x{rel.CombatFlags:X})");
            }
        }

        if (faction.Ranks is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("[bold]Ranks:[/]");
            foreach (var rank in faction.Ranks)
            {
                lines.Add(
                    $"  [[{rank.RankNumber}]] {Markup.Escape(rank.MaleTitle ?? rank.FemaleTitle ?? "(unnamed)")}");
            }
        }

        var members = new List<(string type, string label, sbyte rank)>();
        foreach (var npc in records.Npcs)
        {
            var membership = npc.Factions.FirstOrDefault(f => f.FactionFormId == faction.FormId);
            if (membership != null)
            {
                var label = resolver.FormatWithEditorId(npc.FormId);
                members.Add(("NPC_", label, membership.Rank));
            }
        }

        foreach (var creature in records.Creatures)
        {
            var membership = creature.Factions.FirstOrDefault(f => f.FactionFormId == faction.FormId);
            if (membership != null)
            {
                var label = resolver.FormatWithEditorId(creature.FormId);
                members.Add(("CREA", label, membership.Rank));
            }
        }

        if (members.Count > 0)
        {
            lines.Add("");
            lines.Add($"[bold]Members ({members.Count}):[/]");
            foreach (var (type, label, rank) in members.OrderBy(m => m.type).ThenBy(m => m.label))
            {
                lines.Add($"  [grey]{type}[/] {label} (rank {rank})");
            }
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]FACT[/] {Markup.Escape(faction.EditorId ?? "")} — {Markup.Escape(faction.FullName ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }
}
