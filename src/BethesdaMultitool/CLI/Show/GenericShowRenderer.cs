using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Export;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Show;

/// <summary>
///     Fallback renderer for record types without a dedicated show renderer.
///     Must be last in the renderer chain — relies on break-on-first-match in ShowCommand.
/// </summary>
internal sealed class GenericShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var flat = RecordFlattener.Flatten(records);
        var match = flat.FirstOrDefault(r =>
            (formId.HasValue && r.FormId == formId.Value) ||
            (editorId != null && (r.EditorId?.Equals(editorId, StringComparison.OrdinalIgnoreCase) ?? false)));

        if (match == null)
        {
            return false;
        }

        var genericRecord = records.GenericRecords
            .FirstOrDefault(r => r.FormId == match.FormId);

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]    0x{match.FormId:X8}",
            $"[cyan]Type:[/]      {Markup.Escape(match.Type)}",
            $"[cyan]EditorID:[/]  {Markup.Escape(match.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]      {Markup.Escape(match.DisplayName ?? "(none)")}"
        };

        // Schema-decoded games (Oblivion/Skyrim/FO4/FO76/Morrowind) carry the full decoded structure on
        // DecodedTree; render it so every subrecord is visible (e.g. an Oblivion TREE's ICON/SNAM/CNAM/BNAM).
        // The tree already includes the model, so skip the standalone Model line to avoid duplicating it.
        if (genericRecord?.DecodedTree is { Count: > 0 } tree)
        {
            lines.Add("");
            AppendDecodedTree(lines, tree, 0);
        }
        else
        {
            if (genericRecord?.ModelPath != null)
            {
                lines.Add($"[cyan]Model:[/]     {Markup.Escape(genericRecord.ModelPath)}");
            }

            if (genericRecord?.Fields is { Count: > 0 })
            {
                lines.Add("");
                ShowHelpers.AppendPdbFields(lines, genericRecord.Fields, resolver);
            }
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]{Markup.Escape(match.Type)}[/] {Markup.Escape(match.EditorId ?? $"0x{match.FormId:X8}")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }

    internal static void AppendDecodedTree(List<string> lines, IReadOnlyList<DecodedNode> nodes, int depth)
    {
        var indent = new string(' ', depth * 2);
        foreach (var node in nodes)
        {
            var sig = node.Signature is { Length: > 0 } s ? $" [grey]({s})[/]" : "";
            var raw = node.IsRaw ? " [grey]raw[/]" : "";

            if (node.Children is { Count: > 0 })
            {
                lines.Add($"{indent}[cyan]{Markup.Escape(node.Label)}[/]{sig}{raw}");
                AppendDecodedTree(lines, node.Children, depth + 1);
            }
            else
            {
                var value = node.Value ?? (node.FormId is { } f ? $"0x{f:X8}" : "(empty)");
                lines.Add($"{indent}[cyan]{Markup.Escape(node.Label)}:[/]{sig} {Markup.Escape(value)}{raw}");
            }
        }
    }
}

