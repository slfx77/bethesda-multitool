using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Script;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Show;

internal sealed class QuestShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId, ShowRenderContext context)
    {
        var quest = records.Quests.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, q => q.FormId, q => q.EditorId));
        if (quest == null)
        {
            return false;
        }

        context.Console.WriteLine();
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

        var script = quest.Script is > 0
            ? records.Scripts.FirstOrDefault(s => s.FormId == quest.Script.Value)
            : null;
        if (quest.Variables is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("[bold]Script Variables:[/]");
            foreach (var variable in quest.Variables)
            {
                lines.Add(
                    $"  {Markup.Escape(ScriptVariableTypeResolver.FormatDeclaration(variable, script?.ReferencedObjects ?? []))} (idx {variable.Index})");
            }
        }

        // Show associated script source/decompiled text inline
        if (script != null)
        {
            // Labelled by ScriptSourceProvenance, as the SCPT renderer and the GECK script report do: a
            // BethesdaMultitool decompilation standing in for missing dump SCTX is never shown as source.
            var source = ScriptSourceProvenance.Classify(script, context.IsMemoryDumpInput);
            string? scriptText;
            string label;
            if (source.HasSourceText)
            {
                scriptText = script.SourceText;
                label = source.Label;
            }
            else
            {
                scriptText = script.DecompiledText;
                label = ScriptSourceProvenance.DecompiledTextLabel;
            }

            if (!string.IsNullOrEmpty(scriptText))
            {
                lines.Add("");
                lines.Add(
                    $"[bold]Script ({Markup.Escape(script.EditorId ?? $"0x{script.FormId:X8}")}) — {Markup.Escape(label)}:[/]");
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
                    $"[cyan]Script:[/]  0x{script.FormId:X8} ({Markup.Escape(script.EditorId ?? "")}) — {script.CompiledSize} bytes compiled; {Markup.Escape(source.Label)}");
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
        context.Console.Write(panel);
        return true;
    }
}
