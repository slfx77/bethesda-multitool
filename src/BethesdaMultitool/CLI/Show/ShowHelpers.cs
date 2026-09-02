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
    /// <summary>Number of leading bytes shown for a raw embedded-struct payload.</summary>
    private const int BytePreviewLength = 16;

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
    ///     Append PDB-derived struct fields to the display lines, grouped by owner class.
    /// </summary>
    internal static void AppendPdbFields(List<string> lines, IReadOnlyDictionary<string, object?> fields,
        FormIdResolver resolver)
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
                // which is indistinguishable from a genuine link to the null FormID.
                var textureSet = swap.TextureSetFormId != 0
                    ? resolver.FormatWithEditorId(swap.TextureSetFormId)
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
                    detail += $", explosion {resolver.FormatWithEditorId(stage.ExplosionFormId)}";
                }

                if (stage.DebrisFormId != 0)
                {
                    detail += $", debris {resolver.FormatWithEditorId(stage.DebrisFormId)} ×{stage.DebrisCount}";
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
                // Likely a FormID — try to resolve with EditorID
                resolver.FormatWithEditorId(u),
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
