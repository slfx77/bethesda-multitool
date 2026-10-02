using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Show;

internal sealed class BookShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId, ShowRenderContext context)
    {
        var book = records.Books.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, b => b.FormId, b => b.EditorId));
        if (book == null)
        {
            return false;
        }

        context.Console.WriteLine();
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
                $"[cyan]Teaches:[/]   {ShowHelpers.Plain(resolver.GetSkillName(book.SkillTaught), $"Skill#{book.SkillTaught}")}");
        }

        if (book.EnchantmentFormId is > 0)
        {
            lines.Add($"[cyan]Enchantment:[/] {ShowHelpers.Ref(resolver, book.EnchantmentFormId.Value)}");
            if (book.EnchantmentAmount != 0)
            {
                lines.Add($"[cyan]Enchant Amt:[/] {book.EnchantmentAmount}");
            }
        }

        if (!string.IsNullOrEmpty(book.ModelPath))
        {
            lines.Add($"[cyan]Model:[/]     {Markup.Escape(book.ModelPath)}");
        }

        // Under --full the text is written after the panel exactly as stored (see WriteVerbatimBlocks);
        // by default it is cut at the cap with a marker naming the total and --full.
        var blocks = new List<VerbatimBlock>();
        if (!string.IsNullOrEmpty(book.Text))
        {
            lines.Add("");
            if (context.FullText)
            {
                lines.Add($"[bold]Text:[/] {ShowHelpers.VerbatimPlaceholder(book.Text)}");
                blocks.Add(new VerbatimBlock("Book text", book.Text));
            }
            else
            {
                lines.Add("[bold]Text:[/]");
                lines.Add(Markup.Escape(ShowHelpers.TruncateForPanel(book.Text)));
            }
        }

        ShowHelpers.AppendNestedPayloads(lines, records, book.FormId, resolver);

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]BOOK[/] {Markup.Escape(book.EditorId ?? "")} — {Markup.Escape(book.FullName ?? "")}")
        };
        context.Console.Write(panel);
        ShowHelpers.WriteVerbatimBlocks(context.Console, blocks);
        return true;
    }
}
