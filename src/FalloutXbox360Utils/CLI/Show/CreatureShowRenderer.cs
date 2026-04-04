using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Show;

internal sealed class CreatureShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var creature = records.Creatures.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, c => c.FormId, c => c.EditorId));
        if (creature == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var panel = new Panel(BuildContent(creature, resolver))
        {
            Header = new PanelHeader(
                $"[bold]CREA[/] {Markup.Escape(creature.EditorId ?? "")} — {Markup.Escape(creature.FullName ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }

    private static string BuildContent(CreatureRecord creature, FormIdResolver resolver)
    {
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]       0x{creature.FormId:X8}",
            $"[cyan]EditorID:[/]     {Markup.Escape(creature.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]         {Markup.Escape(creature.FullName ?? "(none)")}",
            $"[cyan]Type:[/]         {creature.CreatureTypeName}",
            $"[cyan]Level:[/]        {creature.Stats?.Level.ToString() ?? "(unknown)"}"
        };

        lines.Add("");
        lines.Add("[bold]Combat:[/]");
        lines.Add($"  Attack Damage:  {creature.AttackDamage}");
        lines.Add($"  Combat Skill:   {creature.CombatSkill}");
        lines.Add($"  Magic Skill:    {creature.MagicSkill}");
        lines.Add($"  Stealth Skill:  {creature.StealthSkill}");

        if (creature.Stats != null)
        {
            lines.Add("");
            lines.Add("[bold]Stats (ACBS):[/]");
            lines.Add($"  Fatigue:    {creature.Stats.FatigueBase}");
            lines.Add($"  Speed Mult: {creature.Stats.SpeedMultiplier}");
            lines.Add($"  Calc Range: {creature.Stats.CalcMin} - {creature.Stats.CalcMax}");
            if (creature.Stats.Flags != 0)
            {
                lines.Add($"  Flags:      0x{creature.Stats.Flags:X8}");
            }
        }

        if (creature.AiData != null)
        {
            var ai = creature.AiData;
            lines.Add("");
            lines.Add("[bold]AI Data:[/]");
            lines.Add($"  Aggression:     {ai.AggressionName}");
            lines.Add($"  Confidence:     {ai.ConfidenceName}");
            lines.Add($"  Assistance:     {ai.AssistanceName}");
            lines.Add($"  Mood:           {ai.MoodName}");
            lines.Add($"  Energy:         {ai.EnergyLevel}");
            lines.Add($"  Responsibility: {ai.ResponsibilityName}");
        }

        if (creature.Script.HasValue)
        {
            lines.Add($"[cyan]Script:[/]       {resolver.FormatWithEditorId(creature.Script.Value)}");
        }

        if (creature.DeathItem.HasValue)
        {
            lines.Add($"[cyan]Death Item:[/]   {resolver.FormatWithEditorId(creature.DeathItem.Value)}");
        }

        if (creature.Factions is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("[bold]Factions:[/]");
            foreach (var faction in creature.Factions)
            {
                lines.Add($"  {resolver.FormatWithEditorId(faction.FactionFormId)} (rank {faction.Rank})");
            }
        }

        if (creature.Spells is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("[bold]Spells/Abilities:[/]");
            foreach (var spell in creature.Spells)
            {
                lines.Add($"  {resolver.FormatWithEditorId(spell)}");
            }
        }

        if (creature.Packages is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("[bold]AI Packages:[/]");
            foreach (var package in creature.Packages)
            {
                lines.Add($"  {resolver.FormatWithEditorId(package)}");
            }
        }

        if (!string.IsNullOrEmpty(creature.ModelPath))
        {
            lines.Add($"[cyan]Model:[/]        {Markup.Escape(creature.ModelPath)}");
        }

        return string.Join("\n", lines);
    }
}
