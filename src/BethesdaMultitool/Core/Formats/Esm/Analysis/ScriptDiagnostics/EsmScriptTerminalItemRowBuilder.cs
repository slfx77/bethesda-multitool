using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.Games;
using static BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics.EsmScriptDiagnosticsResolvers;
using EsmStringUtils = BethesdaMultitool.Core.Utils.EsmStringUtils;

namespace BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics;

/// <summary>
///     Builds <see cref="EsmScriptTerminalItemRow" />s: one per menu item of a target-related TERM record, read
///     straight from the record's subrecords so a diagnostics run shows what the plugin stores.
///     <para>
///         Menu items are delimited the way <c>TextRecordHandler.ParseTerminalFromAccessor</c> delimits them: an
///         ITXT opens an item, and the next ITXT or a NEXT closes it. Within an item the last RNAM/ANAM/INAM/TNAM
///         wins (as in the parser), FormIDs are read in the subrecord's container byte order
///         (<see cref="ParsedSubrecord.DataAsFormId" />), and CIS1/CIS2 siblings are bound to the CTDA they follow.
///     </para>
///     <para>
///         Conditions are described by the shared <see cref="ConditionDescriber" /> and printed with
///         <see cref="ConditionTextFormatter.FormatLine(ConditionDescription)" />, against a <see cref="ConditionDisplayContext" /> built
///         from the diagnostics' own FormID index. Only a CTDA whose width is valid for the game is interpreted
///         (the rule <c>target_conditions.csv</c> follows); any other is listed raw. The connectors printed on each
///         line are the stored OR flags; how they group is not asserted here.
///     </para>
/// </summary>
internal static class EsmScriptTerminalItemRowBuilder
{
    /// <summary>
    ///     FO3/FNV menu-item ANAM flag bits, as the xEdit schema names them (FalloutNvSchema.g.cs and
    ///     Fallout3Schema.g.cs, TERM Menu Item "Flags": bit 0 Add Note, bit 1 Force Redraw).
    /// </summary>
    private static readonly FlagBit[] MenuItemFlags =
    [
        new(0x01, "Add Note"),
        new(0x02, "Force Redraw")
    ];

    /// <summary>
    ///     Locates the menu items of a TERM record's subrecords. <see cref="MenuItemSpan.Start" /> is the item's
    ///     ITXT and <see cref="MenuItemSpan.End" /> the next ITXT or NEXT (exclusive), or the subrecord count.
    /// </summary>
    public static IReadOnlyList<MenuItemSpan> LocateMenuItems(List<ParsedSubrecord> subrecords)
    {
        var items = new List<MenuItemSpan>();
        for (var i = 0; i < subrecords.Count; i++)
        {
            if (subrecords[i].Signature != "ITXT")
            {
                continue;
            }

            var end = i + 1;
            while (end < subrecords.Count && subrecords[end].Signature is not ("ITXT" or "NEXT"))
            {
                end++;
            }

            items.Add(new MenuItemSpan(
                items.Count + 1,
                i,
                end,
                EsmStringUtils.ReadNullTermString(subrecords[i].Data)));
        }

        return items;
    }

    /// <summary>Returns the menu item whose span contains <paramref name="subrecordIndex" />, or null.</summary>
    public static MenuItemSpan? FindOwner(IReadOnlyList<MenuItemSpan> items, int subrecordIndex)
    {
        foreach (var item in items)
        {
            if (subrecordIndex > item.Start && subrecordIndex < item.End)
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>
    ///     Builds one row per menu item for every TERM row in <paramref name="recordRows" />, ordered by target,
    ///     FormID and item index. A TERM related to several targets yields its items once per target.
    /// </summary>
    public static List<EsmScriptTerminalItemRow> BuildRows(
        IReadOnlyList<EsmScriptDiagnosticRecordRow> recordRows,
        IReadOnlyDictionary<uint, ParsedMainRecord> byFormId,
        IReadOnlyDictionary<uint, EsmScriptFormIdInfo> index,
        BethesdaGame game)
    {
        var rows = new List<EsmScriptTerminalItemRow>();
        var itemsByFormId = new Dictionary<uint, List<EsmScriptTerminalItemRow>>();
        ConditionDisplayContext? context = null;

        foreach (var recordRow in recordRows)
        {
            if (recordRow.RecordType != "TERM" ||
                !byFormId.TryGetValue(recordRow.FormId, out var record) ||
                record.Header.Signature != "TERM")
            {
                continue;
            }

            if (!itemsByFormId.TryGetValue(recordRow.FormId, out var items))
            {
                context ??= ConditionDisplayContext.ForResolver(BuildResolver(index), game);
                items = DescribeMenuItems(record, index, game, context);
                itemsByFormId[recordRow.FormId] = items;
            }

            foreach (var item in items)
            {
                rows.Add(item with
                {
                    Target = recordRow.Target,
                    Relation = recordRow.Relation,
                    EditorId = recordRow.EditorId
                });
            }
        }

        return rows
            .OrderBy(r => r.Target, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.FormId)
            .ThenBy(r => r.ItemIndex)
            .ToList();
    }

    /// <summary>
    ///     Names a menu item's ANAM flags for Fallout 3 and New Vegas: <c>None</c> for zero, the known bit names,
    ///     and any remaining bits as <c>unknown bits 0xNN</c>. Empty when ANAM is absent or the game stores
    ///     something else there.
    /// </summary>
    internal static string FormatMenuItemFlags(byte? flags, BethesdaGame game)
    {
        if (flags is not { } value || game is not (BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas))
        {
            return string.Empty;
        }

        if (value == 0)
        {
            return "None";
        }

        var names = new List<string>(MenuItemFlags.Length + 1);
        uint known = 0;
        foreach (var flag in MenuItemFlags)
        {
            known |= flag.Mask;
            if ((value & flag.Mask) != 0)
            {
                names.Add(flag.Name);
            }
        }

        var unknown = value & ~known;
        if (unknown != 0)
        {
            names.Add($"unknown bits 0x{unknown:X2}");
        }

        return string.Join(", ", names);
    }

    /// <summary>
    ///     A resolver over the diagnostics index: EditorIDs and full names of every record in the file. The index
    ///     holds only this file's records, so a FormID owned by a master prints as <c>0x... (no EditorID)</c>.
    /// </summary>
    internal static FormIdResolver BuildResolver(IReadOnlyDictionary<uint, EsmScriptFormIdInfo> index)
    {
        var editorIds = new Dictionary<uint, string>();
        var displayNames = new Dictionary<uint, string>();
        foreach (var (formId, info) in index)
        {
            if (!string.IsNullOrWhiteSpace(info.EditorId))
            {
                editorIds[formId] = info.EditorId;
            }

            if (!string.IsNullOrWhiteSpace(info.FullName))
            {
                displayNames[formId] = info.FullName;
            }
        }

        return new FormIdResolver(editorIds, displayNames, new Dictionary<uint, uint>());
    }

    private static List<EsmScriptTerminalItemRow> DescribeMenuItems(
        ParsedMainRecord record,
        IReadOnlyDictionary<uint, EsmScriptFormIdInfo> index,
        BethesdaGame game,
        ConditionDisplayContext context)
    {
        var subs = record.Subrecords;
        var spans = LocateMenuItems(subs);
        var items = new List<EsmScriptTerminalItemRow>(spans.Count);
        if (spans.Count == 0)
        {
            return items;
        }

        var blocks = EsmScriptBlockReader.LocateScriptBlocks(subs);

        // Record-level CTDA ordinals, counted over every CTDA as target_conditions.csv counts them.
        var ctdaOrdinals = new int[subs.Count];
        var ordinal = 0;
        for (var i = 0; i < subs.Count; i++)
        {
            if (subs[i].Signature == "CTDA")
            {
                ctdaOrdinals[i] = ++ordinal;
            }
        }

        foreach (var span in spans)
        {
            string? resultText = null;
            byte? flags = null;
            uint? displayNote = null;
            uint? subTerminal = null;
            int? sourceTextLength = null;
            var decoded = new List<DialogueCondition>();
            var entries = new List<CtdaEntry>();
            var conditionStrings = new ConditionStringSiblingBinder();

            for (var i = span.Start + 1; i < span.End; i++)
            {
                var sub = subs[i];
                if (conditionStrings.TryConsume(sub.Signature, sub.Data))
                {
                    continue;
                }

                switch (sub.Signature)
                {
                    case "RNAM":
                        resultText = EsmStringUtils.ReadNullTermString(sub.Data);
                        break;
                    case "ANAM" when sub.Data.Length >= 1:
                        flags = sub.Data[0];
                        break;
                    case "INAM" when sub.Data.Length == 4:
                        displayNote = sub.DataAsFormId;
                        break;
                    case "TNAM" when sub.Data.Length == 4:
                        subTerminal = sub.DataAsFormId;
                        break;
                    case "SCTX":
                        // The same text the block row carries as source_text (its first SCTX).
                        sourceTextLength ??= sub.DataAsString.Length;
                        break;
                    case "CTDA":
                        if (CtdaParser.TryDecode(sub.Data, sub.BigEndian, out var condition, out _))
                        {
                            decoded.Add(condition);
                            conditionStrings.Begin(decoded);
                            entries.Add(new CtdaEntry(ctdaOrdinals[i], decoded.Count - 1, sub.Data));
                        }
                        else
                        {
                            entries.Add(new CtdaEntry(ctdaOrdinals[i], -1, sub.Data));
                        }

                        break;
                }
            }

            items.Add(new EsmScriptTerminalItemRow(
                string.Empty,
                string.Empty,
                record.Header.FormId,
                string.Empty,
                span.Index,
                span.Text,
                resultText,
                flags,
                FormatMenuItemFlags(flags, game),
                displayNote,
                displayNote is { } note ? ResolveLabel(index, note) : string.Empty,
                subTerminal,
                subTerminal is { } child ? ResolveLabel(index, child) : string.Empty,
                entries.Select(e => e.Ordinal).ToArray(),
                DescribeConditions(entries, decoded, game, context),
                blocks
                    .Where(b => b.StartIndex > span.Start && b.StartIndex < span.End)
                    .Select(b => b.BlockIndex)
                    .ToArray(),
                sourceTextLength));
        }

        return items;
    }

    /// <summary>
    ///     One line per CTDA, in order. Interpretable conditions go through the describer together, so each
    ///     line carries its stored connector; a CTDA that did not decode, or whose width is not valid for the
    ///     game, is listed raw with its layout status and is left out of that connector chain.
    /// </summary>
    private static string[] DescribeConditions(
        List<CtdaEntry> entries,
        List<DialogueCondition> decoded,
        BethesdaGame game,
        ConditionDisplayContext context)
    {
        var interpretable = new List<DialogueCondition>(entries.Count);
        var slots = new int[entries.Count];
        for (var e = 0; e < entries.Count; e++)
        {
            var entry = entries[e];
            if (entry.DecodedIndex >= 0 &&
                CtdaParser.GetLayoutStatus(game, entry.Data.Length) == "valid")
            {
                slots[e] = interpretable.Count;
                interpretable.Add(decoded[entry.DecodedIndex]);
            }
            else
            {
                slots[e] = -1;
            }
        }

        var descriptions = ConditionDescriber.DescribeAll(interpretable, context);
        var lines = new string[entries.Count];
        for (var e = 0; e < entries.Count; e++)
        {
            if (slots[e] >= 0)
            {
                lines[e] = ConditionTextFormatter.FormatLine(descriptions[slots[e]]);
                continue;
            }

            var entry = entries[e];
            var status = CtdaParser.GetLayoutStatus(game, entry.Data.Length);
            var hex = Convert.ToHexString(entry.Data);
            lines[e] = entry.DecodedIndex < 0
                ? $"Invalid CTDA ({status}): {hex}"
                : $"CTDA not interpreted ({status}): {hex}";
        }

        return lines;
    }

    /// <summary>
    ///     A TERM menu item's subrecord span: its 1-based on-disk <c>Index</c>, the subrecord index of its ITXT
    ///     (<c>Start</c>), the exclusive <c>End</c>, and its ITXT <c>Text</c>.
    /// </summary>
    internal sealed record MenuItemSpan(int Index, int Start, int End, string Text);

    /// <summary>One CTDA of a menu item: its record-level ordinal, its slot in the decoded list (-1 if undecodable), and its bytes.</summary>
    private readonly record struct CtdaEntry(int Ordinal, int DecodedIndex, byte[] Data);
}
