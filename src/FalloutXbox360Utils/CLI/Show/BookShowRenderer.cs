using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Show;

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
