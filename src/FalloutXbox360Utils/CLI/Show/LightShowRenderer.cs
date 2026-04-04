using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Show;

internal sealed class LightShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
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

        if (light.FalloffExponent is not 0f)
        {
            lines.Add($"[cyan]Falloff:[/]     {light.FalloffExponent:F2}");
        }

        if (light.FOV is not 0f)
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

        if (light.Weight is not 0f)
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
