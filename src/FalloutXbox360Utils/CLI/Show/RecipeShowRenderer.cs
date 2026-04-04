using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Show;

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
