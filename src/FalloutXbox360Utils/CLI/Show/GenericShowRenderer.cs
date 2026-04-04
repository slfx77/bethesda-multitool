using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Show;

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

        if (genericRecord?.ModelPath != null)
        {
            lines.Add($"[cyan]Model:[/]     {Markup.Escape(genericRecord.ModelPath)}");
        }

        if (genericRecord?.Fields is { Count: > 0 })
        {
            lines.Add("");
            ShowHelpers.AppendPdbFields(lines, genericRecord.Fields, resolver);
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]{Markup.Escape(match.Type)}[/] {Markup.Escape(match.EditorId ?? $"0x{match.FormId:X8}")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }
}
