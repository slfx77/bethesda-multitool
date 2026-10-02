using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;

namespace BethesdaMultitool.Core.Formats.Esm.Presentation;

/// <summary>
///     Builds the TERM (terminal) detail model the CLI <c>show</c> command renders: the record's identity, DNAM
///     difficulty/flags/server type and links, its header text, the terminals whose menu items open it
///     ("Linked From"), and one section per menu item.
///     <para>
///         Every menu item comes from <see cref="TerminalMenuItemDescriber" />, the same description
///         terminal_report.txt and terminals.csv print, so show and the reports cannot disagree: the item's
///         stable 1-based index and text, RNAM result text, ANAM flags, display note, sub-menu, conditions (the
///         shared condition section, so a terminal condition reads exactly like an INFO or PACK one) and its
///         embedded result script with provenance-labeled source and the separately labeled
///         BethesdaMultitool decompilation. Text is never truncated here.
///     </para>
///     <para>
///         Reached only through <see cref="RecordDetailPresenter.TryBuildForLookup" /> (the CLI); the GUI does not
///         use this model.
///     </para>
/// </summary>
internal static class TerminalRecordDetailBuilder
{
    /// <summary>Builds the model.</summary>
    /// <param name="terminal">The TERM record.</param>
    /// <param name="records">The collection it came from (for the incoming sub-menu links), or null.</param>
    /// <param name="resolver">EditorID/display-name source.</param>
    /// <param name="conditions">Condition context: the game and quest-variable names.</param>
    /// <param name="isMemoryDumpInput">
    ///     True when the input is a memory dump: script text is then never labeled as plugin SCTX, and a missing
    ///     script is described as absent from the capture, not from the build.
    /// </param>
    internal static RecordDetailModel Build(
        TerminalRecord terminal,
        RecordCollection? records,
        FormIdResolver resolver,
        ConditionDisplayContext conditions,
        bool isMemoryDumpInput = false)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(conditions);

        var sections = new List<RecordDetailSection>
        {
            BuildIdentity(terminal, resolver),
            BuildHeader(terminal),
            BuildLinkedFrom(terminal, records)
        };

        foreach (var item in TerminalMenuItemDescriber.Describe(terminal, conditions, isMemoryDumpInput))
        {
            sections.Add(BuildMenuItem(item, conditions));
        }

        return RecordDetailHelpers.Model("TERM", terminal.FormId, terminal.EditorId, terminal.FullName, sections);
    }

    /// <summary>The section title for a menu item: <c>Menu Item [1] Yes</c>.</summary>
    internal static string MenuItemTitle(TerminalMenuItemDescriber.ItemDescription item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var text = item.Text is null ? "(no text)" : item.Text.ReplaceLineEndings(" ");
        return $"Menu Item [{item.Index.ToString(CultureInfo.InvariantCulture)}] {text}";
    }

    private static RecordDetailSection BuildIdentity(TerminalRecord terminal, FormIdResolver resolver)
    {
        var entries = new List<RecordDetailEntry>
        {
            RecordDetailHelpers.Scalar("Form ID", $"0x{terminal.FormId:X8}"),
            RecordDetailHelpers.Scalar("Editor ID", terminal.EditorId ?? "(none)"),
            RecordDetailHelpers.Scalar("Name", terminal.FullName ?? "(none)"),
            RecordDetailHelpers.Scalar("Difficulty", TerminalMenuItemDescriber.FormatDifficulty(terminal)),
            RecordDetailHelpers.Scalar("Flags", TerminalMenuItemDescriber.FormatTerminalFlags(terminal.Flags)),
            RecordDetailHelpers.Scalar("Server Type", TerminalMenuItemDescriber.FormatServerType(terminal.ServerType))
        };
        AddFormLink(entries, "Script", terminal.ScriptFormId, resolver);
        AddFormLink(entries, "Password Note", terminal.PasswordNoteFormId, resolver);
        AddFormLink(entries, "Sound Loop", terminal.SoundLoopFormId, resolver);
        entries.Add(RecordDetailHelpers.Scalar("Model", terminal.ModelPath));
        entries.Add(RecordDetailHelpers.Scalar("Item Count",
            terminal.MenuItems.Count.ToString(CultureInfo.InvariantCulture)));
        entries.Add(RecordDetailHelpers.Scalar("Container Byte Order",
            terminal.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)"));
        entries.Add(RecordDetailHelpers.Scalar("Offset", $"0x{terminal.Offset:X8}"));
        return RecordDetailHelpers.Section("Identity", entries);
    }

    private static RecordDetailSection BuildHeader(TerminalRecord terminal)
    {
        return RecordDetailHelpers.Section("Header",
        [
            new RecordDetailEntry
            {
                Kind = RecordDetailEntryKind.TextBlock,
                Label = "Header Text",
                Value = terminal.HeaderText
            }
        ]);
    }

    private static RecordDetailSection BuildLinkedFrom(TerminalRecord terminal, RecordCollection? records)
    {
        if (records is null ||
            !TerminalMenuItemDescriber.BuildIncomingLinks(records.Terminals)
                .TryGetValue(terminal.FormId, out var links))
        {
            return RecordDetailHelpers.ListSection("Linked From", []);
        }

        return RecordDetailHelpers.ListSection("Linked From", links
            .Select(link => new RecordDetailListItem
            {
                Label = ConditionTextFormatter.FormatFormId(link.ParentFormId, link.ParentEditorId),
                Value = $"item [{link.ItemIndex.ToString(CultureInfo.InvariantCulture)}] " +
                        (link.ItemText is null ? "(no text)" : $"\"{link.ItemText}\""),
                LinkedFormId = link.ParentFormId
            })
            .ToList());
    }

    private static RecordDetailSection BuildMenuItem(
        TerminalMenuItemDescriber.ItemDescription item,
        ConditionDisplayContext conditions)
    {
        var entries = new List<RecordDetailEntry>
        {
            new()
            {
                Kind = RecordDetailEntryKind.TextBlock,
                Label = "Item Text",
                Value = item.Text ?? "(no text)"
            },
            new()
            {
                Kind = RecordDetailEntryKind.TextBlock,
                Label = "Result Text",
                Value = item.ResultText ?? "(no RNAM)"
            },
            RecordDetailHelpers.Scalar("Flags", item.FlagsDisplay)
        };

        AddLink(entries, "Display Note", item.DisplayNoteFormId, item.DisplayNoteLabel);
        AddLink(entries, "Sub-menu", item.SubTerminalFormId, item.SubTerminalLabel);

        if (item.Conditions.Count > 0)
        {
            // The shared condition section (FormatLine items, GECK-convention grouping, assumed-game note),
            // folded into this item's section so each menu item stays one section.
            entries.AddRange(RecordDetailBuilders.BuildConditionsSection(item.Item.Conditions, conditions).Entries);
        }

        AppendScript(entries, item.Script);
        foreach (var binding in item.Item.ExternalVariableBindings)
            entries.Add(RecordDetailHelpers.Scalar("Result Script External Variable", binding.Summary));
        return RecordDetailHelpers.Section(MenuItemTitle(item), entries);
    }

    private static void AppendScript(List<RecordDetailEntry> entries, TerminalMenuItemDescriber.ScriptDescription script)
    {
        const string SlotLabel = "Result Script";
        entries.Add(RecordDetailHelpers.Scalar(SlotLabel, script.Summary));

        var classification = script.Classification;
        var empty = classification.Kind == ScriptTextKind.None && script.DecompiledText is null && script.CompiledSize == 0 &&
                    script.Variables.Count == 0 && script.References.Count == 0 && script.BundleNote is null;
        if (empty)
        {
            return;
        }

        entries.Add(RecordDetailHelpers.Scalar($"{SlotLabel} Provenance",
            classification.CorrespondenceToken == ScriptSourceProvenance.CorrespondenceNotApplicable
                ? classification.Token
                : $"{classification.Token} (correspondence: {classification.CorrespondenceToken})"));

        if (classification.HasSourceText && script.SourceText is { } source)
        {
            entries.Add(new RecordDetailEntry
            {
                Kind = RecordDetailEntryKind.CodeBlock,
                Label = $"{SlotLabel}: {classification.Label}",
                Value = source
            });
        }
        else
        {
            entries.Add(RecordDetailHelpers.Scalar($"{SlotLabel} Source", classification.Label));
        }

        // Prefer the reconstructed SCTX, which can also carry local declarations, to a
        // duplicate raw decompilation under the same heading.
        if (script.DecompiledText is { } decompiled &&
            !(classification.HasSourceText && classification.Kind == ScriptTextKind.ReconstructedDecompiled))
        {
            entries.Add(new RecordDetailEntry
            {
                Kind = RecordDetailEntryKind.CodeBlock,
                Label = $"{SlotLabel}: {ScriptSourceProvenance.DecompiledTextLabel}",
                Value = decompiled
            });
        }

        entries.Add(RecordDetailHelpers.Scalar($"{SlotLabel} Compiled Size",
            script.CompiledSize > 0
                ? $"{script.CompiledSize.ToString(CultureInfo.InvariantCulture)} bytes"
                : "0 bytes (no SCDA)"));
        entries.Add(RecordDetailHelpers.Scalar($"{SlotLabel} Bytecode Order", script.BytecodeOrder));

        if (script.Variables.Count > 0)
        {
            entries.Add(ListEntry($"{SlotLabel} Variables", script.Variables
                .Select(variable => new RecordDetailListItem
                {
                    Label = $"[{variable.Index.ToString(CultureInfo.InvariantCulture)}]",
                    Value = ScriptVariableTypeResolver.FormatDeclaration(variable,
                        script.References.Select(reference => reference.Raw))
                })
                .ToList()));
        }

        if (script.References.Count > 0)
        {
            // Numbered from 1: the bytecode addresses the mixed SCRO/SCRV table by 1-based slot.
            entries.Add(ListEntry($"{SlotLabel} References", script.References
                .Select(reference => new RecordDetailListItem
                {
                    Label = reference.Slot.ToString(CultureInfo.InvariantCulture),
                    Value = reference.Display,
                    LinkedFormId = reference.FormId
                })
                .ToList()));
        }

        entries.Add(RecordDetailHelpers.Scalar($"{SlotLabel} Bundle", script.BundleNote));
    }

    private static void AddFormLink(List<RecordDetailEntry> entries, string label, uint? formId, FormIdResolver resolver)
    {
        if (formId is { } id && id != 0)
        {
            AddLink(entries, label, id, TerminalMenuItemDescriber.FormatFormId(id, resolver));
        }
    }

    private static void AddLink(List<RecordDetailEntry> entries, string label, uint? formId, string? display)
    {
        if (formId is not { } id || display is null)
        {
            return;
        }

        entries.Add(new RecordDetailEntry
        {
            Kind = RecordDetailEntryKind.Link,
            Label = label,
            Value = display,
            LinkedFormId = id
        });
    }

    private static RecordDetailEntry ListEntry(string label, List<RecordDetailListItem> items)
    {
        return new RecordDetailEntry
        {
            Kind = RecordDetailEntryKind.List,
            Label = label,
            Items = items,
            ExpandByDefault = items.Count <= 8
        };
    }
}
