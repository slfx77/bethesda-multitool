using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Generates GECK-style text reports for Notes, Books, Terminals, and Messages.
///     These are text-heavy content record types that share similar formatting patterns.
/// </summary>
internal static class GeckTextContentWriter
{
    internal static void AppendNotesSection(StringBuilder sb, List<NoteRecord> notes)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Notes ({notes.Count})");

        foreach (var note in notes.OrderBy(n => n.EditorId ?? ""))
        {
            GeckReportHelpers.AppendRecordHeader(sb, "NOTE", note.EditorId);

            sb.AppendLine($"FormID:         {GeckReportHelpers.FormatFormId(note.FormId)}");
            sb.AppendLine($"Editor ID:      {note.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {note.FullName ?? "(none)"}");
            sb.AppendLine($"Type:           {note.NoteTypeName}");
            sb.AppendLine($"Endianness:     {(note.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{note.Offset:X8}");

            if (!string.IsNullOrEmpty(note.Text))
            {
                sb.AppendLine();
                sb.AppendLine("Text:");
                // Indent each line of the note text
                foreach (var line in note.Text.Split('\n'))
                {
                    sb.AppendLine($"  {line.TrimEnd('\r')}");
                }
            }
        }
    }

    /// <summary>
    ///     Generate a report for Notes only.
    /// </summary>
    internal static string GenerateNotesReport(List<NoteRecord> notes, Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendNotesSection(sb, notes);
        return sb.ToString();
    }

    internal static void AppendBooksSection(StringBuilder sb, List<BookRecord> books)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Books ({books.Count})");

        foreach (var book in books.OrderBy(b => b.EditorId ?? ""))
        {
            GeckReportHelpers.AppendRecordHeader(sb, "BOOK", book.EditorId);

            sb.AppendLine($"FormID:         {GeckReportHelpers.FormatFormId(book.FormId)}");
            sb.AppendLine($"Editor ID:      {book.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {book.FullName ?? "(none)"}");
            sb.AppendLine($"Value:          {book.Value} caps");
            sb.AppendLine($"Weight:         {book.Weight:F1}");
            sb.AppendLine($"Endianness:     {(book.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{book.Offset:X8}");

            if (book.TeachesSkill)
            {
                sb.AppendLine($"Teaches Skill:  {book.SkillTaught}");
            }

            if (!string.IsNullOrEmpty(book.Text))
            {
                sb.AppendLine();
                sb.AppendLine("Text:");
                foreach (var line in book.Text.Split('\n'))
                {
                    sb.AppendLine($"  {line.TrimEnd('\r')}");
                }
            }
        }
    }

    /// <summary>
    ///     Generate a report for Books only.
    /// </summary>
    internal static string GenerateBooksReport(List<BookRecord> books, Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendBooksSection(sb, books);
        return sb.ToString();
    }

    internal static void AppendTerminalsSection(StringBuilder sb, List<TerminalRecord> terminals)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Terminals ({terminals.Count})");

        foreach (var terminal in terminals.OrderBy(t => t.EditorId ?? ""))
        {
            GeckReportHelpers.AppendRecordHeader(sb, "TERM", terminal.EditorId);

            sb.AppendLine($"FormID:         {GeckReportHelpers.FormatFormId(terminal.FormId)}");
            sb.AppendLine($"Editor ID:      {terminal.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {terminal.FullName ?? "(none)"}");
            sb.AppendLine($"Difficulty:     {terminal.DifficultyName}");
            sb.AppendLine($"Endianness:     {(terminal.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{terminal.Offset:X8}");

            if (!string.IsNullOrEmpty(terminal.HeaderText))
            {
                sb.AppendLine();
                sb.AppendLine("Header:");
                foreach (var line in terminal.HeaderText.Split('\n'))
                {
                    sb.AppendLine($"  {line.TrimEnd('\r')}");
                }
            }

            if (terminal.MenuItems.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Menu Items ({terminal.MenuItems.Count}):");
                foreach (var item in terminal.MenuItems)
                {
                    sb.AppendLine($"  - {item.Text ?? "(no text)"}");
                }
            }
        }
    }

    /// <summary>
    ///     Generate a report for Terminals only.
    /// </summary>
    internal static string GenerateTerminalsReport(List<TerminalRecord> terminals,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendTerminalsSection(sb, terminals);
        return sb.ToString();
    }

    internal static void AppendMessagesSection(StringBuilder sb, List<MessageRecord> messages,
        FormIdResolver resolver)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Messages ({messages.Count})");
        sb.AppendLine();

        var messageBoxes = messages.Count(m => m.IsMessageBox);
        var autoDisplay = messages.Count(m => m.IsAutoDisplay);
        var withButtons = messages.Count(m => m.Buttons.Count > 0);
        var withQuest = messages.Count(m => m.QuestFormId != 0);
        sb.AppendLine($"Total Messages: {messages.Count:N0}");
        sb.AppendLine($"  Message Boxes:  {messageBoxes:N0}");
        sb.AppendLine($"  Auto-Display:   {autoDisplay:N0}");
        sb.AppendLine($"  With Buttons:   {withButtons:N0}");
        sb.AppendLine($"  With Quest Link: {withQuest:N0}");
        sb.AppendLine();

        foreach (var msg in messages.OrderBy(m => m.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(new string('\u2500', 80));
            sb.AppendLine($"  MESSAGE: {msg.EditorId ?? "(none)"} \u2014 {msg.FullName ?? "(unnamed)"}");
            sb.AppendLine($"  FormID:      {GeckReportHelpers.FormatFormId(msg.FormId)}");
            var flags = new List<string>();
            if (msg.IsMessageBox)
            {
                flags.Add("MessageBox");
            }

            if (msg.IsAutoDisplay)
            {
                flags.Add("AutoDisplay");
            }

            if (flags.Count > 0)
            {
                sb.AppendLine($"  Flags:       {string.Join(", ", flags)}");
            }

            if (msg.DisplayTime != 0)
            {
                sb.AppendLine($"  Display Time: {msg.DisplayTime}");
            }

            if (msg.QuestFormId != 0)
            {
                sb.AppendLine($"  Quest:       {resolver.FormatFull(msg.QuestFormId)}");
            }

            if (!string.IsNullOrEmpty(msg.Description))
            {
                sb.AppendLine($"  Text:        {msg.Description}");
            }

            if (msg.Buttons.Count > 0)
            {
                sb.AppendLine(
                    $"  \u2500\u2500 Buttons ({msg.Buttons.Count}) {new string('\u2500', 80 - 18 - msg.Buttons.Count.ToString().Length)}");
                for (var i = 0; i < msg.Buttons.Count; i++)
                {
                    sb.AppendLine($"    [{i + 1}] {msg.Buttons[i]}");
                }
            }

            if (!string.IsNullOrEmpty(msg.Icon))
            {
                sb.AppendLine($"  Icon:        {msg.Icon}");
            }

            sb.AppendLine();
        }
    }

    internal static string GenerateMessagesReport(List<MessageRecord> messages,
        FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendMessagesSection(sb, messages, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }
}
