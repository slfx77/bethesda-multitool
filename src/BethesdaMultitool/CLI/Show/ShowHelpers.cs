using BethesdaMultitool.Core.Formats.Esm.Export.Support;
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
