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

internal sealed class LightShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver _,
        uint? formId, string? editorId)
    {
        var light = records.Lights.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, l => l.FormId, l => l.EditorId));
        if (light == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var r8 = (byte)(light.Color & 0xFF);
        var g8 = (byte)((light.Color >> 8) & 0xFF);
        var b8 = (byte)((light.Color >> 16) & 0xFF);
        var colorHex = $"#{r8:X2}{g8:X2}{b8:X2}";

        var lines = new List<string>
        {
            $"[cyan]FormID:[/]      0x{light.FormId:X8}",
            $"[cyan]EditorID:[/]    {Markup.Escape(light.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]        {Markup.Escape(light.FullName ?? "(none)")}",
            $"[cyan]Radius:[/]      {light.Radius}",
            $"[cyan]Color:[/]       {colorHex} ({r8}, {g8}, {b8})",
            $"[cyan]Duration:[/]    {(light.Duration == 0 ? "Infinite" : $"{light.Duration}s")}"
        };

        if (light.FalloffExponent != 0)
        {
            lines.Add($"[cyan]Falloff:[/]     {light.FalloffExponent:F2}");
        }

        if (light.FOV != 0)
        {
            lines.Add($"[cyan]FOV:[/]         {light.FOV:F1}\u00B0");
        }

        if (light.Flags != 0)
        {
            lines.Add($"[cyan]Flags:[/]       0x{light.Flags:X8}");
        }

        if (light.Value != 0)
        {
            lines.Add($"[cyan]Value:[/]       {light.Value}");
        }

        if (light.Weight != 0)
        {
            lines.Add($"[cyan]Weight:[/]      {light.Weight:F1}");
        }

        if (!string.IsNullOrEmpty(light.ModelPath))
        {
            lines.Add($"[cyan]Model:[/]       {Markup.Escape(light.ModelPath)}");
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader($"[bold]LIGH[/] {Markup.Escape(light.EditorId ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }
}

internal sealed class FurnitureShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var furn = records.Furniture.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, f => f.FormId, f => f.EditorId));
        if (furn == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]      0x{furn.FormId:X8}",
            $"[cyan]EditorID:[/]    {Markup.Escape(furn.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]        {Markup.Escape(furn.FullName ?? "(none)")}"
        };

        if (!string.IsNullOrEmpty(furn.ModelPath))
        {
            lines.Add($"[cyan]Model:[/]       {Markup.Escape(furn.ModelPath)}");
        }

        if (furn.MarkerFlags != 0)
        {
            lines.Add($"[cyan]Markers:[/]     0x{furn.MarkerFlags:X8}");
        }

        if (furn.Script.HasValue)
        {
            lines.Add($"[cyan]Script:[/]      {resolver.FormatWithEditorId(furn.Script.Value)}");
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader($"[bold]FURN[/] {Markup.Escape(furn.EditorId ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }
}

internal sealed class ActivatorShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var acti = records.Activators.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, a => a.FormId, a => a.EditorId));
        if (acti == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]      0x{acti.FormId:X8}",
            $"[cyan]EditorID:[/]    {Markup.Escape(acti.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]        {Markup.Escape(acti.FullName ?? "(none)")}"
        };

        if (!string.IsNullOrEmpty(acti.ModelPath))
        {
            lines.Add($"[cyan]Model:[/]       {Markup.Escape(acti.ModelPath)}");
        }

        if (acti.ActivationSoundFormId.HasValue)
        {
            lines.Add($"[cyan]Activ Sound:[/] {resolver.FormatWithEditorId(acti.ActivationSoundFormId.Value)}");
        }

        if (acti.RadioStationFormId.HasValue)
        {
            lines.Add($"[cyan]Radio:[/]       {resolver.FormatWithEditorId(acti.RadioStationFormId.Value)}");
        }

        if (acti.WaterTypeFormId.HasValue)
        {
            lines.Add($"[cyan]Water Type:[/]  {resolver.FormatWithEditorId(acti.WaterTypeFormId.Value)}");
        }

        if (acti.Script.HasValue)
        {
            lines.Add($"[cyan]Script:[/]      {resolver.FormatWithEditorId(acti.Script.Value)}");
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader($"[bold]ACTI[/] {Markup.Escape(acti.EditorId ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }
}

internal sealed class StaticShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver _,
        uint? formId, string? editorId)
    {
        var stat = records.Statics.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, s => s.FormId, s => s.EditorId));
        if (stat == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]      0x{stat.FormId:X8}",
            $"[cyan]EditorID:[/]    {Markup.Escape(stat.EditorId ?? "(none)")}"
        };

        if (!string.IsNullOrEmpty(stat.ModelPath))
        {
            lines.Add($"[cyan]Model:[/]       {Markup.Escape(stat.ModelPath)}");
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader($"[bold]STAT[/] {Markup.Escape(stat.EditorId ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }
}
