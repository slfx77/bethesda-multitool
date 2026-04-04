using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Show;

internal sealed class MessageShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var msg = records.Messages.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, m => m.FormId, m => m.EditorId));
        if (msg == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
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
            lines.Add($"[cyan]Quest:[/]     {resolver.FormatWithEditorId(msg.QuestFormId)}");
        }

        if (!string.IsNullOrEmpty(msg.Description))
        {
            var text = msg.Description.Length > 2000
                ? msg.Description[..2000] + "\n... (truncated)"
                : msg.Description;
            lines.Add("");
            lines.Add("[bold]Text:[/]");
            lines.Add(Markup.Escape(text));
        }

        if (msg.Buttons.Count > 0)
        {
            lines.Add("");
            lines.Add($"[bold]Buttons ({msg.Buttons.Count}):[/]");
            for (var i = 0; i < msg.Buttons.Count; i++)
            {
                lines.Add($"  [{i + 1}] {Markup.Escape(msg.Buttons[i])}");
            }
        }

        if (!string.IsNullOrEmpty(msg.Icon))
        {
            lines.Add($"[cyan]Icon:[/]      {Markup.Escape(msg.Icon)}");
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]MESG[/] {Markup.Escape(msg.EditorId ?? "")} — {Markup.Escape(msg.FullName ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }
}
