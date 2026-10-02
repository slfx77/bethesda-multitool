using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Export.Geck;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;

namespace BethesdaMultitool.Core.Formats.Esm.Export.Support;

/// <summary>
///     The one description of a TERM record's menu items that terminal_report.txt, terminals.csv and the CLI
///     <c>show</c> panel all print, so the three never disagree about an item.
///     <para>
///         Each item keeps its stable 1-based index (its position in the record, which is the engine's menu
///         order), its ITXT text, its RNAM result text, its ANAM flags (named from the xEdit FNV/FO3 schema via
///         <see cref="FlagRegistry.TerminalMenuItemFlags" />), its INAM display note and TNAM sub-menu, its CTDA
///         conditions (through the shared <see cref="ConditionDescriber" />, so a terminal condition prints exactly
///         as an INFO or PACK condition does) and its embedded result script.
///     </para>
///     <para>
///         Script text is labeled by <see cref="ScriptSourceProvenance" /> only: authored SCTX, SCTX recovered
///         from a memory dump and a BethesdaMultitool reconstruction each keep their own label, and decompiled
///         text is always a separate body under <see cref="ScriptSourceProvenance.DecompiledTextLabel" />. Nothing
///         here truncates text.
///     </para>
///     <para>
///         Plain text throughout (no Spectre markup); callers that write markup must escape it.
///     </para>
/// </summary>
internal static class TerminalMenuItemDescriber
{
    /// <summary>The wording for a menu item whose embedded script carries no bytecode.</summary>
    internal const string NoCompiledCode = "no compiled code";

    /// <summary>High bit the parsers set on an SCRV (local variable) entry of a reference table.</summary>
    private const uint ScrvLocalMarker = 0x80000000;

    /// <summary>
    ///     Describes every menu item of <paramref name="terminal" />, in record order, numbered from 1.
    /// </summary>
    /// <param name="terminal">The TERM record.</param>
    /// <param name="conditions">Condition context: the game, the EditorID resolver and quest-variable names.</param>
    /// <param name="isMemoryDumpInput">
    ///     True when the record came from a memory dump. Script text is then never labeled as plugin SCTX, and
    ///     a missing script is described as absent from the capture, not from the build. Each item's own
    ///     <see cref="TerminalMenuItem.IsDmpDerived" /> flag also counts.
    /// </param>
    internal static IReadOnlyList<ItemDescription> Describe(
        TerminalRecord terminal,
        ConditionDisplayContext conditions,
        bool isMemoryDumpInput)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(conditions);

        var items = new ItemDescription[terminal.MenuItems.Count];
        for (var i = 0; i < items.Length; i++)
        {
            items[i] = DescribeItem(terminal.MenuItems[i], i + 1, conditions, isMemoryDumpInput);
        }

        return items;
    }

    /// <summary>Describes one menu item at the given 1-based <paramref name="index" />.</summary>
    internal static ItemDescription DescribeItem(
        TerminalMenuItem item,
        int index,
        ConditionDisplayContext conditions,
        bool isMemoryDumpInput)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(conditions);

        var resolver = conditions.Resolver;
        var described = ConditionDescriber.DescribeAll(item.Conditions, conditions);
        var dumpInput = isMemoryDumpInput || item.IsDmpDerived;

        return new ItemDescription
        {
            Index = index,
            Item = item,
            Text = item.Text,
            ResultText = item.ResultText,
            FlagsRaw = item.ActionType,
            FlagsDisplay = FormatItemFlags(item.ActionType),
            FlagNames = item.ActionType is { } flags
                ? FlagRegistry.DecodeFlagNames(flags, FlagRegistry.TerminalMenuItemFlags)
                : null,
            DisplayNoteFormId = NonZero(item.DisplayNoteFormId),
            DisplayNoteLabel = NonZero(item.DisplayNoteFormId) is { } note ? FormatFormId(note, resolver) : null,
            SubTerminalFormId = NonZero(item.SubTerminal),
            SubTerminalEditorId = NonZero(item.SubTerminal) is { } child ? resolver.GetEditorId(child) : null,
            SubTerminalLabel = NonZero(item.SubTerminal) is { } sub ? FormatFormId(sub, resolver) : null,
            Conditions = described,
            ConditionLines = ConditionTextFormatter.FormatLines(described),
            ConditionGrouping = ConditionTextFormatter.FormatLogicSummary(described),
            Script = DescribeScript(item, resolver, dumpInput)
        };
    }

    /// <summary>
    ///     Every TNAM sub-menu edge, keyed by the TERM it opens: which terminal and which of its items (1-based)
    ///     link to it. Ordered by parent FormID, then item index, so the output is stable.
    /// </summary>
    internal static IReadOnlyDictionary<uint, IReadOnlyList<IncomingLink>> BuildIncomingLinks(
        IEnumerable<TerminalRecord> terminals)
    {
        ArgumentNullException.ThrowIfNull(terminals);

        var links = new Dictionary<uint, List<IncomingLink>>();
        foreach (var parent in terminals.OrderBy(terminal => terminal.FormId))
        {
            for (var i = 0; i < parent.MenuItems.Count; i++)
            {
                if (NonZero(parent.MenuItems[i].SubTerminal) is not { } child)
                {
                    continue;
                }

                if (!links.TryGetValue(child, out var list))
                {
                    list = [];
                    links.Add(child, list);
                }

                list.Add(new IncomingLink(parent.FormId, parent.EditorId, i + 1, parent.MenuItems[i].Text));
            }
        }

        return links.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<IncomingLink>)pair.Value);
    }

    /// <summary>
    ///     One incoming link as plain text, e.g.
    ///     <c>NVDLC01VaultMainInfoDownloadTerminal [0x0100DAD2] item [1] "Retrieve Hologram Technology Data"</c>.
    /// </summary>
    internal static string FormatIncomingLink(IncomingLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var text = link.ItemText is null ? "(no text)" : $"\"{link.ItemText}\"";
        return $"{ConditionTextFormatter.FormatFormId(link.ParentFormId, link.ParentEditorId)} item " +
               $"[{link.ItemIndex.ToString(CultureInfo.InvariantCulture)}] {text}";
    }

    /// <summary>
    ///     The ANAM flags as names plus hex, e.g. <c>Add Note, Force Redraw (0x0003)</c>; <c>None</c> for 0 and
    ///     <c>(no ANAM)</c> when the item carries no ANAM subrecord.
    /// </summary>
    internal static string FormatItemFlags(byte? actionType)
    {
        return actionType is { } flags
            ? FlagRegistry.DecodeFlagNamesWithHex(flags, FlagRegistry.TerminalMenuItemFlags)
            : "(no ANAM)";
    }

    /// <summary>The record's DNAM flags as names plus hex, e.g. <c>Unlocked (0x0002)</c>, or <c>None</c>.</summary>
    internal static string FormatTerminalFlags(byte flags)
    {
        return FlagRegistry.DecodeFlagNamesWithHex(flags, FlagRegistry.TerminalFlags);
    }

    /// <summary>
    ///     The DNAM server type as its raw value plus the xEdit FNV/FO3 enum label from the generated schema
    ///     (value N is <c>-Server N+1-</c> for 0..9), e.g. <c>8 (-Server 9-)</c>.
    /// </summary>
    internal static string FormatServerType(byte serverType)
    {
        var raw = serverType.ToString(CultureInfo.InvariantCulture);
        return serverType <= 9
            ? $"{raw} (-Server {(serverType + 1).ToString(CultureInfo.InvariantCulture)}-)"
            : $"{raw} (not named in the schema)";
    }

    /// <summary>The difficulty as its name plus raw value, e.g. <c>Requires Key (5)</c>.</summary>
    internal static string FormatDifficulty(TerminalRecord terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        return $"{terminal.DifficultyName} ({terminal.Difficulty.ToString(CultureInfo.InvariantCulture)})";
    }

    /// <summary>A FormID as <c>EDID [0xFORMID]</c>, the form every condition line also uses.</summary>
    internal static string FormatFormId(uint formId, FormIdResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        return ConditionTextFormatter.FormatFormId(formId, formId == 0 ? null : resolver.GetEditorId(formId));
    }

    private static ScriptDescription DescribeScript(TerminalMenuItem item, FormIdResolver resolver, bool dumpInput)
    {
        var classification = ScriptSourceProvenance.Classify(item, dumpInput);
        var compiledSize = item.CompiledData?.Length ?? 0;
        var references = new ReferenceDescription[item.ReferencedObjects.Count];
        for (var i = 0; i < references.Length; i++)
        {
            var raw = item.ReferencedObjects[i];
            references[i] = new ReferenceDescription(
                i + 1,
                raw,
                GeckScriptWriter.FormatScriptReference(raw, item.Variables, resolver),
                (raw & ScrvLocalMarker) == 0 && raw != 0 ? raw : null);
        }

        return new ScriptDescription
        {
            Classification = classification,
            SourceText = classification.HasSourceText ? item.SourceText : null,
            DecompiledText = string.IsNullOrEmpty(item.DecompiledText) ? null : item.DecompiledText,
            CompiledSize = compiledSize,
            BytecodeOrder = compiledSize > 0 ? (item.IsBigEndianBytecode ? "Big-Endian" : "Little-Endian") : null,
            Summary = DescribeSummary(item, compiledSize, references.Length, dumpInput),
            Variables = item.Variables.OrderBy(variable => variable.Index).ToArray(),
            References = references,
            IsIncompleteExecutableBundle = item.IsIncompleteExecutableBundle,
            BundleNote = item.IsIncompleteExecutableBundle ? DescribeIncompleteBundle(dumpInput) : null
        };
    }

    private static string DescribeSummary(TerminalMenuItem item, int compiledSize, int referenceCount, bool dumpInput)
    {
        if (compiledSize > 0)
        {
            var size = compiledSize.ToString(CultureInfo.InvariantCulture);
            return referenceCount > 0
                ? $"{size} bytes of compiled code (SCDA), {referenceCount.ToString(CultureInfo.InvariantCulture)} " +
                  $"reference{(referenceCount == 1 ? "" : "s")}"
                : $"{size} bytes of compiled code (SCDA)";
        }

        if (!string.IsNullOrEmpty(item.SourceText))
        {
            return $"{NoCompiledCode} (the menu item holds source text but no SCDA)";
        }

        if (item.WithheldSourceReason is not null)
        {
            return $"{NoCompiledCode}; captured source was withheld by validation";
        }

        if (item.Variables.Count > 0 || item.ReferencedObjects.Count > 0)
        {
            return $"{NoCompiledCode} (the menu item holds locals or references but no SCDA or source text)";
        }

        return dumpInput
            ? $"{NoCompiledCode} recovered: {ScriptSourceProvenance.PartialDumpAbsenceWording}"
            : $"{NoCompiledCode} (no SCDA or source text in this menu item)";
    }

    private static string DescribeIncompleteBundle(bool dumpInput)
    {
        const string Disagreement =
            "the SCHR/SCDA/local/reference bundle is structurally inconsistent or failed emission safety validation; " +
            "this is not a validated, runnable script";
        return dumpInput
            ? $"incomplete or unsafe in this capture: {Disagreement}"
            : $"incomplete: {Disagreement}";
    }

    private static uint? NonZero(uint? formId)
    {
        return formId is { } value && value != 0 ? value : null;
    }

    /// <summary>One described menu item. Every string is plain text.</summary>
    internal sealed record ItemDescription
    {
        /// <summary>1-based position of the item in its record (the engine's menu order).</summary>
        public required int Index { get; init; }

        /// <summary>The parsed item this description was built from.</summary>
        public required TerminalMenuItem Item { get; init; }

        /// <summary>ITXT, the menu item text.</summary>
        public string? Text { get; init; }

        /// <summary>RNAM, the result text shown after the item is chosen.</summary>
        public string? ResultText { get; init; }

        /// <summary>The raw ANAM byte, or null when the item has no ANAM.</summary>
        public byte? FlagsRaw { get; init; }

        /// <summary>The ANAM flags as names plus hex (see <see cref="FormatItemFlags" />).</summary>
        public required string FlagsDisplay { get; init; }

        /// <summary>The ANAM flag names alone (<c>None</c> for 0), or null when the item has no ANAM.</summary>
        public string? FlagNames { get; init; }

        /// <summary>INAM, the NOTE the item displays, when present.</summary>
        public uint? DisplayNoteFormId { get; init; }

        /// <summary>The display note as <c>EDID [0xFORMID]</c>.</summary>
        public string? DisplayNoteLabel { get; init; }

        /// <summary>TNAM, the TERM the item opens, when present.</summary>
        public uint? SubTerminalFormId { get; init; }

        /// <summary>The sub-menu terminal's EditorID, when the resolver knows it.</summary>
        public string? SubTerminalEditorId { get; init; }

        /// <summary>The sub-menu terminal as <c>EDID [0xFORMID]</c>.</summary>
        public string? SubTerminalLabel { get; init; }

        /// <summary>The item's CTDA conditions, in stored order.</summary>
        public required IReadOnlyList<ConditionDescription> Conditions { get; init; }

        /// <summary><see cref="ConditionTextFormatter.FormatLine(ConditionDescription)" /> for each condition, in order.</summary>
        public required IReadOnlyList<string> ConditionLines { get; init; }

        /// <summary>The GECK-convention grouping summary, or null when there is nothing to group.</summary>
        public string? ConditionGrouping { get; init; }

        /// <summary>The embedded result script.</summary>
        public required ScriptDescription Script { get; init; }
    }

    /// <summary>A menu item's embedded result script. Every string is plain text.</summary>
    internal sealed record ScriptDescription
    {
        /// <summary>How the source text is labeled (never "original" for a reconstruction).</summary>
        public required ScriptSourceClassification Classification { get; init; }

        /// <summary>The source text, in full, or null when there is none.</summary>
        public string? SourceText { get; init; }

        /// <summary>
        ///     BethesdaMultitool's decompilation of the SCDA, in full, or null. Always shown under
        ///     <see cref="ScriptSourceProvenance.DecompiledTextLabel" />, never as source.
        /// </summary>
        public string? DecompiledText { get; init; }

        /// <summary>Length of the SCDA bytecode, 0 when absent.</summary>
        public int CompiledSize { get; init; }

        /// <summary>The byte order the parser decoded the SCDA with, or null without SCDA.</summary>
        public string? BytecodeOrder { get; init; }

        /// <summary>One-line summary: the compiled size and reference count, or why there is no code.</summary>
        public required string Summary { get; init; }

        /// <summary>The SLSD/SCVR local table, ordered by index.</summary>
        public required IReadOnlyList<ScriptVariableInfo> Variables { get; init; }

        /// <summary>The SCRO/SCRV table in stored order (the bytecode addresses it by 1-based slot).</summary>
        public required IReadOnlyList<ReferenceDescription> References { get; init; }

        /// <summary>Whether the parser found the SCHR/SCDA/locals/references bundle inconsistent.</summary>
        public bool IsIncompleteExecutableBundle { get; init; }

        /// <summary>The incomplete-bundle wording, or null for a consistent bundle.</summary>
        public string? BundleNote { get; init; }
    }

    /// <summary>One entry of the script's SCRO/SCRV table.</summary>
    /// <param name="Slot">1-based slot, as the bytecode addresses it.</param>
    /// <param name="Raw">The stored value (the high bit marks an SCRV local).</param>
    /// <param name="Display">The entry as text: a form, <c>Player</c>/<c>PlayerRef</c>, or <c>local #n (name)</c>.</param>
    /// <param name="FormId">The FormID for an SCRO entry, null for an SCRV local or a null reference.</param>
    internal sealed record ReferenceDescription(int Slot, uint Raw, string Display, uint? FormId);

    /// <summary>A TNAM edge into a terminal: which terminal and which of its items open it.</summary>
    /// <param name="ParentFormId">The terminal whose item links here.</param>
    /// <param name="ParentEditorId">That terminal's EditorID, when known.</param>
    /// <param name="ItemIndex">The linking item's 1-based index.</param>
    /// <param name="ItemText">The linking item's ITXT.</param>
    internal sealed record IncomingLink(uint ParentFormId, string? ParentEditorId, int ItemIndex, string? ItemText);
}
