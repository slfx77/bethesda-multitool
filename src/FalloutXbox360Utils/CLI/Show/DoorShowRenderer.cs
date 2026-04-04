using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Show;

internal sealed class DoorShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var door = records.Doors.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, d => d.FormId, d => d.EditorId));
        if (door == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]      0x{door.FormId:X8}",
            $"[cyan]EditorID:[/]    {Markup.Escape(door.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]        {Markup.Escape(door.FullName ?? "(none)")}"
        };

        if (!string.IsNullOrEmpty(door.ModelPath))
        {
            lines.Add($"[cyan]Model:[/]       {Markup.Escape(door.ModelPath)}");
        }

        if (door.Flags != 0)
        {
            lines.Add($"[cyan]Flags:[/]       0x{door.Flags:X2}");
        }

        if (door.OpenSoundFormId.HasValue)
        {
            lines.Add($"[cyan]Open Sound:[/]  {resolver.FormatWithEditorId(door.OpenSoundFormId.Value)}");
        }

        if (door.CloseSoundFormId.HasValue)
        {
            lines.Add($"[cyan]Close Sound:[/] {resolver.FormatWithEditorId(door.CloseSoundFormId.Value)}");
        }

        if (door.LoopSoundFormId.HasValue)
        {
            lines.Add($"[cyan]Loop Sound:[/]  {resolver.FormatWithEditorId(door.LoopSoundFormId.Value)}");
        }

        if (door.Script.HasValue)
        {
            lines.Add($"[cyan]Script:[/]      {resolver.FormatWithEditorId(door.Script.Value)}");
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader($"[bold]DOOR[/] {Markup.Escape(door.EditorId ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }
}
