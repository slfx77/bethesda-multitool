using FalloutXbox360Utils.Core.Formats.Esm.Enums;
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

internal sealed class ArmorShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver _,
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

internal sealed class RecipeShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var recipe = records.Recipes.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, rc => rc.FormId, rc => rc.EditorId));
        if (recipe == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]    0x{recipe.FormId:X8}",
            $"[cyan]EditorID:[/]  {Markup.Escape(recipe.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]      {Markup.Escape(recipe.FullName ?? "(none)")}"
        };

        if (recipe.RequiredSkill != 0 || recipe.RequiredSkillLevel != 0)
        {
            var skillName = resolver.GetSkillName(recipe.RequiredSkill) ?? $"Skill#{recipe.RequiredSkill}";
            lines.Add($"[cyan]Requires:[/]  {skillName} {recipe.RequiredSkillLevel}");
        }

        if (recipe.CategoryFormId != 0)
        {
            lines.Add($"[cyan]Category:[/]  {resolver.FormatWithEditorId(recipe.CategoryFormId)}");
        }

        if (recipe.SubcategoryFormId != 0)
        {
            lines.Add($"[cyan]Subcategory:[/] {resolver.FormatWithEditorId(recipe.SubcategoryFormId)}");
        }

        if (recipe.Ingredients.Count > 0)
        {
            lines.Add("");
            lines.Add($"[bold]Ingredients ({recipe.Ingredients.Count}):[/]");
            foreach (var ing in recipe.Ingredients)
            {
                lines.Add($"  {resolver.FormatWithEditorId(ing.ItemFormId)} x{ing.Count}");
            }
        }

        if (recipe.Outputs.Count > 0)
        {
            lines.Add("");
            lines.Add($"[bold]Outputs ({recipe.Outputs.Count}):[/]");
            foreach (var output in recipe.Outputs)
            {
                lines.Add($"  {resolver.FormatWithEditorId(output.ItemFormId)} x{output.Count}");
            }
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]RCPE[/] {Markup.Escape(recipe.EditorId ?? "")} — {Markup.Escape(recipe.FullName ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }

}

internal sealed class BookShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var book = records.Books.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, b => b.FormId, b => b.EditorId));
        if (book == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]    0x{book.FormId:X8}",
            $"[cyan]EditorID:[/]  {Markup.Escape(book.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]      {Markup.Escape(book.FullName ?? "(none)")}",
            $"[cyan]Value:[/]     {book.Value} caps",
            $"[cyan]Weight:[/]    {book.Weight:F1}"
        };

        if (book.Flags != 0)
        {
            lines.Add(
                $"[cyan]Flags:[/]     {FlagRegistry.DecodeFlagNamesWithHex(book.Flags, FlagRegistry.BookFlags)}");
        }

        if (book.TeachesSkill)
        {
            lines.Add(
                $"[cyan]Teaches:[/]   {resolver.GetSkillName(book.SkillTaught) ?? $"Skill#{book.SkillTaught}"}");
        }

        if (book.EnchantmentFormId is > 0)
        {
            lines.Add($"[cyan]Enchantment:[/] {resolver.FormatWithEditorId(book.EnchantmentFormId.Value)}");
            if (book.EnchantmentAmount != 0)
            {
                lines.Add($"[cyan]Enchant Amt:[/] {book.EnchantmentAmount}");
            }
        }

        if (!string.IsNullOrEmpty(book.ModelPath))
        {
            lines.Add($"[cyan]Model:[/]     {Markup.Escape(book.ModelPath)}");
        }

        if (!string.IsNullOrEmpty(book.Text))
        {
            var text = book.Text.Length > 2000 ? book.Text[..2000] + "\n... (truncated)" : book.Text;
            lines.Add("");
            lines.Add("[bold]Text:[/]");
            lines.Add(Markup.Escape(text));
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]BOOK[/] {Markup.Escape(book.EditorId ?? "")} — {Markup.Escape(book.FullName ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }
}
