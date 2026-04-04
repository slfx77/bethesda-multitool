using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Show;

internal sealed class RaceShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var race = records.Races.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, n => n.FormId, n => n.EditorId));
        if (race == null)
            return false;

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]      0x{race.FormId:X8}",
            $"[cyan]EditorID:[/]    {Markup.Escape(race.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]        {Markup.Escape(race.FullName ?? "(none)")}",
            $"[cyan]Playable:[/]    {race.IsPlayable}",
            $"[cyan]Height:[/]      M={race.MaleHeight:F2}  F={race.FemaleHeight:F2}",
            $"[cyan]Weight:[/]      M={race.MaleWeight:F2}  F={race.FemaleWeight:F2}",
            $"[cyan]Flags:[/]       0x{race.DataFlags:X8}"
        };

        if (race.OlderRaceFormId.HasValue)
            lines.Add($"[cyan]Older Race:[/]  {resolver.FormatWithEditorId(race.OlderRaceFormId.Value)}");
        if (race.YoungerRaceFormId.HasValue)
            lines.Add($"[cyan]Younger Race:[/] {resolver.FormatWithEditorId(race.YoungerRaceFormId.Value)}");

        lines.Add("");
        lines.Add("[bold]Head Parts (NAM0):[/]");
        lines.Add($"  [cyan]Male Head Mesh:[/]    {Markup.Escape(race.MaleHeadModelPath ?? "(none)")}");
        lines.Add($"  [cyan]Female Head Mesh:[/]  {Markup.Escape(race.FemaleHeadModelPath ?? "(none)")}");
        lines.Add($"  [cyan]Male Head Tex:[/]     {Markup.Escape(race.MaleHeadTexturePath ?? "(none)")}");
        lines.Add($"  [cyan]Female Head Tex:[/]   {Markup.Escape(race.FemaleHeadTexturePath ?? "(none)")}");
        lines.Add($"  [cyan]Male Mouth Mesh:[/]   {Markup.Escape(race.MaleMouthModelPath ?? "(none)")}");
        lines.Add($"  [cyan]Female Mouth Mesh:[/] {Markup.Escape(race.FemaleMouthModelPath ?? "(none)")}");
        lines.Add($"  [cyan]Male Lower Teeth:[/]  {Markup.Escape(race.MaleLowerTeethModelPath ?? "(none)")}");
        lines.Add($"  [cyan]Female Lower Teeth:[/] {Markup.Escape(race.FemaleLowerTeethModelPath ?? "(none)")}");
        lines.Add($"  [cyan]Male Upper Teeth:[/]  {Markup.Escape(race.MaleUpperTeethModelPath ?? "(none)")}");
        lines.Add($"  [cyan]Female Upper Teeth:[/] {Markup.Escape(race.FemaleUpperTeethModelPath ?? "(none)")}");
        lines.Add($"  [cyan]Male Tongue:[/]       {Markup.Escape(race.MaleTongueModelPath ?? "(none)")}");
        lines.Add($"  [cyan]Female Tongue:[/]     {Markup.Escape(race.FemaleTongueModelPath ?? "(none)")}");

        lines.Add("");
        lines.Add("[bold]Body Parts (NAM1):[/]");
        lines.Add($"  [cyan]Male Upper Body:[/]   {Markup.Escape(race.MaleUpperBodyPath ?? "(none)")}");
        lines.Add($"  [cyan]Female Upper Body:[/] {Markup.Escape(race.FemaleUpperBodyPath ?? "(none)")}");
        lines.Add($"  [cyan]Male Left Hand:[/]    {Markup.Escape(race.MaleLeftHandPath ?? "(none)")}");
        lines.Add($"  [cyan]Female Left Hand:[/]  {Markup.Escape(race.FemaleLeftHandPath ?? "(none)")}");
        lines.Add($"  [cyan]Male Right Hand:[/]   {Markup.Escape(race.MaleRightHandPath ?? "(none)")}");
        lines.Add($"  [cyan]Female Right Hand:[/] {Markup.Escape(race.FemaleRightHandPath ?? "(none)")}");
        lines.Add($"  [cyan]Male Body Tex:[/]     {Markup.Escape(race.MaleBodyTexturePath ?? "(none)")}");
        lines.Add($"  [cyan]Female Body Tex:[/]   {Markup.Escape(race.FemaleBodyTexturePath ?? "(none)")}");

        if (race.AbilityFormIds.Count > 0)
        {
            lines.Add("");
            lines.Add("[bold]Abilities:[/]");
            foreach (var abilId in race.AbilityFormIds)
                lines.Add($"  {resolver.FormatWithEditorId(abilId)}");
        }

        if (race.SkillBoosts.Count > 0)
        {
            lines.Add("");
            lines.Add("[bold]Skill Boosts:[/]");
            foreach (var (skillIndex, boost) in race.SkillBoosts)
                lines.Add($"  Skill {skillIndex}: {(boost > 0 ? "+" : "")}{boost}");
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]RACE[/] {Markup.Escape(race.EditorId ?? "")} — {Markup.Escape(race.FullName ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }
}
