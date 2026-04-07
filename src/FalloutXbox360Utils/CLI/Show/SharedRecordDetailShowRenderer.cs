using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Presentation;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Show;

internal sealed class SharedRecordDetailShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver, uint? formId, string? editorId)
    {
        if (!RecordDetailPresenter.TryBuildForLookup(records, resolver, formId, editorId, out var model) ||
            model == null)
        {
            return false;
        }

        var lines = new List<string>();
        foreach (var section in model.Sections)
        {
            if (lines.Count > 0)
            {
                lines.Add("");
            }

            lines.Add($"[bold]{Markup.Escape(section.Title)}:[/]");
            foreach (var entry in section.Entries)
            {
                switch (entry.Kind)
                {
                    case RecordDetailEntryKind.List:
                        if (!string.Equals(entry.Label, section.Title, StringComparison.Ordinal))
                        {
                            lines.Add($"[cyan]{Markup.Escape(entry.Label)}:[/]");
                        }
                        if (entry.Items != null)
                        {
                            foreach (var item in entry.Items)
                            {
                                var value = string.IsNullOrEmpty(item.Value) ? "" : $": {Markup.Escape(item.Value)}";
                                lines.Add($"  {Markup.Escape(item.Label)}{value}");
                            }
                        }

                        break;

                    default:
                        lines.Add($"[cyan]{Markup.Escape(entry.Label)}:[/] {Markup.Escape(entry.Value ?? "(none)")}");
                        break;
                }
            }
        }

        AnsiConsole.WriteLine();
        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]{Markup.Escape(model.RecordSignature)}[/] {Markup.Escape(model.EditorId ?? "")} — " +
                $"{Markup.Escape(model.DisplayName ?? $"0x{model.FormId:X8}")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }
}
