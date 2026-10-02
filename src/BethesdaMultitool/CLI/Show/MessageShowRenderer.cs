using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Show;

internal sealed class MessageShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId, ShowRenderContext context)
    {
        var msg = records.Messages.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, m => m.FormId, m => m.EditorId));
        if (msg == null)
        {
            return false;
        }

        context.Console.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]    0x{msg.FormId:X8}",
            $"[cyan]EditorID:[/]  {Markup.Escape(msg.EditorId ?? "(none)")}",
            $"[cyan]Title:[/]     {Markup.Escape(msg.FullName ?? "(none)")}"
        };

        var flags = new List<string>();
        if (msg.IsMessageBox)
        {
            flags.Add("Message Box");
        }

        if (msg.IsAutoDisplay)
        {
            flags.Add("Auto Display");
        }

        if (flags.Count > 0)
        {
            lines.Add($"[cyan]Flags:[/]     {string.Join(", ", flags)}");
        }

        if (msg.DisplayTime != 0)
        {
            lines.Add($"[cyan]Display:[/]   {msg.DisplayTime} seconds");
        }

        if (msg.QuestFormId != 0)
        {
            lines.Add($"[cyan]Quest:[/]     {ShowHelpers.Ref(resolver, msg.QuestFormId)}");
        }

        // Under --full the text is written after the panel exactly as stored (see WriteVerbatimBlocks);
        // by default it is cut at the cap with a marker naming the total and --full.
        var blocks = new List<VerbatimBlock>();
        if (!string.IsNullOrEmpty(msg.Description))
        {
            lines.Add("");
            if (context.FullText)
            {
                lines.Add($"[bold]Text:[/] {ShowHelpers.VerbatimPlaceholder(msg.Description)}");
                blocks.Add(new VerbatimBlock("Message text", msg.Description));
            }
            else
            {
                lines.Add("[bold]Text:[/]");
                lines.Add(Markup.Escape(ShowHelpers.TruncateForPanel(msg.Description)));
            }
        }

        lines.AddRange(RuntimeMessageEvidenceFormatter.FormatSelectedSources(msg).Select(Markup.Escape));
        if (msg.ButtonSource == MessageFieldSource.RuntimeObject || msg.RuntimeButtons is null)
            lines.AddRange(RuntimeMessageEvidenceFormatter.Format(msg.RuntimeEvidence).Select(Markup.Escape));
        var conditions = ConditionDisplayContext.From(records, resolver);
        if (msg.UnassignedConditions.Count > 0)
        {
            lines.Add("");
            lines.Add("[bold]Unassigned conditions (before first button; ownership unknown):[/]");
            lines.AddRange(MessageConditionFormatter.Format(msg.UnassignedConditions, conditions)
                .Select(line => "  " + Markup.Escape(line)));
        }

        if (msg.Buttons.Count > 0)
        {
            lines.Add("");
            lines.Add($"[bold]Buttons ({msg.Buttons.Count}):[/]");
            for (var i = 0; i < msg.Buttons.Count; i++)
            {
                // Doubled brackets: a bare [N] with N in 0..255 is a Spectre colour-number tag, so
                // "[1]" would open a style that is never closed and the panel would throw.
                lines.Add($"  [[{i + 1}]] {Markup.Escape(msg.Buttons[i])}");
                lines.AddRange(MessageConditionFormatter.Format(msg.GetButtonConditions(i), conditions)
                    .Select(line => "    " + Markup.Escape(line)));
            }
        }

        lines.AddRange(RuntimeMessageEvidenceFormatter.FormatAlternative(msg, conditions).Select(Markup.Escape));

        if (!string.IsNullOrEmpty(msg.Icon))
        {
            lines.Add($"[cyan]Icon:[/]      {Markup.Escape(msg.Icon)}");
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]MESG[/] {Markup.Escape(msg.EditorId ?? "")} — {Markup.Escape(msg.FullName ?? "")}")
        };
        context.Console.Write(panel);
        ShowHelpers.WriteVerbatimBlocks(context.Console, blocks);
        return true;
    }
}
