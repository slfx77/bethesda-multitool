using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Export.Report;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Export.Geck;

/// <summary>
///     Generates GECK-style text reports for Notes, Books, Terminals, and Messages.
///     These are text-heavy content record types that share similar formatting patterns.
/// </summary>
internal static class GeckTextContentWriter
{
    internal static RecordReport BuildNoteReport(NoteRecord note, FormIdResolver? resolver = null)
    {
        resolver ??= FormIdResolver.Empty;
        var sections = new List<ReportSection>
        {
            new("Identity",
            [
                new ReportField("Type", ReportValue.String(note.NoteTypeName)),
                new ReportField("Endianness",
                    ReportValue.String(note.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")),
                new ReportField("Offset", ReportValue.String($"0x{note.Offset:X8}"))
            ])
        };

        var artFields = new List<ReportField>();
        if (!string.IsNullOrEmpty(note.ModelPath))
        {
            artFields.Add(new ReportField("Model", ReportValue.String(note.ModelPath)));
        }

        if (!string.IsNullOrEmpty(note.IconPath))
        {
            artFields.Add(new ReportField("Inventory Icon", ReportValue.String(note.IconPath)));
        }

        if (!string.IsNullOrEmpty(note.TexturePath))
        {
            artFields.Add(new ReportField("Menu Icon", ReportValue.String(note.TexturePath)));
        }

        if (artFields.Count > 0)
        {
            sections.Add(new ReportSection("Art Assets", artFields));
        }

        var referenceFields = new List<ReportField>();
        if (note.SoundFormId is > 0)
        {
            referenceFields.Add(new ReportField("Audio",
                ReportValue.FormId(note.SoundFormId.Value, resolver),
                $"0x{note.SoundFormId.Value:X8}"));
        }

        if (note.ObjectFormId is > 0)
        {
            referenceFields.Add(new ReportField("Object",
                ReportValue.FormId(note.ObjectFormId.Value, resolver),
                $"0x{note.ObjectFormId.Value:X8}"));
        }

        if (note.TopicFormId is > 0)
        {
            referenceFields.Add(new ReportField("Topic",
                ReportValue.FormId(note.TopicFormId.Value, resolver),
                $"0x{note.TopicFormId.Value:X8}"));
        }

        if (referenceFields.Count > 0)
        {
            sections.Add(new ReportSection("References", referenceFields));
        }

        if (!string.IsNullOrEmpty(note.Text))
        {
            sections.Add(new ReportSection("Content",
            [
                new ReportField("Text", ReportValue.String(note.Text))
            ]));
        }

        return new RecordReport("Note", note.FormId, note.EditorId, note.FullName, sections);
    }

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
            if (!string.IsNullOrEmpty(note.ModelPath))
            {
                sb.AppendLine($"Model:          {note.ModelPath}");
            }

            if (!string.IsNullOrEmpty(note.IconPath))
            {
                sb.AppendLine($"Icon:           {note.IconPath}");
            }

            if (!string.IsNullOrEmpty(note.TexturePath))
            {
                sb.AppendLine($"Menu Icon:      {note.TexturePath}");
            }

            if (note.SoundFormId is > 0)
            {
                sb.AppendLine($"Audio:          {GeckReportHelpers.FormatFormId(note.SoundFormId.Value)}");
            }

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
    internal static string GenerateNotesReport(List<NoteRecord> notes, Dictionary<uint, string>? _lookup = null)
    {
        var sb = new StringBuilder();
        AppendNotesSection(sb, notes);
        return sb.ToString();
    }

    internal static void AppendBooksSection(StringBuilder sb, List<BookRecord> books,
        FormIdResolver resolver)
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

            if (book.Flags != 0)
            {
                sb.AppendLine(
                    $"Flags:          {FlagRegistry.DecodeFlagNamesWithHex(book.Flags, FlagRegistry.BookFlags)}");
            }

            if (book.TeachesSkill)
            {
                sb.AppendLine(
                    $"Teaches Skill:  {resolver.GetSkillName(book.SkillTaught) ?? $"Skill#{book.SkillTaught}"}");
            }

            if (book.EnchantmentFormId is > 0)
            {
                sb.AppendLine($"Enchantment:    {resolver.FormatFull(book.EnchantmentFormId.Value)}");
                if (book.EnchantmentAmount != 0)
                {
                    sb.AppendLine($"Enchant Amount: {book.EnchantmentAmount}");
                }
            }

            sb.AppendLine($"Endianness:     {(book.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{book.Offset:X8}");

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
    internal static string GenerateBooksReport(List<BookRecord> books, FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendBooksSection(sb, books, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }

    /// <summary>
    ///     Appends the Terminals section. Every legacy line keeps its place (identity lines, the Header block and
    ///     the <c>Menu Items (n):</c> list of item texts); added after them are the record's DNAM flags, server
    ///     type, SCRI/PNAM/SNAM links, the terminals whose items open this one ("Linked From"), and one full
    ///     block per menu item: its stable 1-based index and text, RNAM result text, ANAM flags, display note,
    ///     sub-menu, every condition and its embedded result script (provenance-labeled source, the separately
    ///     labeled BethesdaMultitool decompilation, locals and references). Nothing is truncated.
    /// </summary>
    /// <param name="sb">The report being built.</param>
    /// <param name="terminals">The TERM records.</param>
    /// <param name="resolver">EditorID/display-name source.</param>
    /// <param name="conditions">
    ///     Condition context (game and quest-variable names); null assumes <see cref="GameProfiles.DefaultGame" />
    ///     and says so.
    /// </param>
    /// <param name="isMemoryDumpInput">True when the records came from a memory dump (see <see cref="ScriptSourceProvenance" />).</param>
    internal static void AppendTerminalsSection(
        StringBuilder sb,
        List<TerminalRecord> terminals,
        FormIdResolver resolver,
        ConditionDisplayContext? conditions = null,
        bool isMemoryDumpInput = false)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        conditions ??= ConditionDisplayContext.ForResolver(resolver, GameProfiles.DefaultGame, gameAssumed: true);

        GeckReportHelpers.AppendSectionHeader(sb, $"Terminals ({terminals.Count})");
        if (conditions.GameAssumed && terminals.Any(t => t.MenuItems.Any(i => i.Conditions.Count > 0)))
        {
            sb.AppendLine(
                $"Conditions:     function names assume {conditions.Game} (not detected from the input)");
        }

        var incoming = TerminalMenuItemDescriber.BuildIncomingLinks(terminals);
        foreach (var terminal in terminals.OrderBy(t => t.EditorId ?? ""))
        {
            GeckReportHelpers.AppendRecordHeader(sb, "TERM", terminal.EditorId);

            sb.AppendLine($"FormID:         {GeckReportHelpers.FormatFormId(terminal.FormId)}");
            sb.AppendLine($"Editor ID:      {terminal.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {terminal.FullName ?? "(none)"}");
            sb.AppendLine($"Difficulty:     {terminal.DifficultyName}");
            sb.AppendLine($"Endianness:     {(terminal.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{terminal.Offset:X8}");
            sb.AppendLine($"Flags:          {TerminalMenuItemDescriber.FormatTerminalFlags(terminal.Flags)}");
            sb.AppendLine($"Server Type:    {TerminalMenuItemDescriber.FormatServerType(terminal.ServerType)}");
            AppendFormLine(sb, "Script:", terminal.ScriptFormId, resolver);
            AppendFormLine(sb, "Password Note:", terminal.PasswordNoteFormId, resolver);
            AppendFormLine(sb, "Sound Loop:", terminal.SoundLoopFormId, resolver);

            if (incoming.TryGetValue(terminal.FormId, out var parents))
            {
                sb.AppendLine($"Linked From ({parents.Count}):");
                foreach (var link in parents)
                {
                    sb.AppendLine($"  {TerminalMenuItemDescriber.FormatIncomingLink(link)}");
                }
            }

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

                foreach (var description in TerminalMenuItemDescriber.Describe(terminal, conditions, isMemoryDumpInput))
                {
                    AppendMenuItemBlock(sb, description);
                }
            }
        }
    }

    /// <summary>
    ///     Generate a report for Terminals only.
    /// </summary>
    internal static string GenerateTerminalsReport(
        List<TerminalRecord> terminals,
        FormIdResolver? resolver = null,
        ConditionDisplayContext? conditions = null,
        bool isMemoryDumpInput = false)
    {
        var sb = new StringBuilder();
        AppendTerminalsSection(sb, terminals, resolver ?? conditions?.Resolver ?? FormIdResolver.Empty, conditions,
            isMemoryDumpInput);
        return sb.ToString();
    }

    /// <summary>
    ///     Appends an embedded script (a terminal menu item's or an INFO result-script slot's) in the report
    ///     layout shared by terminal_report.txt and dialogue_report.txt: a heading line with the summary, then
    ///     (unless the block is empty) its provenance token, bytecode order, the source under its
    ///     <see cref="ScriptSourceProvenance" /> label, the decompiled text under
    ///     <see cref="ScriptSourceProvenance.DecompiledTextLabel" />, locals, references and any incomplete-bundle
    ///     note. Bodies are written in full, one report line per source line (CR stripped, TABs kept).
    /// </summary>
    internal static void AppendEmbeddedScript(StringBuilder sb, string indent, EmbeddedScriptText script)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(script);

        AppendLabeledText(sb, indent, $"{script.Heading}:", script.Summary);
        var classification = script.Classification;
        var empty = classification.Kind == ScriptTextKind.None && script.DecompiledText is null && script.BytecodeOrder is null &&
                    script.Variables.Count == 0 && script.References.Count == 0 && script.BundleNote is null;
        if (empty)
        {
            return;
        }

        var inner = indent + "  ";
        sb.AppendLine($"{inner}{"Provenance:",-16}{FormatProvenance(classification)}");
        if (script.BytecodeOrder is { } order)
        {
            sb.AppendLine($"{inner}{"Bytecode Order:",-16}{order}");
        }

        if (classification.HasSourceText && script.SourceText is { } source)
        {
            sb.AppendLine($"{inner}{classification.Label}:");
            AppendTextLines(sb, inner + "  ", source);
        }
        else
        {
            sb.AppendLine($"{inner}{"Source:",-16}{classification.Label}");
        }

        if (script.DecompiledText is { } decompiled)
        {
            sb.AppendLine($"{inner}{ScriptSourceProvenance.DecompiledTextLabel}:");
            AppendTextLines(sb, inner + "  ", decompiled);
        }

        if (script.Variables.Count > 0)
        {
            sb.AppendLine($"{inner}Variables ({script.Variables.Count}):");
            foreach (var variable in script.Variables)
            {
                sb.AppendLine($"{inner}  [{variable.Index}] {ScriptVariableTypeResolver.FormatDeclaration(variable, script.RawReferences)}");
            }
        }

        if (script.References.Count > 0)
        {
            sb.AppendLine($"{inner}References ({script.References.Count}):");
            for (var i = 0; i < script.References.Count; i++)
            {
                sb.AppendLine($"{inner}  {i + 1}: {script.References[i]}");
            }
        }

        if (script.BundleNote is { } note)
        {
            sb.AppendLine($"{inner}{"Bundle:",-16}{note}");
        }
    }

    /// <summary>
    ///     Writes <paramref name="text" /> one report line per source line, each prefixed by
    ///     <paramref name="indent" />. CR is stripped, TABs are kept, and a terminal line break adds no empty line.
    /// </summary>
    internal static void AppendTextLines(StringBuilder sb, string indent, string text)
    {
        var lines = text.Split('\n');
        var count = lines.Length > 1 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        for (var i = 0; i < count; i++)
        {
            sb.Append(indent).AppendLine(lines[i].TrimEnd('\r'));
        }
    }

    /// <summary>
    ///     A <c>Label:</c> value line (label padded to the report's 16-column field), or, for a value that spans
    ///     lines, the label on its own line and the value indented beneath it.
    /// </summary>
    internal static void AppendLabeledText(StringBuilder sb, string indent, string label, string text)
    {
        if (text.AsSpan().IndexOfAny('\r', '\n') < 0)
        {
            sb.AppendLine($"{indent}{PadLabel(label)}{text}");
            return;
        }

        sb.AppendLine($"{indent}{label}");
        AppendTextLines(sb, indent + "  ", text);
    }

    /// <summary>Pads a <c>Label:</c> to the report's 16-column field, always leaving at least one space.</summary>
    private static string PadLabel(string label)
    {
        return label.Length >= 16 ? label + " " : label.PadRight(16);
    }

    private static void AppendMenuItemBlock(StringBuilder sb, TerminalMenuItemDescriber.ItemDescription item)
    {
        sb.AppendLine();
        var text = item.Text is null ? "(no text)" : item.Text.ReplaceLineEndings(" ");
        sb.AppendLine($"Menu Item [{item.Index}] {text}");
        if (item.Text is not null && item.Text.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            AppendLabeledText(sb, "  ", "Item Text:", item.Text);
        }

        AppendLabeledText(sb, "  ", "Result Text:", item.ResultText ?? "(no RNAM)");
        sb.AppendLine($"  {"Flags:",-16}{item.FlagsDisplay}");
        if (item.DisplayNoteLabel is { } note)
        {
            sb.AppendLine($"  {"Display Note:",-16}{note}");
        }

        if (item.SubTerminalLabel is { } subMenu)
        {
            sb.AppendLine($"  {"Sub-menu:",-16}{subMenu}");
        }

        if (item.ConditionLines.Count > 0)
        {
            sb.AppendLine($"  Conditions ({item.ConditionLines.Count}):");
            for (var i = 0; i < item.ConditionLines.Count; i++)
            {
                sb.AppendLine($"    {item.Conditions[i].Index}: {item.ConditionLines[i]}");
            }

            if (item.ConditionGrouping is { } grouping)
            {
                sb.AppendLine($"  {"Grouping:",-16}{grouping}");
            }
        }

        var script = item.Script;
        AppendEmbeddedScript(sb, "  ", new EmbeddedScriptText(
            "Result Script",
            script.Summary,
            script.Classification,
            script.SourceText,
            script.DecompiledText,
            script.BytecodeOrder,
            script.Variables,
            script.References.Select(reference => reference.Display).ToArray(),
            script.BundleNote,
            script.References.Select(reference => reference.Raw).ToArray()));
    }

    private static string FormatProvenance(ScriptSourceClassification classification)
    {
        return classification.CorrespondenceToken == ScriptSourceProvenance.CorrespondenceNotApplicable
            ? classification.Token
            : $"{classification.Token} (correspondence: {classification.CorrespondenceToken})";
    }

    private static void AppendFormLine(StringBuilder sb, string label, uint? formId, FormIdResolver resolver)
    {
        if (formId is { } id && id != 0)
        {
            sb.AppendLine($"{PadLabel(label)}{TerminalMenuItemDescriber.FormatFormId(id, resolver)}");
        }
    }

    /// <summary>
    ///     One embedded script as <see cref="AppendEmbeddedScript" /> prints it. Every string is plain text.
    /// </summary>
    /// <param name="Heading">The slot heading, e.g. <c>Result Script</c> or <c>Result Script (Begin)</c>.</param>
    /// <param name="Summary">One-line summary: compiled size, or why there is no code.</param>
    /// <param name="Classification">How the source text is labeled.</param>
    /// <param name="SourceText">The source text in full, or null.</param>
    /// <param name="DecompiledText">BethesdaMultitool's decompilation in full, or null.</param>
    /// <param name="BytecodeOrder">The byte order the SCDA was decoded with, or null without SCDA.</param>
    /// <param name="Variables">The SLSD/SCVR locals.</param>
    /// <param name="References">The SCRO/SCRV table as text, in stored order.</param>
    /// <param name="BundleNote">The incomplete-bundle wording, or null.</param>
    /// <param name="RawReferences">The owning script's SCRO/SCRV values, used to distinguish reference locals from floats.</param>
    internal sealed record EmbeddedScriptText(
        string Heading,
        string Summary,
        ScriptSourceClassification Classification,
        string? SourceText,
        string? DecompiledText,
        string? BytecodeOrder,
        IReadOnlyList<ScriptVariableInfo> Variables,
        IReadOnlyList<string> References,
        string? BundleNote,
        IReadOnlyList<uint> RawReferences);

    internal static void AppendMessagesSection(StringBuilder sb, List<MessageRecord> messages,
        FormIdResolver resolver, ConditionDisplayContext? conditions = null)
    {
        conditions ??= ConditionDisplayContext.ForResolver(resolver, GameProfiles.DefaultGame, gameAssumed: true);
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

            foreach (var line in RuntimeMessageEvidenceFormatter.FormatSelectedSources(msg))
            {
                sb.AppendLine($"  {line}");
            }
            if (msg.ButtonSource == MessageFieldSource.RuntimeObject || msg.RuntimeButtons is null)
                foreach (var line in RuntimeMessageEvidenceFormatter.Format(msg.RuntimeEvidence))
                    sb.AppendLine($"  {line}");

            if (msg.UnassignedConditions.Count > 0)
            {
                sb.AppendLine("  Unassigned conditions (before first button; ownership unknown):");
                foreach (var line in MessageConditionFormatter.Format(msg.UnassignedConditions, conditions))
                {
                    sb.AppendLine($"    {line}");
                }
            }

            if (msg.Buttons.Count > 0)
            {
                sb.AppendLine(
                    $"  \u2500\u2500 Buttons ({msg.Buttons.Count}) {new string('\u2500', 80 - 18 - msg.Buttons.Count.ToString().Length)}");
                for (var i = 0; i < msg.Buttons.Count; i++)
                {
                    sb.AppendLine($"    [{i + 1}] {msg.Buttons[i]}");
                    foreach (var line in MessageConditionFormatter.Format(msg.GetButtonConditions(i), conditions))
                    {
                        sb.AppendLine($"      {line}");
                    }
                }
            }

            foreach (var line in RuntimeMessageEvidenceFormatter.FormatAlternative(msg, conditions))
            {
                sb.AppendLine($"  {line}");
            }

            if (!string.IsNullOrEmpty(msg.Icon))
            {
                sb.AppendLine($"  Icon:        {msg.Icon}");
            }

            sb.AppendLine();
        }
    }

    internal static string GenerateMessagesReport(List<MessageRecord> messages,
        FormIdResolver? resolver = null, ConditionDisplayContext? conditions = null)
    {
        var sb = new StringBuilder();
        AppendMessagesSection(sb, messages, resolver ?? conditions?.Resolver ?? FormIdResolver.Empty, conditions);
        return sb.ToString();
    }
}
