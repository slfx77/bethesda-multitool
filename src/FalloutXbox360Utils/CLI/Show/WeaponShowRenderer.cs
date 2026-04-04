using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Show;

internal sealed class WeaponShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var weapon = records.Weapons.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, w => w.FormId, w => w.EditorId));
        if (weapon == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]    0x{weapon.FormId:X8}",
            $"[cyan]EditorID:[/]  {Markup.Escape(weapon.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]      {Markup.Escape(weapon.FullName ?? "(none)")}",
            $"[cyan]Type:[/]      {weapon.WeaponTypeName}",
            $"[cyan]Equip:[/]     {weapon.EquipmentTypeName}",
            $"[cyan]Skill:[/]     {resolver.GetActorValueName((int)weapon.Skill) ?? $"AV#{weapon.Skill}"}",
            $"[cyan]Damage:[/]    {weapon.Damage}",
            $"[cyan]Crit %:[/]    {weapon.CriticalChance:P0}",
            $"[cyan]Crit Dmg:[/]  {weapon.CriticalDamage}",
            $"[cyan]Speed:[/]     {weapon.Speed:F2}",
            $"[cyan]Weight:[/]    {weapon.Weight:F1}",
            $"[cyan]Value:[/]     {weapon.Value}",
            $"[cyan]Health:[/]    {weapon.Health}"
        };

        if (weapon.StrengthRequirement > 0)
        {
            lines.Add($"[cyan]Str Req:[/]   {weapon.StrengthRequirement}");
        }

        if (weapon.SkillRequirement > 0)
        {
            lines.Add($"[cyan]Skill Req:[/] {weapon.SkillRequirement}");
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]WEAP[/] {Markup.Escape(weapon.EditorId ?? "")} — {Markup.Escape(weapon.FullName ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }
}
