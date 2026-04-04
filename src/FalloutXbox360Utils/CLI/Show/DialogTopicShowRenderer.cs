using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Show;

internal sealed class DialogTopicShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var topic = records.DialogTopics.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, t => t.FormId, t => t.EditorId));
        if (topic == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var questStr = topic.QuestFormId.HasValue
            ? resolver.FormatWithEditorId(topic.QuestFormId.Value)
            : "(none)";
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]     0x{topic.FormId:X8}",
            $"[cyan]EditorID:[/]   {Markup.Escape(topic.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]       {Markup.Escape(topic.FullName ?? "(none)")}",
            $"[cyan]Type:[/]       {topic.TopicTypeName}",
            $"[cyan]Quest:[/]      {questStr}",
            $"[cyan]Responses:[/]  {topic.ResponseCount}",
            $"[cyan]Flags:[/]      0x{topic.Flags:X2}"
        };

        // Find INFO records that reference this topic
        var infos = records.Dialogues
            .Where(d => d.TopicFormId == topic.FormId)
            .Take(20)
            .ToList();

        if (infos.Count > 0)
        {
            lines.Add("");
            lines.Add($"[bold]INFO records ({infos.Count}):[/]");

            foreach (var info in infos)
            {
                var speaker = info.SpeakerFormId.HasValue && info.SpeakerFormId.Value != 0
                    ? resolver.FormatWithEditorId(info.SpeakerFormId.Value)
                    : "(unknown)";
                var firstResponse = info.Responses.FirstOrDefault()?.Text;
                string responseText;
                if (!string.IsNullOrEmpty(firstResponse))
                {
                    var truncated = firstResponse.Length > 80
                        ? firstResponse[..80] + "..."
                        : firstResponse;
                    responseText = Markup.Escape(truncated);
                }
                else
                {
                    responseText = "(no text)";
                }

                lines.Add($"  0x{info.FormId:X8} [[{Markup.Escape(speaker)}]]: {responseText}");
            }
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]DIAL[/] {Markup.Escape(topic.EditorId ?? "")} — {Markup.Escape(topic.FullName ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }
}
