using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Presentation;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Show;

internal sealed class SharedRecordDetailShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver, uint? formId, string? editorId,
        ShowRenderContext context)
    {
        if (!RecordDetailPresenter.TryBuildForLookup(records, resolver, formId, editorId, out var model,
                context.IsMemoryDumpInput) ||
            model == null)
        {
            return false;
        }

        Render(context.Console, model, context.FullText);
        return true;
    }

    /// <summary>
    ///     Renders a <see cref="RecordDetailModel" /> to a Spectre panel. Shared so the profile-driven
    ///     renderer (<see cref="ProfileShowRenderer" />) presents schema-decoded records identically to the
    ///     typed ones.
    ///     <para>
    ///         With <paramref name="fullText" /> (<c>show --full</c>), any value that spans lines or is
    ///         longer than <see cref="ShowHelpers.DefaultTextCap" /> — script source, decompiled text,
    ///         dialogue and terminal text — is replaced in the panel by its length and written after the
    ///         panel as a verbatim block titled by its section and label, so it is neither folded at the
    ///         console width nor stripped of its TABs and CRLF. Short values stay in the panel. Without it
    ///         the output is unchanged.
    ///     </para>
    /// </summary>
    internal static void Render(IAnsiConsole console, RecordDetailModel model, bool fullText = false)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(model);

        var lines = new List<string>();
        var blocks = new List<VerbatimBlock>();
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
                        var listLabelShown = !string.Equals(entry.Label, section.Title, StringComparison.Ordinal);
                        if (listLabelShown)
                        {
                            lines.Add($"[cyan]{Markup.Escape(entry.Label)}:[/]");
                        }

                        if (entry.Items != null)
                        {
                            foreach (var item in entry.Items)
                            {
                                if (fullText && ShowHelpers.NeedsVerbatimBlock(item.Value))
                                {
                                    lines.Add(
                                        $"  {Markup.Escape(item.Label)}: {ShowHelpers.VerbatimPlaceholder(item.Value)}");
                                    blocks.Add(new VerbatimBlock(
                                        BlockTitle(section.Title, listLabelShown ? entry.Label : null, item.Label),
                                        item.Value));
                                    continue;
                                }

                                var value = string.IsNullOrEmpty(item.Value) ? "" : $": {Markup.Escape(item.Value)}";
                                lines.Add($"  {Markup.Escape(item.Label)}{value}");
                            }
                        }

                        break;

                    default:
                        var text = entry.Value ?? "(none)";
                        if (fullText && ShowHelpers.NeedsVerbatimBlock(text))
                        {
                            lines.Add($"[cyan]{Markup.Escape(entry.Label)}:[/] {ShowHelpers.VerbatimPlaceholder(text)}");
                            blocks.Add(new VerbatimBlock(BlockTitle(section.Title, null, entry.Label), text));
                            break;
                        }

                        lines.Add($"[cyan]{Markup.Escape(entry.Label)}:[/] {Markup.Escape(text)}");
                        break;
                }
            }
        }

        console.WriteLine();
        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]{Markup.Escape(model.RecordSignature)}[/] {Markup.Escape(model.EditorId ?? "")} — " +
                $"{Markup.Escape(model.DisplayName ?? $"0x{model.FormId:X8}")}")
        };
        console.Write(panel);
        ShowHelpers.WriteVerbatimBlocks(console, blocks);
    }

    /// <summary>
    ///     "Section / list / label" for a verbatim block, dropping a part that repeats the one before it,
    ///     so each block names where its placeholder sits in the panel.
    /// </summary>
    private static string BlockTitle(string sectionTitle, string? listLabel, string label)
    {
        var parts = new List<string> { sectionTitle };
        foreach (var part in new[] { listLabel, label })
        {
            if (!string.IsNullOrEmpty(part) && !string.Equals(part, parts[^1], StringComparison.Ordinal))
            {
                parts.Add(part);
            }
        }

        return string.Join(" / ", parts);
    }
}
