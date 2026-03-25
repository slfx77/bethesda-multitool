using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Show;

/// <summary>
///     Show renderers for quest and dialogue record types: QUST, DIAL.
/// </summary>
internal static class QuestShowRenderer
{
    internal static bool TryShowQuest(RecordCollection records, FormIdResolver _,
        uint? formId, string? editorId)
    {
        var quest = records.Quests.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, q => q.FormId, q => q.EditorId));
        if (quest == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]   0x{quest.FormId:X8}",
            $"[cyan]EditorID:[/] {Markup.Escape(quest.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]     {Markup.Escape(quest.FullName ?? "(none)")}",
            $"[cyan]Priority:[/] {quest.Priority}",
            $"[cyan]Flags:[/]    0x{quest.Flags:X4}"
        };

        if (quest.Objectives is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("[bold]Objectives:[/]");
            foreach (var obj in quest.Objectives.OrderBy(o => o.Index))
            {
                lines.Add($"  [[{obj.Index}]] {Markup.Escape(obj.DisplayText ?? "(no text)")}");
            }
        }

        if (quest.Stages is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("[bold]Stages:[/]");
            foreach (var stage in quest.Stages.OrderBy(s => s.Index))
            {
                lines.Add($"  [[{stage.Index}]] Flags: 0x{stage.Flags:X2}");
            }
        }

        if (quest.Variables is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("[bold]Script Variables:[/]");
            foreach (var variable in quest.Variables)
            {
                lines.Add(
                    $"  {Markup.Escape(variable.Name ?? $"var_{variable.Index}")} ({variable.TypeName}, idx {variable.Index})");
            }
        }

        // Show associated script source/decompiled text inline
        var script = quest.Script is > 0
            ? records.Scripts.FirstOrDefault(s => s.FormId == quest.Script.Value)
            : null;
        if (script != null)
        {
            var scriptText = script.SourceText ?? script.DecompiledText;
            if (!string.IsNullOrEmpty(scriptText))
            {
                var label = script.SourceText != null ? "Source (SCTX)" : "Decompiled";
                lines.Add("");
                lines.Add($"[bold]Script ({Markup.Escape(script.EditorId ?? $"0x{script.FormId:X8}")}) — {label}:[/]");
                if (scriptText.Length > 3000)
                {
                    scriptText = scriptText[..3000] + "\n... (truncated)";
                }

                lines.Add(Markup.Escape(scriptText));
            }
            else
            {
                lines.Add("");
                lines.Add(
                    $"[cyan]Script:[/]  0x{script.FormId:X8} ({Markup.Escape(script.EditorId ?? "")}) — {script.CompiledSize} bytes compiled, no source");
            }
        }
        else if (quest.Script is > 0)
        {
            lines.Add("");
            lines.Add($"[cyan]Script:[/]  0x{quest.Script.Value:X8} (not parsed)");
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]QUST[/] {Markup.Escape(quest.EditorId ?? "")} — {Markup.Escape(quest.FullName ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }

    internal static bool TryShowDialogTopic(RecordCollection records, FormIdResolver resolver,
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
                var responseText = !string.IsNullOrEmpty(firstResponse)
                    ? Markup.Escape(firstResponse.Length > 80
                        ? firstResponse[..80] + "..."
                        : firstResponse)
                    : "(no text)";
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
