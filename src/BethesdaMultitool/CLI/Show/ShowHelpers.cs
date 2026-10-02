using System.Globalization;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Show;

/// <summary>
///     Shared helper methods used by all show renderers.
/// </summary>
internal static class ShowHelpers
{
    /// <summary>
    ///     Longest body (script source, decompiled text, message or book text) a default-mode panel
    ///     prints before cutting it with <see cref="TruncateForPanel" />. <c>show --full</c> lifts it.
    /// </summary>
    internal const int DefaultTextCap = 2000;

    /// <summary>The option that lifts <see cref="DefaultTextCap" />, named in the truncation marker.</summary>
    internal const string FullTextOptionName = "--full";

    /// <summary>Number of leading bytes shown for a raw embedded-struct payload.</summary>
    private const int BytePreviewLength = 16;

    /// <summary>Prefix of the line that opens a verbatim block.</summary>
    private const string VerbatimBeginPrefix = "----- BEGIN ";

    /// <summary>Prefix of the line that closes a verbatim block.</summary>
    private const string VerbatimEndPrefix = "----- END ";

    /// <summary>Suffix of both delimiter lines.</summary>
    private const string VerbatimDelimiterSuffix = " -----";

    /// <summary>
    ///     Cut <paramref name="text" /> to <paramref name="cap" /> characters for a panel, ending with a
    ///     marker that says how much was shown out of how much, and how to see the rest. Text within
    ///     the cap is returned unchanged. The result is plain text: callers escape it.
    ///     <para>
    ///         Counts are UTF-16 characters, formatted with the invariant culture so the marker reads the
    ///         same on every machine. The cut never splits a surrogate pair.
    ///     </para>
    /// </summary>
    internal static string TruncateForPanel(string text, int cap = DefaultTextCap)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cap);
        if (text.Length <= cap)
        {
            return text;
        }

        var shown = char.IsHighSurrogate(text[cap - 1]) ? cap - 1 : cap;
        return text[..shown] +
               $"\n... (truncated: {FormatCount(shown)} of {FormatCount(text.Length)} characters shown; " +
               $"rerun with {FullTextOptionName})";
    }

    /// <summary>
    ///     True when a value belongs in a verbatim block under <c>--full</c>: it spans lines, or it is
    ///     longer than <see cref="DefaultTextCap" />. A panel folds every line at the console width and
    ///     measures a TAB as one cell, so only a raw block keeps such text exactly as stored.
    /// </summary>
    internal static bool NeedsVerbatimBlock(string? value)
    {
        return value is not null &&
               (value.Length > DefaultTextCap || value.Contains('\n') || value.Contains('\r'));
    }

    /// <summary>
    ///     The in-panel stand-in for a body that is written as a verbatim block after the panel. Plain
    ///     text with no brackets, so it is markup-safe as it stands.
    /// </summary>
    internal static string VerbatimPlaceholder(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return $"({FormatCount(text.Length)} chars; printed verbatim below)";
    }

    /// <summary>
    ///     Write each block after whatever the console has already rendered, bypassing Spectre's
    ///     layout entirely: the text goes to the console's own output writer
    ///     (<c>console.Profile.Out.Writer</c>) exactly as stored, so TABs, CRLF line breaks and lines
    ///     longer than the console width survive. <c>console.WriteLine(string)</c> and a
    ///     <see cref="Panel" /> both fold at <c>Profile.Width</c> (80 when output is redirected, even
    ///     under <c>--plain</c>), so no Spectre renderable can print a long body unwrapped.
    ///     <para>
    ///         Nothing is parsed as markup, so neither the titles nor the text are escaped. Each block
    ///         reads <c>----- BEGIN title (N chars, M lines) -----</c>, the text, a line break when the
    ///         text lacks a final one, and <c>----- END title -----</c>.
    ///     </para>
    ///     <para>
    ///         Safe only while no Spectre live display (progress, status) is running: <c>show</c> renders
    ///         after its progress runner has finished.
    ///     </para>
    /// </summary>
    internal static void WriteVerbatimBlocks(IAnsiConsole console, IReadOnlyList<VerbatimBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(blocks);
        if (blocks.Count == 0)
        {
            return;
        }

        var writer = console.Profile.Out.Writer;
        foreach (var block in blocks)
        {
            writer.Write(FormatVerbatimBlock(block));
        }

        writer.Flush();
    }

    /// <summary>One block as <see cref="WriteVerbatimBlocks" /> writes it.</summary>
    internal static string FormatVerbatimBlock(VerbatimBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        var title = SingleLineTitle(block.Title);
        var text = block.Text;
        var newline = Environment.NewLine;

        var sb = new StringBuilder(text.Length + title.Length * 2 + 96);
        sb.Append(VerbatimBeginPrefix).Append(title)
            .Append(" (").Append(FormatCount(text.Length)).Append(" chars, ")
            .Append(FormatCount(CountLines(text))).Append(" lines)")
            .Append(VerbatimDelimiterSuffix).Append(newline);
        sb.Append(text);
        if (text.Length > 0 && text[^1] != '\n')
        {
            sb.Append(newline);
        }

        sb.Append(VerbatimEndPrefix).Append(title).Append(VerbatimDelimiterSuffix).Append(newline);
        return sb.ToString();
    }

    /// <summary>
    ///     Lines in <paramref name="text" />: its line feeds, plus one for a final line that has none.
    ///     Empty text has no lines.
    /// </summary>
    internal static int CountLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return 0;
        }

        var count = 0;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                count++;
            }
        }

        return text[^1] == '\n' ? count : count + 1;
    }

    /// <summary>A count with thousands separators, identical on every machine.</summary>
    internal static string FormatCount(int value)
    {
        return value.ToString("N0", CultureInfo.InvariantCulture);
    }

    /// <summary>A delimiter title must stay on its own line: fold any line break in it to a space.</summary>
    private static string SingleLineTitle(string title)
    {
        return title.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');
    }

    internal static bool Matches<T>(T record, uint? formId, string? editorId,
        Func<T, uint> getFormId, Func<T, string?> getEditorId)
    {
        if (formId.HasValue && getFormId(record) == formId.Value)
        {
            return true;
        }

        if (editorId != null)
        {
            var eid = getEditorId(record);
            return eid != null && eid.Equals(editorId, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    ///     A resolver-formatted FormID ("EditorID (0x…)") escaped for Spectre markup.
    ///     <para>
    ///         The EditorID half is data — an ESM EDID, a runtime string from a dump, or a
    ///         synthesized cell ID such as <c>[Virtual 3,4 WastelandNV]</c> — so interpolating
    ///         <see cref="FormIdResolver.FormatWithEditorId" /> raw lets the text parse as markup
    ///         and throw at display time. Call sites that already escape the resolver output later
    ///         must keep calling FormatWithEditorId, or the brackets would print doubled.
    ///     </para>
    /// </summary>
    internal static string Ref(FormIdResolver resolver, uint formId)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        return Markup.Escape(resolver.FormatWithEditorId(formId));
    }

    /// <summary>
    ///     Data text escaped for Spectre markup, or <paramref name="fallback" /> (escaped too) when
    ///     the value is null — for names that come from records, such as AVIF actor-value names.
    /// </summary>
    internal static string Plain(string? value, string fallback = "(none)")
    {
        return Markup.Escape(value ?? fallback);
    }

    /// <summary>
    ///     Append PDB-derived struct fields to the display lines, grouped by owner class.
    /// </summary>
    internal static void AppendPdbFields(List<string> lines, IReadOnlyDictionary<string, object?> fields,
        FormIdResolver resolver, bool fullText = false, List<VerbatimBlock>? verbatimBlocks = null)
    {
        // Group fields by owner class (key format is "OwnerClass.FieldName")
        var grouped = new Dictionary<string, List<(string FieldName, object? Value)>>();
        foreach (var (key, value) in fields)
        {
            var dotIndex = key.IndexOf('.');
            string owner;
            string fieldName;
            if (dotIndex >= 0)
            {
                owner = key[..dotIndex];
                fieldName = key[(dotIndex + 1)..];
            }
            else
            {
                owner = "(unknown)";
                fieldName = key;
            }

            if (!grouped.TryGetValue(owner, out var list))
            {
                list = [];
                grouped[owner] = list;
            }

            list.Add((fieldName, value));
        }

        foreach (var (owner, fieldList) in grouped)
        {
            lines.Add($"[bold]{Markup.Escape(owner)}:[/]");
            foreach (var (fieldName, value) in fieldList)
            {
                if (fullText && verbatimBlocks != null && value is string text && NeedsVerbatimBlock(text))
                {
                    verbatimBlocks.Add(new VerbatimBlock($"{owner}.{fieldName}", text));
                    lines.Add($"  [grey]{Markup.Escape(fieldName)}:[/] {VerbatimPlaceholder(text)}");
                    continue;
                }

                var formatted = FormatPdbFieldValue(value, resolver);
                lines.Add($"  [grey]{Markup.Escape(fieldName)}:[/] {formatted}");
            }
        }
    }

    /// <summary>
    ///     Append the nested payloads a base record carries — MODS alternate textures, the DEST
    ///     destruction block, MODT texture hashes — from the collection's side-indexes.
    ///     <para>
    ///         These live beside the records rather than on them because they hang off engine base
    ///         classes shared by dozens of record types, so a single appender lets every renderer
    ///         show them without each typed model growing three properties it rarely uses.
    ///         Sections are emitted only when populated; a record with none adds nothing.
    ///     </para>
    /// </summary>
    internal static void AppendNestedPayloads(
        List<string> lines, RecordCollection records, uint formId, FormIdResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(records);

        if (records.AlternateTexturesByFormId.TryGetValue(formId, out var swaps) && swaps.Count > 0)
        {
            lines.Add($"[bold]Alternate Textures[/] ({swaps.Count}):");
            foreach (var swap in swaps)
            {
                // The browse path keeps an entry whose TXST pointer did not resolve, because the
                // shape name and 3D index are still real. Saying so beats printing 0x00000000,
                // which is indistinguishable from a genuine link to the null FormID. Only the
                // resolver branch is data; the grey placeholder is our own markup.
                var textureSet = swap.TextureSetFormId != 0
                    ? Ref(resolver, swap.TextureSetFormId)
                    : "[grey](texture set unresolved)[/]";

                lines.Add(
                    $"  [grey]{Markup.Escape(swap.ShapeName)}[/] → " +
                    $"{textureSet}  [grey](3D index {swap.Index})[/]");
            }
        }

        if (records.DestructionByFormId.TryGetValue(formId, out var destruction))
        {
            lines.Add(
                $"[bold]Destruction:[/] health {destruction.Health}, flags 0x{destruction.Flags:X2}, " +
                $"{destruction.Stages.Count} stage(s)");
            for (var i = 0; i < destruction.Stages.Count; i++)
            {
                var stage = destruction.Stages[i];
                var detail = $"  [grey]stage {i}:[/] {stage.HealthPercent}% health, model stage {stage.DamageStage}";
                if (stage.SelfDamagePerSecond != 0)
                {
                    detail += $", {stage.SelfDamagePerSecond} dmg/s";
                }

                if (stage.ExplosionFormId != 0)
                {
                    detail += $", explosion {Ref(resolver, stage.ExplosionFormId)}";
                }

                if (stage.DebrisFormId != 0)
                {
                    detail += $", debris {Ref(resolver, stage.DebrisFormId)} ×{stage.DebrisCount}";
                }

                lines.Add(detail);
                if (!string.IsNullOrEmpty(stage.ReplacementModel))
                {
                    lines.Add($"    [grey]model:[/] {Markup.Escape(stage.ReplacementModel)}");
                }
            }
        }

        // Hashes of the source build's texture paths — a count and an identity, not portable data.
        if (records.TextureHashesByFormId.TryGetValue(formId, out var hashes) && hashes.DeclaredCount > 0)
        {
            // Slot order is the meaning here — hash i is "the texture in slot i" — so an uncaptured
            // slot is shown in place rather than closed up, which would re-attribute every hash
            // after it.
            var header = hashes.IsComplete
                ? $"({hashes.DeclaredCount})"
                : $"({hashes.CapturedCount} of {hashes.DeclaredCount} captured)";
            // Captured slots are escaped: they are hex today, but this line renders inside Spectre
            // markup, and the sibling path below escapes — an unescaped '[' here would throw at
            // display time.
            var rendered = hashes.Slots.Select(slot => slot is null ? "[grey]--[/]" : Markup.Escape(slot));

            lines.Add($"[bold]Texture Hashes[/] {header}: [grey]{string.Join(" ", rendered)}[/]");
        }
    }

    /// <summary>
    ///     Format a PDB field value for display, resolving FormIDs where possible.
    /// </summary>
    internal static string FormatPdbFieldValue(object? value, FormIdResolver resolver)
    {
        return value switch
        {
            null => "[grey](null)[/]",
            uint u when u > 0x00010000 && u < 0x10000000 =>
                // Likely a FormID — try to resolve with EditorID. Escaped like every other arm:
                // a dump's EditorIDs are runtime strings and can carry brackets.
                Ref(resolver, u),
            uint u => $"0x{u:X8}  ({u})",
            int i => i.ToString(),
            float f => f.ToString("F4"),
            ushort us => $"{us}  (0x{us:X4})",
            short s => s.ToString(),
            byte b => $"{b}  (0x{b:X2})",
            sbyte sb => sb.ToString(),
            bool b => b.ToString(),
            string s => Markup.Escape(s),
            // RuntimeGenericReader hands back the raw bytes of any embedded struct larger than
            // 8 bytes. Without this arm the default ToString() renders them as "System.Byte[]".
            byte[] raw => FormatBytePreview(raw),
            // RuntimeContainerFieldReader walks BSSimpleList / counted-array fields into FormID or
            // string lists; without these arms they render as "System.Collections.Generic.List`1".
            IReadOnlyList<uint> formIds => string.Join(", ", formIds.Select(id => $"0x{id:X8}")),
            // Must precede the IReadOnlyList<string> arm: this carries uncaptured slots as nulls,
            // and nullable annotations are erased at runtime, so a plain join would silently render
            // every hole as an empty string.
            RuntimeTextureHashList hashList => Markup.Escape(
                string.Join(" ", hashList.Slots.Select(slot => slot ?? "--")) +
                (hashList.IsComplete
                    ? ""
                    : $"  ({hashList.CapturedCount} of {hashList.DeclaredCount} captured)")),
            IReadOnlyList<string> strings => Markup.Escape(string.Join(", ", strings)),
            _ => Markup.Escape(value.ToString() ?? "")
        };
    }

    /// <summary>
    ///     Render a raw byte payload as a short hex preview plus its full length, so a long
    ///     embedded struct stays one readable line.
    /// </summary>
    private static string FormatBytePreview(byte[] raw)
    {
        if (raw.Length == 0)
        {
            return "[grey](empty)[/]";
        }

        var shown = Math.Min(BytePreviewLength, raw.Length);
        var hex = Convert.ToHexString(raw, 0, shown);
        var ellipsis = raw.Length > shown ? "…" : "";
        return $"{hex}{ellipsis}  ({raw.Length} bytes)";
    }
}

/// <summary>
///     A body that <c>show --full</c> writes after the panel, unwrapped and unescaped (see
///     <see cref="ShowHelpers.WriteVerbatimBlocks" />).
/// </summary>
/// <param name="Title">What the text is, e.g. its provenance label; plain text, one line.</param>
/// <param name="Text">The text exactly as stored.</param>
internal sealed record VerbatimBlock(string Title, string Text);
